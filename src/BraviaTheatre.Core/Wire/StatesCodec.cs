using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BraviaTheatre.Core.Wire;

public static class StatesCodec
{
    public static byte[] BuildGetStatesRequest(
        string hmacKeyHex,
        IEnumerable<string> fieldPaths,
        byte[] sessionRandom,
        string sessionId)
    {
        using var msInner = new MemoryStream();

        foreach (var path in fieldPaths)
        {
            var pathBytes = Encoding.UTF8.GetBytes(path);
            var depth2 = ProtobufWireCodec.LengthDelimited(1, pathBytes);
            var depth1 = ProtobufWireCodec.LengthDelimited(1, depth2);
            msInner.Write(depth1, 0, depth1.Length);
        }

        // Embedded session
        var idBytes = Encoding.UTF8.GetBytes(sessionId);
        using var msSession = new MemoryStream();
        var f1 = ProtobufWireCodec.LengthDelimited(1, sessionRandom);
        msSession.Write(f1, 0, f1.Length);
        var f3 = ProtobufWireCodec.LengthDelimited(3, idBytes);
        msSession.Write(f3, 0, f3.Length);

        var sessionData = msSession.ToArray();
        var sessionField = ProtobufWireCodec.LengthDelimited(2, sessionData);
        msInner.Write(sessionField, 0, sessionField.Length);

        var innerBody = msInner.ToArray();
        var authToken = PacketSigner.ComputeHmac(hmacKeyHex, innerBody);

        var fieldListBlock = ProtobufWireCodec.LengthDelimited(1, innerBody);
        var authBlock = ProtobufWireCodec.LengthDelimited(2, authToken);

        var outer = new byte[fieldListBlock.Length + authBlock.Length];
        Buffer.BlockCopy(fieldListBlock, 0, outer, 0, fieldListBlock.Length);
        Buffer.BlockCopy(authBlock, 0, outer, fieldListBlock.Length, authBlock.Length);

        return ProtobufWireCodec.LengthDelimited(1, outer);
    }

    public static (byte[]? sessionRandom, byte[]? authToken, string? sessionId) ExtractSessionTokens(byte[] raw)
    {
        byte[]? sessionRandom = null;
        byte[]? authToken = null;
        string? sessionId = null;

        int offset = 0;
        while (offset < raw.Length)
        {
            var (field, nextOffset) = ProtobufWireCodec.DecodeField(raw, offset);
            if (field == null)
                break;

            if (field.WireType == 2 && field.BytesValue != null)
            {
                if (field.FieldNumber == 2 && field.BytesValue.Length == 8)
                {
                    sessionRandom = field.BytesValue;
                }
                else if (field.FieldNumber == 3)
                {
                    sessionId = Encoding.UTF8.GetString(field.BytesValue);
                }
                else if (field.FieldNumber == 4 && field.BytesValue.Length == 32)
                {
                    authToken = field.BytesValue;
                }
            }

            offset = nextOffset;
        }

        return (sessionRandom, authToken, sessionId);
    }

    public static Dictionary<string, object?> ParseGetStatesResponse(byte[] raw)
    {
        var result = new Dictionary<string, object?>();
        int offset = 0;
        while (offset < raw.Length)
        {
            var (field, nextOffset) = ProtobufWireCodec.DecodeField(raw, offset);
            if (field == null)
                break;

            if (field.WireType == 2 && field.BytesValue != null)
            {
                if (field.FieldNumber == 1)
                {
                    ParseEntriesStream(field.BytesValue, result);
                }
                else
                {
                    ParseStatesBlob(field.BytesValue, result);
                }
            }

            offset = nextOffset;
        }

        return result;
    }

    private static void ParseStatesBlob(byte[] blob, Dictionary<string, object?> outDict)
    {
        int pos = 0;
        while (pos < blob.Length)
        {
            var (field, nextPos) = ProtobufWireCodec.DecodeField(blob, pos);
            if (field == null)
                break;

            if (field.WireType == 2 && field.BytesValue != null)
            {
                if (field.FieldNumber == 1)
                {
                    ParseEntriesStream(field.BytesValue, outDict);
                }
                else
                {
                    var (path, value) = ParseStateEntry(field.BytesValue);
                    if (!string.IsNullOrEmpty(path))
                        outDict[path] = value;
                }
            }

            pos = nextPos;
        }
    }

    private static void ParseEntriesStream(byte[] stream, Dictionary<string, object?> outDict)
    {
        int pos = 0;
        while (pos < stream.Length)
        {
            if (stream[pos] != 0x0A)
                break;

            var (len, i) = ProtobufWireCodec.ReadVarint(stream, pos + 1);
            int entryLen = (int)len;
            if (i + entryLen > stream.Length)
                break;

            var chunk = stream.AsSpan(i, entryLen).ToArray();
            var (path, value) = ParseStateEntry(chunk);
            if (!string.IsNullOrEmpty(path))
                outDict[path] = value;

            pos = i + entryLen;
        }
    }

    private static (string? path, object? value) ParseStateEntry(byte[] payload)
    {
        string? path = null;
        var fields = new Dictionary<int, DecodedField>();
        int pos = 0;
        while (pos < payload.Length)
        {
            var (field, nextPos) = ProtobufWireCodec.DecodeField(payload, pos);
            if (field == null)
                break;

            if (field.FieldNumber == 1 && field.WireType == 2 && field.BytesValue != null)
            {
                path = Encoding.UTF8.GetString(field.BytesValue);
            }
            else
            {
                fields[field.FieldNumber] = field;
            }

            pos = nextPos;
        }

        object? value = null;
        if (fields.TryGetValue(4, out var f4) && f4.BytesValue != null && f4.BytesValue.Length > 0)
        {
            var (sub, _) = ProtobufWireCodec.DecodeField(f4.BytesValue, 0);
            if (sub?.BytesValue != null)
                value = Encoding.UTF8.GetString(sub.BytesValue);
        }
        else if (fields.TryGetValue(2, out var f2))
        {
            if (f2.WireType == 0)
                value = (int)f2.VarintValue;
            else if (f2.BytesValue != null)
            {
                if (f2.BytesValue.Length == 0)
                    value = 0;
                else
                {
                    var (sub, _) = ProtobufWireCodec.DecodeField(f2.BytesValue, 0);
                    if (sub?.WireType == 0)
                        value = (int)sub.VarintValue;
                    else if (sub?.BytesValue != null)
                        value = Encoding.UTF8.GetString(sub.BytesValue);
                }
            }
        }
        else if (fields.TryGetValue(3, out var f3) && f3.BytesValue != null)
        {
            if (f3.BytesValue.Length == 0)
                value = false;
            else
            {
                var (sub, _) = ProtobufWireCodec.DecodeField(f3.BytesValue, 0);
                if (sub?.WireType == 0)
                    value = sub.VarintValue != 0;
            }
        }

        return (path, value);
    }
}
