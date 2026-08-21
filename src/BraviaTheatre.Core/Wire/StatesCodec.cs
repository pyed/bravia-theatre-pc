using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BraviaTheatre.Core.Wire;

public static class StatesCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] BuildSingleGetStatesRequest(
        string hmacKeyHex,
        string fieldPath,
        byte[] sessionRandom,
        string sessionId)
    {
        var pathBytes = Encoding.UTF8.GetBytes(fieldPath);
        var depth2 = ProtobufWireCodec.LengthDelimited(1, pathBytes);
        var depth1 = ProtobufWireCodec.LengthDelimited(1, depth2);

        var sessionIdBytes = Encoding.UTF8.GetBytes(sessionId);
        using var msEmbedded = new MemoryStream();
        var fRand = ProtobufWireCodec.LengthDelimited(1, sessionRandom);
        msEmbedded.Write(fRand, 0, fRand.Length);
        var fId = ProtobufWireCodec.LengthDelimited(3, sessionIdBytes);
        msEmbedded.Write(fId, 0, fId.Length);
        var embeddedData = msEmbedded.ToArray();
        var embeddedField = ProtobufWireCodec.LengthDelimited(2, embeddedData);

        var inner = new byte[depth1.Length + embeddedField.Length];
        Buffer.BlockCopy(depth1, 0, inner, 0, depth1.Length);
        Buffer.BlockCopy(embeddedField, 0, inner, depth1.Length, embeddedField.Length);

        var authToken = PacketSigner.ComputeHmac(hmacKeyHex, inner);
        var fieldListBytes = ProtobufWireCodec.LengthDelimited(1, inner);
        var authBytes = ProtobufWireCodec.LengthDelimited(2, authToken);

        var result = new byte[fieldListBytes.Length + authBytes.Length];
        Buffer.BlockCopy(fieldListBytes, 0, result, 0, fieldListBytes.Length);
        Buffer.BlockCopy(authBytes, 0, result, fieldListBytes.Length, authBytes.Length);
        return result;
    }

    public static byte[] BuildGetStatesRequest(
        string hmacKeyHex,
        IEnumerable<string> fieldPaths,
        byte[] sessionRandom,
        string sessionId)
    {
        var pathList = new List<string>(fieldPaths);
        if (pathList.Count == 1)
            return BuildSingleGetStatesRequest(hmacKeyHex, pathList[0], sessionRandom, sessionId);

        using var msInnerParts = new MemoryStream();
        foreach (var path in pathList)
        {
            var pathBytes = Encoding.UTF8.GetBytes(path);
            var field = ProtobufWireCodec.LengthDelimited(1, pathBytes);
            msInnerParts.Write(field, 0, field.Length);
        }

        var nestedField = ProtobufWireCodec.LengthDelimited(1, msInnerParts.ToArray());
        var sessionIdBytes = Encoding.UTF8.GetBytes(sessionId);
        using var msEmbedded = new MemoryStream();
        var fRand = ProtobufWireCodec.LengthDelimited(1, sessionRandom);
        msEmbedded.Write(fRand, 0, fRand.Length);
        var fId = ProtobufWireCodec.LengthDelimited(3, sessionIdBytes);
        msEmbedded.Write(fId, 0, fId.Length);
        var embeddedField = ProtobufWireCodec.LengthDelimited(2, msEmbedded.ToArray());

        var field1Content = new byte[nestedField.Length + embeddedField.Length];
        Buffer.BlockCopy(nestedField, 0, field1Content, 0, nestedField.Length);
        Buffer.BlockCopy(embeddedField, 0, field1Content, nestedField.Length, embeddedField.Length);

        var authToken = PacketSigner.ComputeHmac(hmacKeyHex, field1Content);
        var fieldListBytes = ProtobufWireCodec.LengthDelimited(1, field1Content);
        var authBytes = ProtobufWireCodec.LengthDelimited(2, authToken);

        var result = new byte[fieldListBytes.Length + authBytes.Length];
        Buffer.BlockCopy(fieldListBytes, 0, result, 0, fieldListBytes.Length);
        Buffer.BlockCopy(authBytes, 0, result, fieldListBytes.Length, authBytes.Length);
        return result;
    }

    public static (byte[]? sessionRandom, byte[]? authToken, string? sessionId) ExtractSessionTokens(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        byte[]? sessionRandom = null;
        byte[]? authToken = null;
        string? sessionId = null;

        var offset = 0;
        while (offset < raw.Length)
        {
            if (!ProtobufWireCodec.TryDecodeField(raw, offset, out var field, out var nextOffset)
                || field == null)
            {
                break;
            }

            if (field.WireType == 2 && field.BytesValue != null)
            {
                if (field.FieldNumber == 2 && field.BytesValue.Length == 8)
                    sessionRandom = field.BytesValue;
                else if (field.FieldNumber == 3)
                    sessionId = DecodeUtf8(field.BytesValue);
                else if (field.FieldNumber == 4 && field.BytesValue.Length == 32)
                    authToken = field.BytesValue;

                // Theatre responses commonly wrap states plus rolling metadata in
                // top-level field 2. Inspect known metadata fields only.
                if (field.FieldNumber == 2 && field.BytesValue.Length != 8)
                {
                    ExtractNestedSessionTokens(
                        field.BytesValue,
                        ref sessionRandom,
                        ref authToken,
                        ref sessionId);
                }
            }

            offset = nextOffset;
        }

        return (sessionRandom, authToken, sessionId);
    }

    public static Dictionary<string, object?> ParseGetStatesResponse(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var result = new Dictionary<string, object?>();
        var offset = 0;
        while (offset < raw.Length)
        {
            if (!ProtobufWireCodec.TryDecodeField(raw, offset, out var field, out var nextOffset)
                || field == null)
            {
                break;
            }

            if (field.WireType == 2 && field.BytesValue != null)
            {
                if (field.FieldNumber == 1)
                    ParseEntriesStream(field.BytesValue, result);
                else if (field.FieldNumber == 2)
                    ParseStatesEnvelope(field.BytesValue, result);
            }

            offset = nextOffset;
        }

        return result;
    }

    private static void ParseStatesEnvelope(byte[] envelope, Dictionary<string, object?> output)
    {
        var pos = 0;
        while (pos < envelope.Length)
        {
            if (!ProtobufWireCodec.TryDecodeField(envelope, pos, out var field, out var nextPos)
                || field == null)
            {
                break;
            }

            // Field 1 is the states stream. Fields 2-4 carry rolling auth/session
            // metadata and must never be interpreted as state entries.
            if (field.FieldNumber == 1 && field.WireType == 2 && field.BytesValue != null)
                ParseEntriesStream(field.BytesValue, output);

            pos = nextPos;
        }
    }

    private static void ParseEntriesStream(byte[] stream, Dictionary<string, object?> output)
    {
        var pos = 0;
        while (pos < stream.Length)
        {
            if (!ProtobufWireCodec.TryDecodeField(stream, pos, out var field, out var nextPos)
                || field == null)
            {
                break;
            }

            if (field.FieldNumber == 1 && field.WireType == 2 && field.BytesValue != null)
            {
                var (path, value) = ParseStateEntry(field.BytesValue);
                if (IsValidStatePath(path))
                    output[path!] = value;
            }

            pos = nextPos;
        }
    }

    private static (string? path, object? value) ParseStateEntry(byte[] payload)
    {
        string? path = null;
        var fields = new Dictionary<int, DecodedField>();
        var pos = 0;
        while (pos < payload.Length)
        {
            if (!ProtobufWireCodec.TryDecodeField(payload, pos, out var field, out var nextPos)
                || field == null)
            {
                break;
            }

            if (field.FieldNumber == 1 && field.WireType == 2 && field.BytesValue != null)
                path = DecodeUtf8(field.BytesValue);
            else
                fields[field.FieldNumber] = field;

            pos = nextPos;
        }

        object? value = null;
        if (fields.TryGetValue(4, out var f4) && f4.BytesValue is { Length: > 0 })
        {
            value = DecodeNestedString(f4.BytesValue);
        }
        else if (fields.TryGetValue(2, out var f2))
        {
            if (f2.WireType == 0)
            {
                value = DecodeSignedInteger(f2.VarintValue);
            }
            else if (f2.BytesValue != null)
            {
                if (f2.BytesValue.Length == 0)
                {
                    value = 0;
                }
                else if (ProtobufWireCodec.TryDecodeField(f2.BytesValue, 0, out var sub, out _))
                {
                    if (sub?.WireType == 0)
                        value = DecodeSignedInteger(sub.VarintValue);
                    else if (sub is { FieldNumber: 1, WireType: 2, BytesValue: not null })
                        value = DecodeUtf8(sub.BytesValue);
                }
            }
        }
        else if (fields.TryGetValue(3, out var f3) && f3.BytesValue != null)
        {
            if (f3.BytesValue.Length == 0)
            {
                value = false;
            }
            else if (ProtobufWireCodec.TryDecodeField(f3.BytesValue, 0, out var sub, out _)
                && sub?.WireType == 0)
            {
                value = sub.VarintValue != 0;
            }
        }
        else if (fields.TryGetValue(5, out var f5) && f5.BytesValue is { Length: > 0 })
        {
            value = DecodeNestedString(f5.BytesValue);
        }

        return (path, value);
    }

    private static void ExtractNestedSessionTokens(
        byte[] envelope,
        ref byte[]? sessionRandom,
        ref byte[]? authToken,
        ref string? sessionId)
    {
        var pos = 0;
        while (pos < envelope.Length)
        {
            if (!ProtobufWireCodec.TryDecodeField(envelope, pos, out var field, out var nextPos)
                || field == null)
            {
                break;
            }

            if (field.WireType == 2 && field.BytesValue != null)
            {
                if (field.FieldNumber == 2 && field.BytesValue.Length == 8)
                    sessionRandom = field.BytesValue;
                else if (field.FieldNumber == 2 && field.BytesValue.Length == 32)
                    authToken = field.BytesValue;
                else if (field.FieldNumber == 3)
                    sessionId = DecodeUtf8(field.BytesValue) ?? sessionId;
                else if (field.FieldNumber == 4 && field.BytesValue.Length == 32)
                    authToken = field.BytesValue;
            }

            pos = nextPos;
        }
    }

    private static object DecodeSignedInteger(ulong value)
    {
        var signed = unchecked((long)value);
        if (signed is >= int.MinValue and <= int.MaxValue)
            return (int)signed;
        return signed;
    }

    private static string? DecodeNestedString(byte[] bytes)
    {
        return ProtobufWireCodec.TryDecodeField(bytes, 0, out var field, out _)
            && field is { FieldNumber: 1, WireType: 2, BytesValue: not null }
                ? DecodeUtf8(field.BytesValue)
                : null;
    }

    private static bool IsValidStatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 256)
            return false;

        foreach (var c in path)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                return false;
        }

        return true;
    }

    private static string? DecodeUtf8(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
