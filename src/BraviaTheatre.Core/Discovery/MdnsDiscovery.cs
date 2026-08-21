using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;

namespace BraviaTheatre.Core.Discovery;

public sealed record DiscoveredDevice(string Host, int Port, string Name);

internal sealed record ActiveIPv4Interface(IPAddress Address, IPAddress Mask, bool ScanEligible);

public static class MdnsDiscovery
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int MulticastPort = 5353;
    private const int DefaultControlPort = 55051;
    private const int MaxConcurrentProbes = 24;
    private const int MaxSubnetCandidatesPerInterface = 512;
    private const string ServiceType = "_sonysmarthome._tcp.local.";

    private static readonly Method<byte[], byte[]> FingerprintMethod = new(
        MethodType.Unary,
        "jp.co.sony.hes.ssh.controldevice.v1.ControlDeviceService",
        "GetSessionRandom",
        Marshallers.Create(static bytes => bytes, static bytes => bytes),
        Marshallers.Create(static bytes => bytes, static bytes => bytes));

    public static async Task<DiscoveredDevice?> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Discovery timeout must be positive.");

        var interfaces = GetActiveIPv4Interfaces();
        if (interfaces.Count == 0)
            interfaces.Add(new ActiveIPv4Interface(IPAddress.Any, IPAddress.Any, false));

        var clients = new List<UdpClient>();
        var tasks = new List<Task<DiscoveredDevice?>>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        DiscoveredDevice? found = null;
        try
        {
            var query = BuildPtrQuery(ServiceType);
            var endpoint = new IPEndPoint(MulticastAddress, MulticastPort);

            foreach (var network in interfaces)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();
                UdpClient? client = null;
                try
                {
                    client = new UdpClient();
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    client.Client.Bind(new IPEndPoint(network.Address, 0));
                    if (!network.Address.Equals(IPAddress.Any))
                        client.JoinMulticastGroup(MulticastAddress, network.Address);
                    else
                        client.JoinMulticastGroup(MulticastAddress);

                    await client.SendAsync(query, endpoint, timeoutCts.Token);
                    clients.Add(client);
                    tasks.Add(ListenOnClientAsync(client, timeoutCts.Token));
                    client = null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // One unsupported/broken adapter must not prevent discovery
                    // through other active interfaces.
                }
                finally
                {
                    client?.Dispose();
                }
            }

            var scanInterfaces = interfaces.Where(static network => network.ScanEligible).ToArray();
            if (scanInterfaces.Length > 0)
            {
                tasks.Add(DelayedSubnetProbeAsync(
                    scanInterfaces,
                    TimeSpan.FromMilliseconds(900),
                    timeoutCts.Token));
            }

            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks);
                tasks.Remove(finished);

                var device = await finished;
                if (device == null) continue;

                found = device;
                timeoutCts.Cancel();
                break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Internal discovery timeout.
        }
        finally
        {
            timeoutCts.Cancel();
            foreach (var client in clients)
            {
                try { client.Dispose(); } catch { }
            }

            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks); }
                catch (OperationCanceledException) { }
                catch { }
            }
        }

        return found;
    }

    private static async Task<DiscoveredDevice?> DelayedSubnetProbeAsync(
        IReadOnlyCollection<ActiveIPv4Interface> interfaces,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);

        var candidates = interfaces
            .SelectMany(network => BuildSubnetCandidates(
                network.Address,
                network.Mask,
                MaxSubnetCandidatesPerInterface))
            .Distinct()
            .ToArray();

        return await ProbeCandidatesAsync(
            candidates,
            DefaultControlPort,
            MaxConcurrentProbes,
            ProbeControlDeviceAsync,
            cancellationToken);
    }

    internal static async Task<DiscoveredDevice?> ProbeCandidatesAsync(
        IReadOnlyList<IPAddress> candidates,
        int port,
        int maxConcurrency,
        Func<IPAddress, int, CancellationToken, Task<bool>> probeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(probeAsync);
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (maxConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Count == 0) return null;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var nextIndex = -1;
        var resultLock = new object();
        DiscoveredDevice? found = null;

        async Task WorkerAsync()
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= candidates.Count) return;

                var address = candidates[index];
                if (!await probeAsync(address, port, linkedCts.Token)) continue;

                lock (resultLock)
                {
                    if (found != null) return;
                    found = new DiscoveredDevice(address.ToString(), port, "BRAVIA Theatre System");
                }

                linkedCts.Cancel();
                return;
            }
        }

        var workerCount = Math.Min(maxConcurrency, candidates.Count);
        var workers = Enumerable.Range(0, workerCount).Select(_ => WorkerAsync()).ToArray();
        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (found != null)
        {
            // The winning probe cancels sibling workers.
        }

        cancellationToken.ThrowIfCancellationRequested();
        return found;
    }

    private static async Task<bool> ProbeControlDeviceAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(TimeSpan.FromMilliseconds(1200));

        try
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                await socket.ConnectAsync(address, port, probeCts.Token);
            }

            return await FingerprintControlServiceAsync(address, port, probeCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    internal static async Task<bool> FingerprintControlServiceAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress($"http://{address}:{port}");
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(
            FingerprintMethod,
            null,
            new CallOptions(cancellationToken: cancellationToken),
            Array.Empty<byte>());

        try
        {
            await call.ResponseAsync;
            return true;
        }
        catch (RpcException exception) when (IsFingerprintEvidenceStatus(exception.StatusCode))
        {
            // These application-level statuses prove that the exact Sony method
            // exists while leaving device state untouched.
            return true;
        }
        catch (RpcException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static async Task<DiscoveredDevice?> ListenOnClientAsync(
        UdpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var result = await client.ReceiveAsync(cancellationToken);
                var advertised = ParseResponse(result.Buffer, result.RemoteEndPoint.Address);
                var verified = await VerifyAdvertisedDeviceAsync(
                    advertised,
                    ProbeControlDeviceAsync,
                    cancellationToken);
                if (verified != null) return verified;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal static async Task<DiscoveredDevice?> VerifyAdvertisedDeviceAsync(
        DiscoveredDevice? device,
        Func<IPAddress, int, CancellationToken, Task<bool>> probeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probeAsync);
        cancellationToken.ThrowIfCancellationRequested();

        if (device == null
            || !IPAddress.TryParse(device.Host, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        return await probeAsync(address, device.Port, cancellationToken)
            ? device
            : null;
    }

    public static List<IPAddress> GetActiveIPv4Addresses()
    {
        return GetActiveIPv4Interfaces().Select(static network => network.Address).ToList();
    }

    internal static List<ActiveIPv4Interface> GetActiveIPv4Interfaces()
    {
        var result = new List<ActiveIPv4Interface>();
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = networkInterface.GetIPProperties();
                var hasIPv4Gateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork
                    && !gateway.Address.Equals(IPAddress.Any));

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                        || unicast.IPv4Mask == null)
                    {
                        continue;
                    }

                    var eligible = hasIPv4Gateway
                        && IsPrivateOrSharedAddress(unicast.Address)
                        && !unicast.Address.Equals(IPAddress.Loopback);
                    result.Add(new ActiveIPv4Interface(unicast.Address, unicast.IPv4Mask, eligible));
                }
            }
        }
        catch
        {
            // Return interfaces found before an adapter API failure.
        }

        return result
            .GroupBy(static network => network.Address)
            .Select(static group => group.First())
            .ToList();
    }

    internal static IReadOnlyList<IPAddress> BuildSubnetCandidates(
        IPAddress localAddress,
        IPAddress mask,
        int maximumCandidates)
    {
        if (localAddress.AddressFamily != AddressFamily.InterNetwork
            || mask.AddressFamily != AddressFamily.InterNetwork
            || maximumCandidates <= 0)
        {
            return Array.Empty<IPAddress>();
        }

        var local = ToUInt32(localAddress);
        var maskValue = ToUInt32(mask);
        var inverseMask = ~maskValue;
        if ((inverseMask & (inverseMask + 1)) != 0)
            return Array.Empty<IPAddress>();

        var network = local & maskValue;
        var broadcast = network | inverseMask;
        if ((ulong)broadcast - network <= 1)
            return Array.Empty<IPAddress>();

        var candidates = new List<IPAddress>(maximumCandidates);
        for (ulong distance = 1; candidates.Count < maximumCandidates; distance++)
        {
            var added = false;
            if (distance < (ulong)local - network)
            {
                candidates.Add(FromUInt32(local - (uint)distance));
                added = true;
            }

            if (candidates.Count >= maximumCandidates) break;
            if (distance < (ulong)broadcast - local)
            {
                candidates.Add(FromUInt32(local + (uint)distance));
                added = true;
            }

            if (!added) break;
        }

        return candidates;
    }

    internal static DiscoveredDevice? ParseResponse(byte[] buffer, IPAddress senderAddress)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(senderAddress);
        if (buffer.Length < 12) return null;

        var flags = ReadUInt16(buffer, 2);
        if ((flags & 0x8000) == 0       // not a response
            || (flags & 0x7800) != 0   // unsupported opcode
            || (flags & 0x000F) != 0)  // DNS error response
        {
            return null;
        }

        var questionCount = ReadUInt16(buffer, 4);
        var answerCount = ReadUInt16(buffer, 6);
        var authorityCount = ReadUInt16(buffer, 8);
        var additionalCount = ReadUInt16(buffer, 10);
        var offset = 12;

        for (var index = 0; index < questionCount; index++)
        {
            if (!TryReadDnsName(buffer, ref offset, out _) || buffer.Length - offset < 4)
                return null;
            offset += 4;
        }

        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var services = new Dictionary<string, DnsServiceRecord>(StringComparer.OrdinalIgnoreCase);
        var textRecords = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var addresses = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        var recordCount = (long)answerCount + authorityCount + additionalCount;

        for (long index = 0; index < recordCount; index++)
        {
            if (!TryReadDnsName(buffer, ref offset, out var owner) || buffer.Length - offset < 10)
                return null;

            var type = ReadUInt16(buffer, offset);
            var recordClass = ReadUInt16(buffer, offset + 2) & 0x7FFF;
            var dataLength = ReadUInt16(buffer, offset + 8);
            offset += 10;
            if (dataLength > buffer.Length - offset) return null;

            var dataStart = offset;
            var dataEnd = offset + dataLength;
            var normalizedOwner = NormalizeDnsName(owner);

            if (recordClass != 1)
            {
                offset = dataEnd;
                continue;
            }

            if (type == 12)
            {
                var nameOffset = dataStart;
                if (TryReadDnsName(buffer, ref nameOffset, out var instance)
                    && nameOffset == dataEnd
                    && normalizedOwner.Equals(ServiceType, StringComparison.OrdinalIgnoreCase))
                {
                    instances.Add(NormalizeDnsName(instance));
                }
            }
            else if (type == 33 && dataLength >= 6)
            {
                var port = ReadUInt16(buffer, dataStart + 4);
                var nameOffset = dataStart + 6;
                if (TryReadDnsName(buffer, ref nameOffset, out var target) && nameOffset == dataEnd)
                {
                    services[normalizedOwner] = new DnsServiceRecord(
                        port,
                        NormalizeDnsName(target));
                    if (normalizedOwner.EndsWith('.' + ServiceType, StringComparison.OrdinalIgnoreCase))
                        instances.Add(normalizedOwner);
                }
            }
            else if (type == 16)
            {
                textRecords[normalizedOwner] = ParseTxtRecord(buffer.AsSpan(dataStart, dataLength));
            }
            else if (type == 1 && dataLength == 4)
            {
                addresses[normalizedOwner] = new IPAddress(buffer.AsSpan(dataStart, 4));
            }

            offset = dataEnd;
        }

        foreach (var instance in instances)
        {
            services.TryGetValue(instance, out var service);
            textRecords.TryGetValue(instance, out var txt);

            var hostAddress = service != null && addresses.TryGetValue(service.Target, out var resolved)
                ? resolved
                : senderAddress;
            var port = service?.Port ?? ParseTxtPort(txt) ?? DefaultControlPort;
            if (port is <= 0 or > 65535) continue;

            var name = SanitizeDisplayName(GetTxtValue(txt, "imName")
                ?? GetTxtValue(txt, "name")
                ?? FriendlyInstanceName(instance))
                ?? "Sony BRAVIA Device";
            return new DiscoveredDevice(hostAddress.ToString(), port, name);
        }

        return null;
    }

    private static byte[] BuildPtrQuery(string service)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[]
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        });

        foreach (var part in service.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(part);
            if (bytes.Length is 0 or > 63)
                throw new InvalidDataException("Invalid mDNS service label.");
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        stream.WriteByte(0);
        // Request a unicast reply because discovery sockets use ephemeral ports.
        stream.Write(new byte[] { 0x00, 0x0C, 0x80, 0x01 });
        return stream.ToArray();
    }

    private static bool TryReadDnsName(byte[] packet, ref int offset, out string name)
    {
        name = string.Empty;
        if ((uint)offset >= (uint)packet.Length) return false;

        var labels = new List<string>();
        var visited = new HashSet<int>();
        var cursor = offset;
        int? returnOffset = null;

        while (true)
        {
            if ((uint)cursor >= (uint)packet.Length || !visited.Add(cursor))
                return false;

            var length = packet[cursor++];
            if (length == 0)
            {
                offset = returnOffset ?? cursor;
                name = string.Join('.', labels) + '.';
                return name.Length <= 255;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor >= packet.Length) return false;
                var pointer = ((length & 0x3F) << 8) | packet[cursor++];
                if ((uint)pointer >= (uint)packet.Length) return false;
                returnOffset ??= cursor;
                cursor = pointer;
                continue;
            }

            if ((length & 0xC0) != 0 || length > 63 || length > packet.Length - cursor)
                return false;

            labels.Add(Encoding.ASCII.GetString(packet, cursor, length));
            cursor += length;
            if (labels.Count > 127) return false;
        }
    }

    private static Dictionary<string, string> ParseTxtRecord(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (offset < data.Length)
        {
            var length = data[offset++];
            if (length > data.Length - offset) break;

            string text;
            try { text = StrictUtf8.GetString(data.Slice(offset, length)); }
            catch (DecoderFallbackException) { offset += length; continue; }
            offset += length;

            var separator = text.IndexOf('=');
            if (separator > 0)
                result[text[..separator]] = text[(separator + 1)..];
            else if (text.Length > 0)
                result[text] = string.Empty;
        }
        return result;
    }

    private static int? ParseTxtPort(Dictionary<string, string>? values)
    {
        var raw = GetTxtValue(values, "port");
        return int.TryParse(raw, out var port) && port is > 0 and <= 65535 ? port : null;
    }

    private static string? GetTxtValue(Dictionary<string, string>? values, string key)
    {
        return values != null && values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string FriendlyInstanceName(string instance)
    {
        var serviceSuffix = '.' + ServiceType;
        var name = instance.EndsWith(serviceSuffix, StringComparison.OrdinalIgnoreCase)
            ? instance[..^serviceSuffix.Length]
            : instance.TrimEnd('.').Split('.')[0];

        if (name.Contains("HT-A9000", StringComparison.OrdinalIgnoreCase)) return "BRAVIA Theatre Bar 9";
        if (name.Contains("HT-A8000", StringComparison.OrdinalIgnoreCase)) return "BRAVIA Theatre Bar 8";
        if (name.Contains("HT-A9M2", StringComparison.OrdinalIgnoreCase)) return "BRAVIA Theatre Quad";
        return string.IsNullOrWhiteSpace(name) ? "Sony BRAVIA Device" : name;
    }

    internal static bool IsFingerprintEvidenceStatus(StatusCode statusCode)
    {
        return statusCode is StatusCode.InvalidArgument
            or StatusCode.Unauthenticated
            or StatusCode.PermissionDenied
            or StatusCode.FailedPrecondition;
    }

    private static string? SanitizeDisplayName(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 128)
            return null;

        return value.Any(char.IsControl) ? null : value;
    }

    private static bool IsPrivateOrSharedAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private static string NormalizeDnsName(string name)
    {
        return name.Trim().TrimEnd('.') + '.';
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }

    private static uint ToUInt32(IPAddress address)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }

    private static IPAddress FromUInt32(uint address)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
        return new IPAddress(bytes);
    }

    private sealed record DnsServiceRecord(int Port, string Target);
}
