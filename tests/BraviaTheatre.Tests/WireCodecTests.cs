using System;
using System.IO;
using System.Linq;
using System.Text;
using BraviaTheatre.Core.Wire;
using Xunit;

namespace BraviaTheatre.Tests;

public class WireCodecTests
{
    private const string SyntheticSessionId = "11111111-2222-3333-4444-555555555555";
    private static readonly string SyntheticHmacKey = Convert.ToHexString(
        Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

    [Fact]
    public void TestVarintEncoding()
    {
        Assert.Equal(new byte[] { 0x00 }, ProtobufWireCodec.EncodeVarint(0));
        Assert.Equal(new byte[] { 0x01 }, ProtobufWireCodec.EncodeVarint(1));

        var encoded300 = ProtobufWireCodec.EncodeVarint(300);
        Assert.Equal(new byte[] { 0xAC, 0x02 }, encoded300);

        var (value, pos) = ProtobufWireCodec.ReadVarint(encoded300, 0);
        Assert.Equal(300UL, value);
        Assert.Equal(2, pos);
    }

    [Theory]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02 })]
    public void TryReadVarintRejectsTruncatedOrOverflowingValues(byte[] raw)
    {
        Assert.False(ProtobufWireCodec.TryReadVarint(raw, 0, out _, out _));
        Assert.Throws<InvalidDataException>(() => ProtobufWireCodec.ReadVarint(raw, 0));
    }

    [Fact]
    public void TryDecodeFieldRejectsOversizedLengthWithoutThrowing()
    {
        var raw = new byte[] { 0x12, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F };
        var exception = Record.Exception(() =>
            ProtobufWireCodec.TryDecodeField(raw, 0, out _, out _));

        Assert.Null(exception);
        Assert.False(ProtobufWireCodec.TryDecodeField(raw, 0, out _, out _));
    }

    [Fact]
    public void DecodeFieldSkipsFixedWidthUnknownFieldsSafely()
    {
        var raw = new byte[]
        {
            0x0D, 0x01, 0x02, 0x03, 0x04, // field 1, fixed32
            0x10, 0x07                    // field 2, varint 7
        };

        Assert.True(ProtobufWireCodec.TryDecodeField(raw, 0, out var first, out var next));
        Assert.Equal(5, first!.WireType);
        Assert.Equal(5, next);

        Assert.True(ProtobufWireCodec.TryDecodeField(raw, next, out var second, out next));
        Assert.Equal(2, second!.FieldNumber);
        Assert.Equal(7UL, second.VarintValue);
        Assert.Equal(raw.Length, next);
    }

    [Fact]
    public void TestLengthDelimited()
    {
        var body = Encoding.UTF8.GetBytes("hello");
        var field = ProtobufWireCodec.LengthDelimited(1, body);

        Assert.Equal(0x0A, field[0]);
        Assert.Equal(5, field[1]);
        Assert.Equal("hello", Encoding.UTF8.GetString(field.AsSpan(2, 5)));
    }

    [Fact]
    public void TestHmacSigning()
    {
        var message = Encoding.UTF8.GetBytes("synthetic-test-message");
        var hmac = PacketSigner.ComputeHmac(SyntheticHmacKey, message);

        Assert.Equal(32, hmac.Length);
        Assert.Equal(
            "46424bfb6dfad30f03e31a89dd0ec7fd69c8c486125f129434aa62540c8bef79",
            Convert.ToHexString(hmac).ToLowerInvariant());
    }

    [Fact]
    public void TestExecCommandBuilding()
    {
        var request = CommandBuilder.BuildExecCommandRequest(
            SyntheticHmacKey,
            "volume",
            new byte[8],
            SyntheticSessionId,
            intValue: 25);

        Assert.NotEmpty(request);
        Assert.Equal(0x0A, request[0]);
    }

    [Fact]
    public void ExecCommandRequiresExactlyOneValue()
    {
        Assert.Throws<ArgumentException>(() => CommandBuilder.BuildExecCommandRequest(
            SyntheticHmacKey,
            "volume",
            new byte[8],
            SyntheticSessionId,
            intValue: 25,
            boolValue: true));
    }

    [Fact]
    public void TestExecResponseParsing()
    {
        Assert.True(CommandBuilder.ParseExecResponse(new byte[] { 0x08, 0x01 }));
        Assert.False(CommandBuilder.ParseExecResponse(new byte[] { 0x08, 0x00 }));
        Assert.True(CommandBuilder.ParseExecResponse(Array.Empty<byte>()));
        Assert.False(CommandBuilder.ParseExecResponse(new byte[] { 0x08, 0x01, 0x10, 0x01 }));
    }

    [Theory]
    [InlineData("ssh-app://signin?code=AUTH123&state=STATE456", "AUTH123")]
    [InlineData("https://example.com/signin?code=AUTH123&state=STATE456", "AUTH123")]
    [InlineData("AUTH123&state=STATE456", "AUTH123")]
    [InlineData("code=AUTH123&state=STATE456", "AUTH123")]
    [InlineData("AUTH123", "AUTH123")]
    public void TestParseAuthorizationCode(string input, string expectedCode)
    {
        var code = BraviaTheatre.Core.Auth.SonyOAuth.ParseAuthorizationCode(input);
        Assert.Equal(expectedCode, code);
    }

    [Fact]
    public void ParseGetStatesResponseSkipsOpaqueMetadata()
    {
        var authToken = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        // This prefix previously made the parser reinterpret auth bytes as a
        // length-delimited state entry and throw ArgumentOutOfRangeException.
        new byte[] { 0x12, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }.CopyTo(authToken, 0);
        var raw = BuildSyntheticStatesResponse(authToken);

        var exception = Record.Exception(() => StatesCodec.ParseGetStatesResponse(raw));
        Assert.Null(exception);

        var states = StatesCodec.ParseGetStatesResponse(raw);
        Assert.Single(states);
        Assert.Equal(81, Convert.ToInt32(states["volume"]));

        var (_, extractedAuth, sessionId) = StatesCodec.ExtractSessionTokens(raw);
        Assert.Equal(authToken, extractedAuth);
        Assert.Equal(SyntheticSessionId, sessionId);
    }

    [Fact]
    public void MalformedStatesResponseReturnsOnlyCompleteEntries()
    {
        var complete = BuildSyntheticStatesResponse(new byte[32]);
        var truncated = complete[..^1];

        var exception = Record.Exception(() => StatesCodec.ParseGetStatesResponse(truncated));
        Assert.Null(exception);
        Assert.Empty(StatesCodec.ParseGetStatesResponse(truncated));
    }

    [Fact]
    public void TestBuildSingleGetStatesRequest()
    {
        var bytes = StatesCodec.BuildSingleGetStatesRequest(
            SyntheticHmacKey,
            "volume",
            Convert.FromHexString("0102030405060708"),
            SyntheticSessionId);

        const string expectedHex = "0a3c0a080a06766f6c756d6512300a0801020304050607081a2431313131313131312d323232322d333333332d343434342d35353535353535353535353512206309ccd57e9b4823ad95eef30dd87abc77cd7d2220ca88a84c1d69da53765cd8";
        Assert.Equal(expectedHex, Convert.ToHexString(bytes).ToLowerInvariant());
    }

    [Fact]
    public void TestBuildExecCommandRequest()
    {
        var bytes = CommandBuilder.BuildExecCommandRequest(
            SyntheticHmacKey,
            "volume",
            Convert.FromHexString("0102030405060708"),
            SyntheticSessionId,
            intValue: 37);

        const string expectedHex = "0a680a440a100a0e0a06766f6c756d6510012202082512300a0801020304050607081a2431313131313131312d323232322d333333332d343434342d3535353535353535353535351220cd32bcc6b7b809fea0fe2901a8f73ff9f4f188d0d473bfe5faa8600ea508664a";
        Assert.Equal(expectedHex, Convert.ToHexString(bytes).ToLowerInvariant());
    }

    [Theory]
    [InlineData("dolby_atmos_truehd", "atmos_truehd", "Dolby Atmos (TrueHD)")]
    [InlineData("dolby_atmos_digital_plus", "atmos", "Dolby Atmos (DD+)")]
    [InlineData("dolby_digital_plus", "ddplus", "Dolby Digital Plus")]
    [InlineData("dolby_digital", "dd", "Dolby Digital")]
    [InlineData("dolby_audio", "ddplus", "Dolby Audio")]
    [InlineData("dts_x_master_audio", "dtsx", "DTS:X Master Audio")]
    [InlineData("dts_hd_master_audio", "dtshd", "DTS-HD MA")]
    [InlineData("lpcm", "lpcm", "LPCM")]
    public void TestCodecClassification(string raw, string expectedKind, string expectedLabel)
    {
        var kind = BraviaTheatre.Core.Models.CodecTaxonomy.Classify(raw);
        var label = BraviaTheatre.Core.Models.CodecTaxonomy.FormatHumanReadable(raw);

        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedLabel, label);
    }

    private static byte[] BuildSyntheticStatesResponse(byte[] authToken)
    {
        var path = ProtobufWireCodec.LengthDelimited(1, Encoding.UTF8.GetBytes("volume"));
        var value = ProtobufWireCodec.LengthDelimited(2, new byte[] { 0x08, 0x51 });
        var entry = Concat(path, value);
        var entriesStream = ProtobufWireCodec.LengthDelimited(1, entry);
        var statesField = ProtobufWireCodec.LengthDelimited(1, entriesStream);
        var authField = ProtobufWireCodec.LengthDelimited(2, authToken);
        var sessionField = ProtobufWireCodec.LengthDelimited(3, Encoding.UTF8.GetBytes(SyntheticSessionId));
        return ProtobufWireCodec.LengthDelimited(2, Concat(statesField, authField, sessionField));
    }

    private static byte[] Concat(params byte[][] values)
    {
        var length = values.Sum(value => value.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var value in values)
        {
            Buffer.BlockCopy(value, 0, result, offset, value.Length);
            offset += value.Length;
        }
        return result;
    }
}
