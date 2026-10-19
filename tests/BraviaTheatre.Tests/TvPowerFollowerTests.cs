using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BraviaTheatre.Core;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Tv;

namespace BraviaTheatre.Tests;

public class TvPowerFollowerTests
{
    private const TvPowerReading On = TvPowerReading.On;
    private const TvPowerReading Off = TvPowerReading.NotOn;

    [Theory]
    [InlineData("""{"device":{"PowerState":"on"}}""", TvPowerReading.On)]
    [InlineData("""{"device":{"PowerState":" On "}}""", TvPowerReading.On)]
    [InlineData("""{"device":{"PowerState":"standby"}}""", TvPowerReading.NotOn)]
    // Sets that have been off for a while report an empty string; reading it as on woke
    // other integrations' devices for a TV that was off.
    [InlineData("""{"device":{"PowerState":""}}""", TvPowerReading.NotOn)]
    [InlineData("""{"device":{"name":"TV"}}""", TvPowerReading.NotOn)]
    [InlineData("""{"device":{"PowerState":true}}""", TvPowerReading.NotOn)]
    [InlineData("""{"device":"on"}""", TvPowerReading.NotOn)]
    [InlineData("""[]""", TvPowerReading.NotOn)]
    [InlineData("""not json""", TvPowerReading.NotOn)]
    public void OnlyAnExplicitOnCountsAsOn(string json, TvPowerReading expected)
    {
        Assert.Equal(expected, SamsungTvProbe.ParsePowerState(json));
    }

    [Theory]
    [InlineData("http://192.168.1.40")]
    [InlineData("192.168.1.40:8001")]
    [InlineData("")]
    public void ProbeRejectsHostsThatAreNotBareAddresses(string host)
    {
        Assert.Throws<ArgumentException>(() => new SamsungTvProbe(host));
    }

    [Fact]
    public async Task TvTurningOnTurnsTheSoundbarOnOnce()
    {
        var bar = new FakeSoundbar { Power = false };
        await RunAsync([Off, Off, On, On, On, On, On], bar);

        Assert.Equal(1, bar.TurnOnRequests);
        Assert.True(bar.Power);
    }

    [Fact]
    public async Task StartingWhileTheTvIsOnDoesNothing()
    {
        var bar = new FakeSoundbar { Power = false };
        await RunAsync([On, On, On, On], bar);

        Assert.Equal(0, bar.TurnOnRequests);
    }

    [Fact]
    public async Task OneOddReadingDoesNotWakeTheSoundbar()
    {
        var bar = new FakeSoundbar { Power = false };
        await RunAsync([Off, Off, On, Off, On, Off, Off], bar);

        Assert.Equal(0, bar.TurnOnRequests);
    }

    [Fact]
    public async Task OneMissedReadingWhileTheTvIsOnDoesNotWakeTheSoundbarAgain()
    {
        // The user turned the soundbar off while watching; a single dropped poll must not
        // look like the TV turning off and on again.
        var bar = new FakeSoundbar { Power = false };
        await RunAsync([Off, Off, On, On, On, Off, On, On], bar, afterWake: () => bar.Power = false);

        Assert.Equal(1, bar.TurnOnRequests);
    }

    [Fact]
    public async Task TurningTheSoundbarOffWhileTheTvStaysOnIsLeftAlone()
    {
        var bar = new FakeSoundbar { Power = false };
        await RunAsync([Off, Off, On, On, On, On, On, On], bar, afterWake: () => bar.Power = false);

        Assert.Equal(1, bar.TurnOnRequests);
        Assert.False(bar.Power);
    }

    [Fact]
    public async Task EachTvPowerOnWakesTheSoundbar()
    {
        var bar = new FakeSoundbar { Power = false };
        await RunAsync([Off, Off, On, On, Off, Off, On, On], bar, afterWake: () => bar.Power = false);

        Assert.Equal(2, bar.TurnOnRequests);
    }

    [Fact]
    public async Task ASoundbarThatIsAlreadyOnGetsNoCommand()
    {
        var bar = new FakeSoundbar { Power = true };
        await RunAsync([Off, Off, On, On], bar);

        Assert.Equal(0, bar.TurnOnRequests);
    }

    [Fact]
    public async Task WaitsForTheSoundbarConnectionBeforeTurningItOn()
    {
        // After the PC resumes, the TV can report on before the soundbar connection is back.
        var bar = new FakeSoundbar { Power = false, Connected = false };
        var checks = 0;
        bar.OnPowerRead = () =>
        {
            if (++checks == 4) bar.Connected = true;
        };

        await RunAsync([Off, Off, On, On], bar);

        Assert.Equal(1, bar.TurnOnRequests);
        Assert.Equal(4, checks);
    }

    [Fact]
    public async Task GivesUpAfterTheWakeWindowAndSaysSo()
    {
        var bar = new FakeSoundbar { Power = false, Connected = false };
        var logs = new List<(string Message, DiagnosticLevel Level)>();

        await RunAsync([Off, Off, On, On], bar, log: (message, level) => logs.Add((message, level)));

        Assert.Equal(0, bar.TurnOnRequests);
        Assert.Equal(TvPowerFollower.WakeAttempts, bar.PowerReads);
        Assert.Contains(logs, entry => entry.Level == DiagnosticLevel.Critical && entry.Message.Contains("could not be reached"));
    }

    [Fact]
    public async Task ARefusedCommandIsRetried()
    {
        var bar = new FakeSoundbar { Power = false, RefuseRequests = 2 };
        await RunAsync([Off, Off, On, On], bar);

        Assert.Equal(3, bar.TurnOnRequests);
        Assert.True(bar.Power);
    }

    [Fact]
    public async Task AFailingReadCountsAsNotOnAndPollingContinues()
    {
        var bar = new FakeSoundbar { Power = false };
        var readings = new Queue<Func<TvPowerReading>>(
        [
            () => throw new InvalidOperationException("boom"),
            () => Off,
            () => On,
            () => On
        ]);

        await RunCoreAsync(readings, bar, afterWake: null, log: null);

        Assert.Equal(1, bar.TurnOnRequests);
    }

    [Fact]
    public async Task EngineSetPowerSendsOnlyWhenTheStateDiffers()
    {
        var client = new EngineRegressionTests.FakeBraviaClient
        {
            StateHandler = static (_, _) => Task.FromResult(new Dictionary<string, object?>
            {
                ["power"] = false,
                ["volume"] = 10
            })
        };
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => client,
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        engine.StateChanged += state =>
        {
            if (state.Connected) connected.TrySetResult(true);
        };

        try
        {
            engine.Start();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.False(await engine.SetPowerAsync(false));
            Assert.True(await engine.SetPowerAsync(true));
            Assert.False(await engine.SetPowerAsync(true));

            await client.CommandReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { ("power", (object?)true) }, client.CommandValues.ToArray());
            Assert.True(engine.CurrentState.Power);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    private static Task RunAsync(
        TvPowerReading[] readings,
        FakeSoundbar bar,
        Action? afterWake = null,
        Action<string, DiagnosticLevel>? log = null) =>
        RunCoreAsync(new Queue<Func<TvPowerReading>>(readings.Select(r => (Func<TvPowerReading>)(() => r))), bar, afterWake, log);

    private static async Task RunCoreAsync(
        Queue<Func<TvPowerReading>> readings,
        FakeSoundbar bar,
        Action? afterWake,
        Action<string, DiagnosticLevel>? log)
    {
        using var cts = new CancellationTokenSource();
        bar.AfterWake = afterWake;

        var follower = new TvPowerFollower(
            _ =>
            {
                if (readings.Count == 0)
                {
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                }

                return Task.FromResult(readings.Dequeue()());
            },
            bar.ReadPower,
            bar.TurnOnAsync,
            static (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            log);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower.RunAsync(cts.Token));
    }

    private sealed class FakeSoundbar
    {
        public bool Power { get; set; }
        public bool Connected { get; set; } = true;
        public int RefuseRequests { get; set; }
        public int TurnOnRequests { get; private set; }
        public int PowerReads { get; private set; }
        public Action? OnPowerRead { get; set; }
        public Action? AfterWake { get; set; }

        public bool? ReadPower()
        {
            PowerReads++;
            OnPowerRead?.Invoke();
            return Connected ? Power : null;
        }

        public Task<bool> TurnOnAsync()
        {
            TurnOnRequests++;
            if (RefuseRequests > 0)
            {
                RefuseRequests--;
                return Task.FromResult(false);
            }

            Power = true;
            AfterWake?.Invoke();
            return Task.FromResult(true);
        }
    }
}
