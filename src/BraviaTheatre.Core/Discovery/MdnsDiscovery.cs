using System;
using System.Net;
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
        using var client = new UdpClient();
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.ExclusiveAddressUse = false;
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        client.JoinMulticastGroup(MulticastAddress);

        var query = BuildPtrQuery(ServiceType);
        var endPoint = new IPEndPoint(MulticastAddress, MulticastPort);
        await client.SendAsync(query, query.Length, endPoint);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var receiveTask = client.ReceiveAsync(cts.Token).AsTask();
                var result = await receiveTask;

                var (host, port, name) = ParseResponse(result.Buffer, result.RemoteEndPoint.Address.ToString());
                if (!string.IsNullOrEmpty(host))
                {
                    return new DiscoveredDevice(host, port > 0 ? port : 55051, name ?? "Sony BRAVIA Device");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout reached
        }

        return null;
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

        // Best effort extraction: use sender IP and default port
        string name = "Sony BRAVIA Device";
        if (text.Contains("HT-A9000")) name = "BRAVIA Theatre Bar 9";
        else if (text.Contains("HT-A8000")) name = "BRAVIA Theatre Bar 8";
        else if (text.Contains("HT-A9M2")) name = "BRAVIA Theatre Quad";

        return (senderIp, 55051, name);
    }
}
