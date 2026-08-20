using System;
using System.IO;
using System.Text;

namespace BraviaTheatre.Core.Wire;

public static class CommandBuilder
{
    private static byte[] BuildEmbeddedSessionBlock(byte[] sessionRandom, string sessionId)
    {
        using var ms = new MemoryStream();
        // field 1: session_random (bytes)
        var f1 = ProtobufWireCodec.LengthDelimited(1, sessionRandom);
        ms.Write(f1, 0, f1.Length);

        // field 2: session_id (string)
        var idBytes = Encoding.UTF8.GetBytes(sessionId);
        var f2 = ProtobufWireCodec.LengthDelimited(2, idBytes);
        ms.Write(f2, 0, f2.Length);

        var inner = ms.ToArray();
        return ProtobufWireCodec.LengthDelimited(2, inner);
    }

    private static byte[] BuildValueSuffix(int? intValue, bool? boolValue, string? stringValue)
    {
        if (intValue.HasValue)
        {
            var valBytes = ProtobufWireCodec.EncodeVarint(intValue.Value);
            var inner = new byte[1 + valBytes.Length];
            inner[0] = 0x08;
            Buffer.BlockCopy(valBytes, 0, inner, 1, valBytes.Length);
            return ProtobufWireCodec.LengthDelimited(2, inner);
        }
        if (boolValue.HasValue)
        {
            var inner = new byte[] { 0x08, (byte)(boolValue.Value ? 0x01 : 0x00) };
            return ProtobufWireCodec.LengthDelimited(3, inner);
        }
        if (!string.IsNullOrEmpty(stringValue))
        {
            var strBytes = Encoding.UTF8.GetBytes(stringValue);
            var inner = ProtobufWireCodec.LengthDelimited(1, strBytes);
            return ProtobufWireCodec.LengthDelimited(4, inner);
        }

        throw new ArgumentException("Must specify exactly one value (int, bool, or string)");
    }

    public static byte[] BuildSigningPreimage(
        string commandPath,
        byte[] sessionRandom,
        string sessionId,
        int? intValue = null,
        bool? boolValue = null,
        string? stringValue = null)
    {
        var pathBytes = Encoding.UTF8.GetBytes(commandPath);
        var suffix = BuildValueSuffix(intValue, boolValue, stringValue);

        using var ms3 = new MemoryStream();
        var f1 = ProtobufWireCodec.LengthDelimited(1, pathBytes);
        ms3.Write(f1, 0, f1.Length);
        ms3.Write(new byte[] { 0x10, 0x01 }, 0, 2);
        ms3.Write(suffix, 0, suffix.Length);

        var depth3 = ms3.ToArray();
        var depth2 = ProtobufWireCodec.LengthDelimited(1, depth3);
        var depth1 = ProtobufWireCodec.LengthDelimited(1, depth2);

        var sessionBlock = BuildEmbeddedSessionBlock(sessionRandom, sessionId);

        var res = new byte[depth1.Length + sessionBlock.Length];
        Buffer.BlockCopy(depth1, 0, res, 0, depth1.Length);
        Buffer.BlockCopy(sessionBlock, 0, res, depth1.Length, sessionBlock.Length);
        return res;
    }

    public static byte[] BuildExecCommandRequest(
        string hmacKeyHex,
        string commandPath,
        byte[] sessionRandom,
        string sessionId,
        int? intValue = null,
        bool? boolValue = null,
        string? stringValue = null)
    {
        var innerCmd = BuildSigningPreimage(
            commandPath,
            sessionRandom,
            sessionId,
            intValue,
            boolValue,
            stringValue);

        var authToken = PacketSigner.ComputeHmac(hmacKeyHex, innerCmd);

        var cmdBlock = ProtobufWireCodec.LengthDelimited(1, innerCmd);
        var authBlock = ProtobufWireCodec.LengthDelimited(2, authToken);

        var outer = new byte[cmdBlock.Length + authBlock.Length];
        Buffer.BlockCopy(cmdBlock, 0, outer, 0, cmdBlock.Length);
        Buffer.BlockCopy(authBlock, 0, outer, cmdBlock.Length, authBlock.Length);

        return ProtobufWireCodec.LengthDelimited(1, outer);
    }

    public static bool ParseExecResponse(ReadOnlySpan<byte> raw)
    {
        return raw.Length == 2 && raw[0] == 0x08 && raw[1] == 0x01;
    }
}
