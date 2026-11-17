using System.Net;
using BraviaTheatre.Core.Discovery;

namespace BraviaTheatre.Tests;

/// <summary>
/// The subnet probe swept any private network the PC was on, every round, always from the same
/// address. These pin where it may sweep, where a sweep resumes, and when sweeping pauses.
/// </summary>
public sealed class SubnetProbePlanTests
{
    private static readonly ActiveIPv4Interface Home = Interface("192.168.1.20", "255.255.255.0");
    private static readonly ActiveIPv4Interface Vpn = Interface("10.8.0.6", "255.255.255.0");

    [Fact]
    public void WithoutAKnownNetworkEveryEligibleInterfaceMayBeSwept()
    {
        var plan = new SubnetProbePlan(null, new ManualTimeProvider(), () => [Home, Vpn]);

        Assert.Equal([Home, Vpn], plan.SelectInterfaces([Home, Vpn]));
    }

    [Fact]
    public void OnceTheSoundbarsNetworkIsKnownOnlyThatNetworkIsSwept()
    {
        var plan = new SubnetProbePlan("192.168.1.0/24", new ManualTimeProvider(), () => [Home, Vpn]);

        Assert.Equal([Home], plan.SelectInterfaces([Home, Vpn]));
        // A café or office network: nothing is swept there, mDNS still runs.
        Assert.Empty(plan.SelectInterfaces([Vpn]));
    }

    [Fact]
    public void ConnectingToASoundbarRemembersTheNetworkOfTheInterfaceItIsOn()
    {
        var plan = new SubnetProbePlan(null, new ManualTimeProvider(), () => [Vpn, Home]);
        var reported = new List<string>();
        plan.SoundbarNetworkChanged += reported.Add;

        plan.RecordSoundbar("192.168.1.77");
        plan.RecordSoundbar("192.168.1.77");
        plan.RecordSoundbar("203.0.113.9");

        Assert.Equal(["192.168.1.0/24"], reported);
        Assert.Equal("192.168.1.0/24", plan.SoundbarNetwork);
        Assert.Equal([Home], plan.SelectInterfaces([Home, Vpn]));
    }

    [Fact]
    public void EachSweepContinuesWhereTheLastOneStopped()
    {
        var plan = new SubnetProbePlan(null, new ManualTimeProvider(), () => []);
        var candidates = Addresses(10);

        Assert.Equal(candidates, plan.BeginSweep(candidates));
        plan.EndSweep(10, firstUnfinished: 4, found: false);

        Assert.Equal(candidates.Skip(4).Concat(candidates.Take(4)), plan.BeginSweep(candidates));
        plan.EndSweep(10, firstUnfinished: 3, found: false);

        Assert.Equal(candidates.Skip(7).Concat(candidates.Take(7)), plan.BeginSweep(candidates));
    }

    [Fact]
    public void ADifferentCandidateListStartsFromItsNearestAddress()
    {
        var plan = new SubnetProbePlan(null, new ManualTimeProvider(), () => []);
        plan.BeginSweep(Addresses(10));
        plan.EndSweep(10, firstUnfinished: 4, found: false);

        var other = Addresses(6);
        Assert.Equal(other, plan.BeginSweep(other));
    }

    [Fact]
    public void AFullPassWithoutASoundbarPausesSweepingLongerEachTime()
    {
        var clock = new ManualTimeProvider();
        var plan = new SubnetProbePlan(null, clock, () => []);
        var candidates = Addresses(10);

        plan.BeginSweep(candidates);
        plan.EndSweep(10, firstUnfinished: 6, found: false);
        Assert.NotEmpty(plan.SelectInterfaces([Home]));

        plan.BeginSweep(candidates);
        plan.EndSweep(10, firstUnfinished: 4, found: false);
        Assert.Empty(plan.SelectInterfaces([Home]));

        clock.Advance(SubnetProbePlan.FirstPause);
        Assert.NotEmpty(plan.SelectInterfaces([Home]));

        plan.BeginSweep(candidates);
        plan.EndSweep(10, firstUnfinished: 10, found: false);
        clock.Advance(SubnetProbePlan.FirstPause);
        Assert.Empty(plan.SelectInterfaces([Home]));
        clock.Advance(SubnetProbePlan.FirstPause);
        Assert.NotEmpty(plan.SelectInterfaces([Home]));
    }

    [Fact]
    public void ThePauseStopsGrowingAtTheLongest()
    {
        var clock = new ManualTimeProvider();
        var plan = new SubnetProbePlan(null, clock, () => []);
        var candidates = Addresses(4);

        for (var miss = 0; miss < 8; miss++)
        {
            plan.BeginSweep(candidates);
            plan.EndSweep(4, firstUnfinished: 4, found: false);
            clock.Advance(SubnetProbePlan.LongestPause);
            Assert.NotEmpty(plan.SelectInterfaces([Home]));
        }
    }

    [Fact]
    public void FindingTheSoundbarLiftsThePauseAndResetsItsLength()
    {
        var clock = new ManualTimeProvider();
        var plan = new SubnetProbePlan(null, clock, () => [Home]);
        var candidates = Addresses(4);
        for (var miss = 0; miss < 3; miss++)
        {
            plan.BeginSweep(candidates);
            plan.EndSweep(4, firstUnfinished: 4, found: false);
            clock.Advance(SubnetProbePlan.LongestPause);
        }

        plan.BeginSweep(candidates);
        plan.EndSweep(4, firstUnfinished: 4, found: false);
        Assert.Empty(plan.SelectInterfaces([Home]));

        plan.RecordSoundbar("192.168.1.30");
        Assert.NotEmpty(plan.SelectInterfaces([Home]));

        plan.BeginSweep(candidates);
        plan.EndSweep(4, firstUnfinished: 4, found: false);
        clock.Advance(SubnetProbePlan.FirstPause);
        Assert.NotEmpty(plan.SelectInterfaces([Home]));
    }

    [Theory]
    [InlineData("192.168.1.77/24", "192.168.1.0/24")]
    [InlineData(" 10.1.2.3/16 ", "10.1.0.0/16")]
    [InlineData("172.16.5.4/32", "172.16.5.4/32")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("192.168.1.0", null)]
    [InlineData("192.168.1.0/0", null)]
    [InlineData("192.168.1.0/33", null)]
    [InlineData("192.168.1.0/-8", null)]
    [InlineData("fe80::1/64", null)]
    [InlineData("not a network/24", null)]
    public void StoredNetworksAreReadInCanonicalFormOrIgnored(string? stored, string? expected)
    {
        Assert.Equal(expected, SubnetProbePlan.NormalizeNetwork(stored));
        Assert.Equal(expected, new SubnetProbePlan(stored).SoundbarNetwork);
    }

    [Fact]
    public void NonContiguousMasksDescribeNoNetwork()
    {
        Assert.Null(SubnetProbePlan.DescribeNetwork(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.0.255.0")));
        Assert.Null(SubnetProbePlan.DescribeNetwork(IPAddress.Any, IPAddress.Any));
        Assert.Equal("192.168.0.0/23", SubnetProbePlan.DescribeNetwork(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.254.0")));
    }

    [Fact]
    public async Task ASweepThatRunsOutOfTimeReportsTheFirstProbeThatDidNotFinish()
    {
        // Unused addresses do not answer, so their probes run until discovery's time is up.
        var candidates = Addresses(10);
        var waiting = 0;
        var bothWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeUp = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new SweepProgress();

        async Task<bool> ProbeAsync(IPAddress address, int port, CancellationToken cancellationToken)
        {
            if (address.GetAddressBytes()[3] <= 3) return false;
            if (Interlocked.Increment(ref waiting) == 2) bothWaiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return false;
        }

        var sweep = MdnsDiscovery.ProbeCandidatesAsync(candidates, 55051, maxConcurrency: 2, ProbeAsync, timeUp.Token, progress);
        await bothWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        timeUp.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep);
        Assert.Equal(3, progress.FirstUnfinished);
    }

    [Fact]
    public async Task ACompletedSweepReportsEveryCandidateFinished()
    {
        var candidates = Addresses(7);
        var progress = new SweepProgress();

        var device = await MdnsDiscovery.ProbeCandidatesAsync(
            candidates, 55051, maxConcurrency: 3, static (_, _, _) => Task.FromResult(false),
            TestContext.Current.CancellationToken, progress);

        Assert.Null(device);
        Assert.Equal(7, progress.FirstUnfinished);
    }

    private static IPAddress[] Addresses(int count) =>
        Enumerable.Range(1, count).Select(index => IPAddress.Parse($"192.168.1.{index}")).ToArray();

    private static ActiveIPv4Interface Interface(string address, string mask) =>
        new(IPAddress.Parse(address), IPAddress.Parse(mask), ScanEligible: true);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _timestamp, by.Ticks);
    }
}
