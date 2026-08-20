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

    [Fact]
    public void TestParseGetStatesResponse()
    {
        var raw = Convert.FromHexString("12580a0e0a0c0a06766f6c756d65120208511220244df0e107ff5017655828d7a23d65f7514b4dfc01c36733ade21952a559f1341a2437343931643738642d643039622d343634382d613739312d313136613530636363393061");
        var states = StatesCodec.ParseGetStatesResponse(raw);
        Assert.True(states.ContainsKey("volume"));
        Assert.Equal(81, Convert.ToInt32(states["volume"]));
    }

    [Fact]
    public void TestBuildSingleGetStatesRequest()
    {
        var path = "volume";
        var rnd = Convert.FromHexString("0102030405060708");
        var sid = "7491d78d-d09b-4648-a791-116a50ccc90a";
        var key = "***REMOVED***";

        var bytes = StatesCodec.BuildSingleGetStatesRequest(key, path, rnd, sid);
        var expectedHex = "0a3c0a080a06766f6c756d6512300a0801020304050607081a2437343931643738642d643039622d343634382d613739312d313136613530636363393061122029e4309d3ccc3fdcb8fd506037ddf3978405e0c79b874c5d96606a5e2d6846a4";

        Assert.Equal(expectedHex, Convert.ToHexString(bytes).ToLowerInvariant());
    }

    [Fact]
    public void TestBuildExecCommandRequest()
    {
        var path = "volume";
        var rnd = Convert.FromHexString("0102030405060708");
        var sid = "7491d78d-d09b-4648-a791-116a50ccc90a";
        var key = "***REMOVED***";

        var bytes = CommandBuilder.BuildExecCommandRequest(key, path, rnd, sid, intValue: 37);
        var expectedHex = "0a680a440a100a0e0a06766f6c756d6510012202082512300a0801020304050607081a2437343931643738642d643039622d343634382d613739312d3131366135306363633930611220330b311a6a188542406192348bc68dd29e2188b2eba9947623463ddd7661ff58";

        Assert.Equal(expectedHex, Convert.ToHexString(bytes).ToLowerInvariant());
    }
}
