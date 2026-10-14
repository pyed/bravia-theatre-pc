using System.Net;
using System.Text;
using BraviaTheatre.Core.Discovery;
using BraviaTheatre.Core.Wire;

namespace BraviaTheatre.Tests;

/// <summary>
/// The soundbar, and anything else on the LAN for mDNS, can hand these parsers any bytes at all.
/// Each case runs seeded random input, mutations of valid messages, and random protobuf trees, so
/// a failure names its seed and iteration and reproduces exactly.
/// </summary>
public class ParserFuzzTests
{
    private const int Iterations = 3000;

    public static TheoryData<int> Seeds => [1, 2, 3, 20261014];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void NotificationsNeverThrowAndNameOnlyPathsTheyCarry(int seed)
    {
        Run(seed, ValidNotifications(), input =>
        {
            var (path, _) = NotifyParser.ParseNotifyMessage(input);
            // A path comes from the input, except that the short "sound_field" gains its prefix.
            Assert.True(path is null || path.Length < input.Length + "sound_setting.".Length);
        });
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void NotificationDeltasNeverThrow(int seed)
    {
        Run(seed, ValidNotifications().Select(UnwrapField2).ToArray(), input => NotifyParser.DecodeNotifyDelta(input));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void StateResponsesNeverThrowAndStayBounded(int seed)
    {
        Run(seed, ValidStateResponses(), input =>
        {
            var states = StatesCodec.ParseGetStatesResponse(input);
            // Every entry needs at least a tag, a length, and one byte of path.
            Assert.True(states.Count <= input.Length / 3);
            Assert.All(states.Keys, path => Assert.True(path.Length < input.Length));
        });
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void SessionTokensNeverThrowAndKeepTheirSizes(int seed)
    {
        Run(seed, ValidStateResponses(), input =>
        {
            var (random, authToken, sessionId) = StatesCodec.ExtractSessionTokens(input);
            Assert.True(random is null || random.Length == 8);
            Assert.True(authToken is null || authToken.Length == 32);
            Assert.True(sessionId is null || sessionId.Length < input.Length);
        });
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void FieldDecodingAlwaysAdvancesWithinTheBuffer(int seed)
    {
        Run(seed, ValidNotifications().Concat(ValidStateResponses()).ToArray(), input =>
        {
            var position = 0;
            while (position < input.Length
                && ProtobufWireCodec.TryDecodeField(input, position, out var field, out var next))
            {
                Assert.NotNull(field);
                Assert.InRange(next, position + 1, input.Length);
                Assert.True(field.BytesValue is null || field.BytesValue.Length <= input.Length - position);
                position = next;
            }
        });
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void MdnsResponsesNeverThrowAndOnlyNameValidPorts(int seed)
    {
        var sender = IPAddress.Parse("192.0.2.7");
        byte[][] valid =
        [
            MdnsDiscoveryTests.BuildMdnsResponse(
                "_sonysmarthome._tcp.local.", "HT-A9000", "bar.local.", IPAddress.Parse("192.0.2.10"), 55051, "Living room"),
            MdnsDiscoveryTests.BuildMdnsResponse(
                "_sonysmarthome._tcp.local.", "Bar", "x.local.", IPAddress.Parse("192.0.2.11"), 1, "B")
        ];

        Run(seed, valid, input =>
        {
            var device = MdnsDiscovery.ParseResponse(input, sender);
            if (device is null) return;
            Assert.InRange(device.Port, 1, 65535);
            Assert.True(IPAddress.TryParse(device.Host, out _));
        });
    }

    private static void Run(int seed, IReadOnlyList<byte[]> valid, Action<byte[]> parse)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var input = (iteration % 3) switch
            {
                0 => RandomBytes(random),
                1 => Mutate(random, valid[random.Next(valid.Count)]),
                _ => RandomMessage(random, depth: 0)
            };

            try
            {
                parse(input);
            }
            catch (Exception error)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Seed {seed}, iteration {iteration}, input {Convert.ToHexString(input)}: {error}");
            }
        }
    }

    private static byte[] RandomBytes(Random random)
    {
        var bytes = new byte[random.Next(10) == 0 ? random.Next(2048) : random.Next(64)];
        random.NextBytes(bytes);
        return bytes;
    }

    private static byte[] Mutate(Random random, byte[] source)
    {
        var bytes = new List<byte>(source);
        for (var edits = random.Next(1, 4); edits > 0; edits--)
        {
            switch (random.Next(5))
            {
                case 0 when bytes.Count > 0:
                    // Flip one bit.
                    var index = random.Next(bytes.Count);
                    bytes[index] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when bytes.Count > 0:
                    // Truncate.
                    var cut = random.Next(bytes.Count);
                    bytes.RemoveRange(cut, bytes.Count - cut);
                    break;
                case 2:
                    // Insert random bytes.
                    var inserted = new byte[random.Next(1, 8)];
                    random.NextBytes(inserted);
                    bytes.InsertRange(random.Next(bytes.Count + 1), inserted);
                    break;
                case 3 when bytes.Count > 0:
                    // Set a byte to a boundary value, often a length or count.
                    bytes[random.Next(bytes.Count)] = (byte)(random.Next(2) == 0 ? 0x00 : 0xFF);
                    break;
                default:
                    // Duplicate a slice.
                    if (bytes.Count == 0) break;
                    var start = random.Next(bytes.Count);
                    var slice = bytes.GetRange(start, random.Next(bytes.Count - start + 1));
                    bytes.InsertRange(random.Next(bytes.Count + 1), slice);
                    break;
            }
        }

        return [.. bytes];
    }

    // A tree of plausible fields, so the parsers' nested paths see deep, valid-looking input.
    private static byte[] RandomMessage(Random random, int depth)
    {
        var message = new List<byte>();
        for (var fields = random.Next(1, 5); fields > 0; fields--)
        {
            var fieldNumber = random.Next(1, 6);
            switch (random.Next(depth < 4 ? 4 : 3))
            {
                case 0:
                    message.AddRange(ProtobufWireCodec.EncodeVarint((fieldNumber << 3) | 0));
                    message.AddRange(ProtobufWireCodec.EncodeVarint(random.NextInt64(long.MinValue, long.MaxValue)));
                    break;
                case 1:
                    var text = new byte[random.Next(12)];
                    random.NextBytes(text);
                    message.AddRange(ProtobufWireCodec.LengthDelimited(
                        fieldNumber,
                        random.Next(2) == 0 ? Encoding.UTF8.GetBytes("volume") : text));
                    break;
                case 2:
                    message.AddRange(ProtobufWireCodec.EncodeVarint((fieldNumber << 3) | 5));
                    message.AddRange(BitConverter.GetBytes(random.Next()));
                    break;
                default:
                    message.AddRange(ProtobufWireCodec.LengthDelimited(fieldNumber, RandomMessage(random, depth + 1)));
                    break;
            }
        }

        return [.. message];
    }

    private static byte[][] ValidNotifications() =>
    [
        Notification("volume", Varint(2, 25)),
        Notification("sound_field", Varint(2, 1)),
        Notification("power", ProtobufWireCodec.LengthDelimited(3, Varint(1, 1))),
        Notification("playback.audio_format", ProtobufWireCodec.LengthDelimited(4, ProtobufWireCodec.LengthDelimited(1, "dolby_atmos"u8.ToArray()))),
        ProtobufWireCodec.LengthDelimited(3, new byte[300])
    ];

    private static byte[][] ValidStateResponses()
    {
        var entry = Concat(
            ProtobufWireCodec.LengthDelimited(1, "volume"u8.ToArray()),
            ProtobufWireCodec.LengthDelimited(2, Varint(1, 81)));
        var stream = ProtobufWireCodec.LengthDelimited(1, entry);
        var authToken = new byte[32];
        new Random(7).NextBytes(authToken);
        return
        [
            ProtobufWireCodec.LengthDelimited(1, stream),
            ProtobufWireCodec.LengthDelimited(2, Concat(
                ProtobufWireCodec.LengthDelimited(1, stream),
                ProtobufWireCodec.LengthDelimited(2, new byte[8]),
                ProtobufWireCodec.LengthDelimited(3, "session-id"u8.ToArray()),
                ProtobufWireCodec.LengthDelimited(4, authToken)))
        ];
    }

    private static byte[] Notification(string path, byte[] value)
    {
        var entry = Concat(ProtobufWireCodec.LengthDelimited(1, Encoding.UTF8.GetBytes(path)), value);
        var delta = ProtobufWireCodec.LengthDelimited(1, ProtobufWireCodec.LengthDelimited(1, entry));
        return ProtobufWireCodec.LengthDelimited(2, delta);
    }

    private static byte[] UnwrapField2(byte[] notification) =>
        ProtobufWireCodec.TryDecodeField(notification, 0, out var field, out _) && field?.BytesValue is { } body
            ? body
            : notification;

    private static byte[] Varint(int fieldNumber, long value) =>
        Concat(ProtobufWireCodec.EncodeVarint((fieldNumber << 3) | 0), ProtobufWireCodec.EncodeVarint(value));

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];
}
