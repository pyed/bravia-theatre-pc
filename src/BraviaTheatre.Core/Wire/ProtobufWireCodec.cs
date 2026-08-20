using System;
using System.IO;

namespace BraviaTheatre.Core.Wire;

/// <summary>
/// Low-level Protobuf wire-format encoder/decoder primitives for custom Sony Seed wire frames.
/// </summary>
public static class ProtobufWireCodec
{
    public static byte[] EncodeVarint(ulong value)
    {
        using var ms = new MemoryStream();
        while (value > 0x7F)
        {
            ms.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        ms.WriteByte((byte)value);
        return ms.ToArray();
    }

    public static byte[] EncodeVarint(long value) => EncodeVarint((ulong)value);
    public static byte[] EncodeVarint(int value) => EncodeVarint((ulong)value);

    public static byte[] LengthDelimited(int fieldNumber, byte[] body)
    {
        var tagBytes = EncodeVarint((fieldNumber << 3) | 2);
        var lenBytes = EncodeVarint(body.Length);
        var res = new byte[tagBytes.Length + lenBytes.Length + body.Length];
        Buffer.BlockCopy(tagBytes, 0, res, 0, tagBytes.Length);
        Buffer.BlockCopy(lenBytes, 0, res, tagBytes.Length, lenBytes.Length);
        Buffer.BlockCopy(body, 0, res, tagBytes.Length + lenBytes.Length, body.Length);
        return res;
    }

    public static (ulong value, int nextPos) ReadVarint(ReadOnlySpan<byte> data, int pos)
    {
        ulong value = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
        }
        return (value, pos);
    }

    public static (DecodedField? field, int nextPos) DecodeField(ReadOnlySpan<byte> data, int pos)
    {
        if (pos >= data.Length)
            return (null, pos);

        var (header, tagPos) = ReadVarint(data, pos);
        int fieldNumber = (int)(header >> 3);
        int wireType = (int)(header & 0x07);

        if (wireType == 0) // Varint
        {
            var (val, nextPos) = ReadVarint(data, tagPos);
            return (new DecodedField(fieldNumber, wireType, val, null), nextPos);
        }
        else if (wireType == 2) // Length-delimited
        {
            var (len, bodyPos) = ReadVarint(data, tagPos);
            int length = (int)len;
            if (bodyPos + length > data.Length)
                return (null, pos);

            byte[] body = data.Slice(bodyPos, length).ToArray();
            return (new DecodedField(fieldNumber, wireType, 0, body), bodyPos + length);
        }

        return (null, pos);
    }
}

public sealed record DecodedField(int FieldNumber, int WireType, ulong VarintValue, byte[]? BytesValue);
