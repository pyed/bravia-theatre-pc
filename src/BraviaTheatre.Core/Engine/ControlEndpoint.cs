using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace BraviaTheatre.Core.Engine;

/// <summary>
/// Validates a user-supplied soundbar host and builds the control-service address from it.
/// </summary>
public static class ControlEndpoint
{
    /// <summary>
    /// Accepts a bare host name, a dotted-quad IPv4 address, or an IPv6 address (optionally
    /// bracketed). Schemes, ports, paths, and whitespace are rejected rather than guessed at,
    /// because interpolating them into the channel address fails on every connection attempt.
    /// </summary>
    public static bool TryNormalizeHost(string? value, out string host)
    {
        host = string.Empty;
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0 || candidate.Length > 253)
            return false;

        if (candidate.StartsWith('[') && candidate.EndsWith(']'))
            candidate = candidate[1..^1];

        if (candidate.Contains(':'))
        {
            if (!IPAddress.TryParse(candidate, out var ipv6)
                || ipv6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            host = ipv6.ToString();
            return true;
        }

        // Uri and IPAddress accept shorthand such as "12345" or "192.168.1", and Uri reads
        // an out-of-range address such as "192.168.1.256" as a DNS name. Anything made of
        // digits and dots is meant as an IPv4 address, so it must be a full dotted quad.
        if (IsDigitsAndDots(candidate))
        {
            // Build the address from the decimal octets. IPAddress.Parse reads a leading zero
            // as octal, so "192.168.1.010" became 192.168.1.8 instead of the .10 that was meant.
            if (!TryParseDottedQuad(candidate, out var octets))
                return false;
            host = new IPAddress(octets).ToString();
            return true;
        }

        switch (Uri.CheckHostName(candidate))
        {
            case UriHostNameType.Dns:
                host = candidate.TrimEnd('.').ToLowerInvariant();
                return host.Length > 0;

            default:
                return false;
        }
    }

    /// <summary>Builds the cleartext gRPC address for a host accepted by <see cref="TryNormalizeHost"/>.</summary>
    public static Uri CreateAddress(string host, int port)
    {
        if (!TryNormalizeHost(host, out var normalized))
            throw new ArgumentException("The soundbar host must be a host name or IP address without a scheme, port, or path.", nameof(host));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "The soundbar port must be between 1 and 65535.");

        var authority = normalized.Contains(':') ? $"[{normalized}]" : normalized;
        return new Uri($"http://{authority}:{port.ToString(CultureInfo.InvariantCulture)}");
    }

    private static bool IsDigitsAndDots(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c) && c != '.') return false;
        }
        return true;
    }

    private static bool TryParseDottedQuad(string value, out byte[] octets)
    {
        octets = new byte[4];
        var parts = value.Split('.');
        if (parts.Length != 4) return false;

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length is 0 or > 3
                || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out octets[index]))
            {
                return false;
            }
        }

        return true;
    }
}
