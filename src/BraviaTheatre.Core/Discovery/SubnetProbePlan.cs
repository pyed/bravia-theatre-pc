using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace BraviaTheatre.Core.Discovery;

/// <summary>
/// Decides where and how often discovery may probe the subnet for the soundbar, and where the
/// next sweep starts.
/// </summary>
/// <remarks>
/// The probe connects to the control port on every nearby address. It used to do that on any
/// private network the PC joined, every 11 to 66 s for as long as no soundbar answered: a recurring
/// port sweep of an office, hotel or VPN network. Once a soundbar has been found, only its network
/// is swept; anywhere else discovery relies on mDNS and the last known address. A sweep also ends
/// with discovery's time budget, after about 120 addresses where unused ones do not answer, and
/// every sweep used to start from the same address, so a soundbar further away was never reached.
/// Each sweep now continues where the last one stopped, and after a full pass without a soundbar
/// sweeping pauses, for longer after each further miss.
/// </remarks>
public sealed class SubnetProbePlan
{
    internal static readonly TimeSpan FirstPause = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan LongestPause = TimeSpan.FromMinutes(30);

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Func<IReadOnlyList<ActiveIPv4Interface>> _interfaces;
    private string? _soundbarNetwork;
    private int _candidateCount;
    private int _cursor;
    private int _coveredThisPass;
    private TimeSpan _nextPause = FirstPause;
    private TimeSpan _pause;
    private long? _pauseStarted;

    /// <param name="soundbarNetwork">
    /// The network the soundbar was last found on, as <see cref="SoundbarNetworkChanged"/> reported
    /// it; null or unreadable means none is known yet, so any private network may be swept.
    /// </param>
    public SubnetProbePlan(string? soundbarNetwork = null)
        : this(soundbarNetwork, TimeProvider.System, MdnsDiscovery.GetActiveIPv4Interfaces)
    {
    }

    internal SubnetProbePlan(
        string? soundbarNetwork,
        TimeProvider timeProvider,
        Func<IReadOnlyList<ActiveIPv4Interface>> interfaces)
    {
        _soundbarNetwork = NormalizeNetwork(soundbarNetwork);
        _timeProvider = timeProvider;
        _interfaces = interfaces;
    }

    /// <summary>
    /// Raised with the soundbar's network, for example "192.168.1.0/24", when the app connects to a
    /// soundbar on a network other than the one it knew. Store it and pass it back next time.
    /// </summary>
    public event Action<string>? SoundbarNetworkChanged;

    public string? SoundbarNetwork
    {
        get { lock (_sync) return _soundbarNetwork; }
    }

    /// <summary>
    /// Remembers the network of a soundbar the app has connected to, and lifts any pause, so a later
    /// miss starts over.
    /// </summary>
    public void RecordSoundbar(string host)
    {
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return;

        string? network = null;
        foreach (var candidate in _interfaces())
        {
            if (DescribeNetwork(candidate.Address, candidate.Mask) is { } described
                && Contains(candidate, address))
            {
                network = described;
                break;
            }
        }

        Action<string>? changed = null;
        lock (_sync)
        {
            ResetPausesLocked();
            if (network != null && !string.Equals(network, _soundbarNetwork, StringComparison.Ordinal))
            {
                _soundbarNetwork = network;
                changed = SoundbarNetworkChanged;
            }
        }

        if (network != null) changed?.Invoke(network);
    }

    /// <summary>
    /// The interfaces whose subnets may be swept now: none while sweeping pauses, and once the
    /// soundbar's network is known, only an interface on that network.
    /// </summary>
    internal IReadOnlyList<ActiveIPv4Interface> SelectInterfaces(IEnumerable<ActiveIPv4Interface> eligible)
    {
        lock (_sync)
        {
            // Measured on the monotonic clock, so a change of the wall clock cannot stretch a pause.
            if (_pauseStarted is { } started && _timeProvider.GetElapsedTime(started) < _pause)
                return [];

            var known = _soundbarNetwork;
            return eligible
                .Where(network => known == null
                    || string.Equals(DescribeNetwork(network.Address, network.Mask), known, StringComparison.Ordinal))
                .ToArray();
        }
    }

    /// <summary>Orders the candidates so that this sweep starts where the last one stopped.</summary>
    internal IReadOnlyList<IPAddress> BeginSweep(IReadOnlyList<IPAddress> candidates)
    {
        lock (_sync)
        {
            if (candidates.Count != _candidateCount)
            {
                // Another network, or the interfaces changed: start that list from its nearest address.
                _candidateCount = candidates.Count;
                _cursor = 0;
                _coveredThisPass = 0;
            }

            if (candidates.Count == 0) return candidates;
            var start = _cursor % candidates.Count;
            return candidates.Skip(start).Concat(candidates.Take(start)).ToArray();
        }
    }

    /// <summary>
    /// Records how far a sweep got: every candidate before <paramref name="firstUnfinished"/>, in the
    /// order <see cref="BeginSweep"/> returned, was probed to the end.
    /// </summary>
    internal void EndSweep(int candidateCount, int firstUnfinished, bool found)
    {
        lock (_sync)
        {
            if (found)
            {
                ResetPausesLocked();
                return;
            }

            if (candidateCount == 0 || candidateCount != _candidateCount) return;

            firstUnfinished = Math.Clamp(firstUnfinished, 0, candidateCount);
            _cursor = (_cursor + firstUnfinished) % candidateCount;
            _coveredThisPass += firstUnfinished;
            if (_coveredThisPass < candidateCount) return;

            _coveredThisPass = 0;
            _pause = _nextPause;
            _pauseStarted = _timeProvider.GetTimestamp();
            _nextPause = _nextPause * 2 > LongestPause ? LongestPause : _nextPause * 2;
        }
    }

    /// <summary>Describes an interface's network as "address/prefix", or null for an unusable mask.</summary>
    internal static string? DescribeNetwork(IPAddress address, IPAddress mask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork
            || mask.AddressFamily != AddressFamily.InterNetwork
            || address.Equals(IPAddress.Any))
        {
            return null;
        }

        var maskValue = ToUInt32(mask);
        if (!TryGetPrefixLength(maskValue, out var prefix)) return null;

        var network = FromUInt32(ToUInt32(address) & maskValue);
        return string.Create(CultureInfo.InvariantCulture, $"{network}/{prefix}");
    }

    /// <summary>Returns the canonical form of a stored network, or null if it is not one.</summary>
    internal static string? NormalizeNetwork(string? value)
    {
        var parts = value?.Trim().Split('/');
        if (parts is not { Length: 2 }
            || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
            || prefix is < 1 or > 32)
        {
            return null;
        }

        var mask = prefix == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefix);
        return DescribeNetwork(address, FromUInt32(mask));
    }

    private static bool Contains(ActiveIPv4Interface network, IPAddress address)
    {
        var mask = ToUInt32(network.Mask);
        return (ToUInt32(address) & mask) == (ToUInt32(network.Address) & mask);
    }

    private static bool TryGetPrefixLength(uint mask, out int prefix)
    {
        prefix = BitOperations.PopCount(mask);
        // A usable mask is one run of ones from the top bit: its inverse plus one is a power of two.
        var inverse = ~mask;
        return prefix > 0 && (inverse & (inverse + 1)) == 0;
    }

    private void ResetPausesLocked()
    {
        _pauseStarted = null;
        _nextPause = FirstPause;
        _coveredThisPass = 0;
    }

    private static uint ToUInt32(IPAddress address) =>
        BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    private static IPAddress FromUInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
