using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BraviaTheatre.Core.Discovery;

public sealed record DiscoveredDevice(string Host, int Port, string Name);

public static class MdnsDiscovery
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private const int MulticastPort = 5353;
    private const string ServiceType = "_sonysmarthome._tcp.local";

    public static async Task<DiscoveredDevice?> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // 1. Try mDNS Multicast on all local IPv4 interfaces
        var localIps = GetActiveIPv4Addresses();
        if (localIps.Count == 0)
            localIps.Add(IPAddress.Any);

        var clients = new List<UdpClient>();
        var tasks = new List<Task<DiscoveredDevice?>>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            var query = BuildPtrQuery(ServiceType);
            var endPoint = new IPEndPoint(MulticastAddress, MulticastPort);

            foreach (var localIp in localIps)
            {
                try
                {
                    var client = new UdpClient();
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    client.Client.Bind(new IPEndPoint(localIp, 0));

                    try
                    {
                        if (!localIp.Equals(IPAddress.Any))
                            client.JoinMulticastGroup(MulticastAddress, localIp);
                        else
                            client.JoinMulticastGroup(MulticastAddress);
                    }
                    catch
                    {
                        // Ignore multicast join error on unsupported interfaces
                    }

                    clients.Add(client);

                    // Send query from this interface
                    _ = client.SendAsync(query, query.Length, endPoint);

                    // Listen on this interface
                    tasks.Add(ListenOnClientAsync(client, cts.Token));
                }
                catch
                {
                    // Ignore interface bind failure
                }
            }

            // Also start fast subnet probe in parallel as backup
            tasks.Add(ProbeSubnetForSoundbarAsync(TimeSpan.FromMilliseconds(2500), cts.Token));

            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks);
                tasks.Remove(finished);

                var device = await finished;
                if (device != null)
                {
                    cts.Cancel(); // Found! Cancel remaining
                    return device;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout
        }
        finally
        {
            foreach (var c in clients)
            {
                try { c.Dispose(); } catch { }
            }
        }

        return null;
    }

    private static async Task<DiscoveredDevice?> ProbeSubnetForSoundbarAsync(TimeSpan timeout, CancellationToken ct)
    {
        var localIps = GetActiveIPv4Addresses();
        foreach (var localIp in localIps)
        {
            var bytes = localIp.GetAddressBytes();
            // Skip loopback, link-local, or virtualbox subnets
            if (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 192 && bytes[1] == 168 && bytes[2] == 56))
                continue;

            var prefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
            var probeTasks = new List<Task<DiscoveredDevice?>>();

            for (int i = 1; i <= 254; i++)
            {
                if (i == bytes[3]) continue;
                var targetIp = $"{prefix}.{i}";
                probeTasks.Add(ProbeIpAsync(targetIp, 55051, TimeSpan.FromMilliseconds(600), ct));
            }

            while (probeTasks.Count > 0)
            {
                var finished = await Task.WhenAny(probeTasks);
                probeTasks.Remove(finished);
                var found = await finished;
                if (found != null)
                    return found;
            }
        }
        return null;
    }

    private static async Task<DiscoveredDevice?> ProbeIpAsync(string ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await socket.ConnectAsync(ip, port, cts.Token);
            return new DiscoveredDevice(ip, port, "BRAVIA Theatre System");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<DiscoveredDevice?> ListenOnClientAsync(UdpClient client, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(ct);
                var (host, port, name) = ParseResponse(result.Buffer, result.RemoteEndPoint.Address.ToString());
                if (!string.IsNullOrEmpty(host))
                {
                    return new DiscoveredDevice(host, port > 0 ? port : 55051, name ?? "Sony BRAVIA Device");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on timeout/cancel
        }
        catch
        {
            // Socket closed
        }

        return null;
    }

    public static List<IPAddress> GetActiveIPv4Addresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = ni.GetIPProperties();
                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        list.Add(ua.Address);
                    }
                }
            }
        }
        catch
        {
            // Fallback
        }
        return list;
    }

    private static byte[] BuildPtrQuery(string service)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }); // Header

        foreach (var part in service.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(part);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.WriteByte(0x00); // End of name

        ms.Write(new byte[] { 0x00, 0x0c, 0x00, 0x01 }); // Type PTR, Class IN
        return ms.ToArray();
    }

    private static (string? host, int port, string? name) ParseResponse(byte[] buffer, string senderIp)
    {
        var text = Encoding.ASCII.GetString(buffer);
        if (!text.Contains("sonysmarthome", StringComparison.OrdinalIgnoreCase))
            return (null, 0, null);

        string name = "BRAVIA Theatre Bar 9";
        if (text.Contains("HT-A9000")) name = "BRAVIA Theatre Bar 9";
        else if (text.Contains("HT-A8000")) name = "BRAVIA Theatre Bar 8";
        else if (text.Contains("HT-A9M2")) name = "BRAVIA Theatre Quad";

        return (senderIp, 55051, name);
    }
}
