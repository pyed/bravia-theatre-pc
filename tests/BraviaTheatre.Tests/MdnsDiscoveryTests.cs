using System.Net;
using System.Text;
using BraviaTheatre.Core.Discovery;

namespace BraviaTheatre.Tests;

public sealed class MdnsDiscoveryTests
{
    [Fact]
    public void BuildSubnetCandidatesUsesTheActualMaskAndExcludesReservedAddresses()
    {
        var candidates = MdnsDiscovery.BuildSubnetCandidates(
            IPAddress.Parse("192.168.50.4"),
            IPAddress.Parse("255.255.255.248"),
            maximumCandidates: 32);

        Assert.Equal(
            new[]
            {
                "192.168.50.3",
                "192.168.50.5",
                "192.168.50.2",
                "192.168.50.6",
                "192.168.50.1"
            },
            candidates.Select(static address => address.ToString()));
    }

    [Fact]
    public void BuildSubnetCandidatesRejectsInvalidOrHostOnlyMasks()
    {
        Assert.Empty(MdnsDiscovery.BuildSubnetCandidates(
            IPAddress.Parse("192.168.50.4"),
            IPAddress.Parse("255.0.255.0"),
            maximumCandidates: 32));

        Assert.Empty(MdnsDiscovery.BuildSubnetCandidates(
            IPAddress.Parse("192.168.50.4"),
            IPAddress.Parse("255.255.255.254"),
            maximumCandidates: 32));
    }

    [Fact]
    public void BuildSubnetCandidatesHonorsTheCandidateLimit()
    {
        var candidates = MdnsDiscovery.BuildSubnetCandidates(
            IPAddress.Parse("10.20.30.40"),
            IPAddress.Parse("255.255.0.0"),
            maximumCandidates: 7);

        Assert.Equal(7, candidates.Count);
        Assert.DoesNotContain(IPAddress.Parse("10.20.30.40"), candidates);
    }

    [Fact]
    public void ParseResponseResolvesCompressedPtrSrvTxtAndAddressRecords()
    {
        var response = BuildMdnsResponse(
            serviceType: "_sonysmarthome._tcp.local.",
            instanceLabel: "Synthetic Theatre",
            target: "synthetic-theatre.local.",
            address: IPAddress.Parse("192.168.50.42"),
            port: 55052,
            displayName: "Living Room");

        var device = MdnsDiscovery.ParseResponse(response, IPAddress.Parse("192.168.50.2"));

        Assert.NotNull(device);
        Assert.Equal("192.168.50.42", device.Host);
        Assert.Equal(55052, device.Port);
        Assert.Equal("Living Room", device.Name);
    }

    [Fact]
    public void ParseResponseRejectsUnrelatedServicesAndMalformedCompression()
    {
        var unrelated = BuildMdnsResponse(
            serviceType: "_http._tcp.local.",
            instanceLabel: "Web Server",
            target: "web.local.",
            address: IPAddress.Parse("192.168.50.99"),
            port: 80,
            displayName: "Not a soundbar");

        Assert.Null(MdnsDiscovery.ParseResponse(unrelated, IPAddress.Parse("192.168.50.99")));

        var pointerLoop = new byte[]
        {
            0x00, 0x00, 0x84, 0x00, 0x00, 0x00,
            0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            0xC0, 0x0C
        };
        var exception = Record.Exception(() =>
            MdnsDiscovery.ParseResponse(pointerLoop, IPAddress.Loopback));

        Assert.Null(exception);
        Assert.Null(MdnsDiscovery.ParseResponse(pointerLoop, IPAddress.Loopback));
    }

    [Fact]
    public async Task ProbeCandidatesIsBoundedAndReturnsOnlyAFingerprintedDevice()
    {
        var candidates = Enumerable.Range(1, 12)
            .Select(index => IPAddress.Parse($"10.0.0.{index}"))
            .ToArray();
        var expected = candidates[7];
        var active = 0;
        var maximumActive = 0;

        async Task<bool> ProbeAsync(IPAddress address, int port, CancellationToken cancellationToken)
        {
            Assert.Equal(55051, port);
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, current);
            try
            {
                await Task.Delay(10, cancellationToken);
                return address.Equals(expected);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        var device = await MdnsDiscovery.ProbeCandidatesAsync(
            candidates,
            port: 55051,
            maxConcurrency: 3,
            ProbeAsync,
            TestContext.Current.CancellationToken);

        Assert.NotNull(device);
        Assert.Equal(expected.ToString(), device.Host);
        Assert.InRange(maximumActive, 1, 3);
    }

    [Fact]
    public async Task ProbeCandidatesRejectsOpenPortsThatFailTheFingerprint()
    {
        var candidates = new[]
        {
            IPAddress.Parse("10.0.0.1"),
            IPAddress.Parse("10.0.0.2")
        };

        var device = await MdnsDiscovery.ProbeCandidatesAsync(
            candidates,
            port: 55051,
            maxConcurrency: 2,
            static (_, _, _) => Task.FromResult(false),
            TestContext.Current.CancellationToken);

        Assert.Null(device);
    }

    [Fact]
    public async Task AdvertisedDeviceMustPassTheControlServiceFingerprint()
    {
        var candidate = new DiscoveredDevice("192.168.50.42", 55052, "Living Room");
        var probeCalls = 0;

        var rejected = await MdnsDiscovery.VerifyAdvertisedDeviceAsync(
            candidate,
            (address, port, _) =>
            {
                Interlocked.Increment(ref probeCalls);
                Assert.Equal(IPAddress.Parse(candidate.Host), address);
                Assert.Equal(candidate.Port, port);
                return Task.FromResult(false);
            },
            TestContext.Current.CancellationToken);

        var accepted = await MdnsDiscovery.VerifyAdvertisedDeviceAsync(
            candidate,
            static (_, _, _) => Task.FromResult(true),
            TestContext.Current.CancellationToken);

        Assert.Null(rejected);
        Assert.Same(candidate, accepted);
        Assert.Equal(1, probeCalls);
    }

    [Fact]
    public async Task ProbeCandidatesPropagatesCallerCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MdnsDiscovery.ProbeCandidatesAsync(
                Array.Empty<IPAddress>(),
                port: 55051,
                maxConcurrency: 1,
                static (_, _, _) => Task.FromResult(false),
                cancellation.Token));
    }

    [Theory]
    [InlineData(Grpc.Core.StatusCode.InvalidArgument)]
    [InlineData(Grpc.Core.StatusCode.Unauthenticated)]
    [InlineData(Grpc.Core.StatusCode.PermissionDenied)]
    [InlineData(Grpc.Core.StatusCode.FailedPrecondition)]
    public void FingerprintAcceptsOnlyApplicationLevelEvidence(Grpc.Core.StatusCode statusCode)
    {
        Assert.True(MdnsDiscovery.IsFingerprintEvidenceStatus(statusCode));
        Assert.False(MdnsDiscovery.IsFingerprintEvidenceStatus(Grpc.Core.StatusCode.Unimplemented));
        Assert.False(MdnsDiscovery.IsFingerprintEvidenceStatus(Grpc.Core.StatusCode.Unavailable));
    }

    private static byte[] BuildMdnsResponse(
        string serviceType,
        string instanceLabel,
        string target,
        IPAddress address,
        int port,
        string displayName)
    {
        using var packet = new MemoryStream();
        packet.Write(new byte[]
        {
            0x00, 0x00, 0x84, 0x00, 0x00, 0x01,
            0x00, 0x04, 0x00, 0x00, 0x00, 0x00
        });

        const int serviceNameOffset = 12;
        WriteDnsName(packet, serviceType);
        WriteUInt16(packet, 12); // PTR question
        WriteUInt16(packet, 1);  // IN

        var instance = $"{instanceLabel}.{serviceType.TrimEnd('.')}";
        using var ptrData = new MemoryStream();
        WriteDnsLabel(ptrData, instanceLabel);
        WriteCompressionPointer(ptrData, serviceNameOffset);
        WriteResourceRecord(
            packet,
            ownerWriter: stream => WriteCompressionPointer(stream, serviceNameOffset),
            type: 12,
            ptrData.ToArray());

        using var srvData = new MemoryStream();
        WriteUInt16(srvData, 0); // priority
        WriteUInt16(srvData, 0); // weight
        WriteUInt16(srvData, checked((ushort)port));
        WriteDnsName(srvData, target);
        WriteResourceRecord(
            packet,
            ownerWriter: stream => WriteDnsName(stream, instance),
            type: 33,
            srvData.ToArray());

        var txtValue = Encoding.UTF8.GetBytes($"imName={displayName}");
        using var txtData = new MemoryStream();
        txtData.WriteByte(checked((byte)txtValue.Length));
        txtData.Write(txtValue);
        WriteResourceRecord(
            packet,
            ownerWriter: stream => WriteDnsName(stream, instance),
            type: 16,
            txtData.ToArray());

        WriteResourceRecord(
            packet,
            ownerWriter: stream => WriteDnsName(stream, target),
            type: 1,
            address.GetAddressBytes());

        return packet.ToArray();
    }

    private static void WriteResourceRecord(
        Stream packet,
        Action<Stream> ownerWriter,
        ushort type,
        byte[] data)
    {
        ownerWriter(packet);
        WriteUInt16(packet, type);
        WriteUInt16(packet, 0x8001); // cache-flush + IN
        WriteUInt32(packet, 120);
        WriteUInt16(packet, checked((ushort)data.Length));
        packet.Write(data);
    }

    private static void WriteDnsName(Stream stream, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
            WriteDnsLabel(stream, label);
        stream.WriteByte(0);
    }

    private static void WriteDnsLabel(Stream stream, string label)
    {
        var bytes = Encoding.ASCII.GetBytes(label);
        stream.WriteByte(checked((byte)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteCompressionPointer(Stream stream, int offset)
    {
        WriteUInt16(stream, checked((ushort)(0xC000 | offset)));
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
