using System;
using System.Text;
using BraviaTheatre.Core.Wire;
using Xunit;

namespace BraviaTheatre.Tests;

public class WireCodecTests
{
    [Fact]
    public void TestVarintEncoding()
    {
        var encoded0 = ProtobufWireCodec.EncodeVarint(0);
        Assert.Equal(new byte[] { 0x00 }, encoded0);

        var encoded1 = ProtobufWireCodec.EncodeVarint(1);
        Assert.Equal(new byte[] { 0x01 }, encoded1);

        var encoded300 = ProtobufWireCodec.EncodeVarint(300);
        Assert.Equal(new byte[] { 0xAC, 0x02 }, encoded300);

        var (val, pos) = ProtobufWireCodec.ReadVarint(encoded300, 0);
        Assert.Equal(300UL, val);
        Assert.Equal(2, pos);
    }

    [Fact]
    public void TestLengthDelimited()
    {
        var body = Encoding.UTF8.GetBytes("hello");
        var field = ProtobufWireCodec.LengthDelimited(1, body);

        // tag = (1 << 3) | 2 = 0x0a, length = 5, body = "hello"
        Assert.Equal(0x0A, field[0]);
        Assert.Equal(5, field[1]);
        Assert.Equal("hello", Encoding.UTF8.GetString(field.AsSpan(2, 5)));
    }

    [Fact]
    public void TestHmacSigning()
    {
        var keyHex = "***REMOVED***";
        var message = Encoding.UTF8.GetBytes("test_message");

        var hmac = PacketSigner.ComputeHmac(keyHex, message);
        Assert.NotNull(hmac);
        Assert.Equal(32, hmac.Length);
    }

    [Fact]
    public void TestExecCommandBuilding()
    {
        var keyHex = new string('a', 64);
        var sessionRandom = new byte[8];
        var ***REMOVED***;

        var req = CommandBuilder.BuildExecCommandRequest(
            keyHex,
            "volume",
            sessionRandom,
            sessionId,
            intValue: 25);

        Assert.NotNull(req);
        Assert.True(req.Length > 0);
        Assert.Equal(0x0A, req[0]); // Outer tag
    }

    [Fact]
    public void TestExecResponseParsing()
    {
        var okResp = new byte[] { 0x08, 0x01 };
        Assert.True(CommandBuilder.ParseExecResponse(okResp));

        var failResp = new byte[] { 0x08, 0x00 };
        Assert.False(CommandBuilder.ParseExecResponse(failResp));
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
}
