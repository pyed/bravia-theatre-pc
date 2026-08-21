using System;
using System.IO;

namespace BraviaTheatre.Core.Wire;

/// <summary>
/// Low-level Protobuf wire-format encoder/decoder primitives for custom Sony Seed wire frames.
/// </summary>
public static class ProtobufWireCodec
{
    private const int MaxVarintBytes = 10;
    private const int MaxGroupDepth = 64;
    private const ulong MaxFieldNumber = (1UL << 29) - 1;

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
        if (!TryReadVarint(data, pos, out var value, out var nextPos))
            throw new InvalidDataException("Invalid or truncated protobuf varint.");

        return (value, nextPos);
    }

    public static bool TryReadVarint(
        ReadOnlySpan<byte> data,
        int pos,
        out ulong value,
        out int nextPos)
    {
        value = 0;
        nextPos = pos;

        if ((uint)pos > (uint)data.Length)
            return false;

        for (var byteIndex = 0; byteIndex < MaxVarintBytes; byteIndex++)
        {
            if (nextPos >= data.Length)
                return false;

            var b = data[nextPos++];

            // A ulong varint can use only bit zero of its tenth byte.
            if (byteIndex == MaxVarintBytes - 1 && (b & 0xFE) != 0)
                return false;

            value |= (ulong)(b & 0x7F) << (byteIndex * 7);
            if ((b & 0x80) == 0)
                return true;
        }

        return false;
    }

    public static (DecodedField? field, int nextPos) DecodeField(ReadOnlySpan<byte> data, int pos)
    {
        return TryDecodeField(data, pos, out var field, out var nextPos)
            ? (field, nextPos)
            : (null, pos);
    }

    public static bool TryDecodeField(
        ReadOnlySpan<byte> data,
        int pos,
        out DecodedField? field,
        out int nextPos)
    {
        field = null;
        nextPos = pos;

        if ((uint)pos >= (uint)data.Length
            || !TryReadVarint(data, pos, out var header, out var valuePos))
        {
            return false;
        }

        var fieldNumberValue = header >> 3;
        var wireType = (int)(header & 0x07);
        if (fieldNumberValue is 0 or > MaxFieldNumber || wireType is 4 or 6 or 7)
            return false;

        var fieldNumber = (int)fieldNumberValue;
        switch (wireType)
        {
            case 0:
                if (!TryReadVarint(data, valuePos, out var value, out nextPos))
                    return false;
                field = new DecodedField(fieldNumber, wireType, value, null);
                return true;

            case 1:
                if (!TryTakeBytes(data, valuePos, sizeof(ulong), out var fixed64, out nextPos))
                    return false;
                field = new DecodedField(fieldNumber, wireType, 0, fixed64);
                return true;

            case 2:
                if (!TryReadVarint(data, valuePos, out var lengthValue, out var bodyPos)
                    || lengthValue > int.MaxValue
                    || lengthValue > (ulong)(data.Length - bodyPos))
                {
                    return false;
                }

                var length = (int)lengthValue;
                field = new DecodedField(
                    fieldNumber,
                    wireType,
                    0,
                    data.Slice(bodyPos, length).ToArray());
                nextPos = bodyPos + length;
                return true;

            case 3:
                if (!TrySkipGroup(data, valuePos, fieldNumber, 0, out nextPos))
                    return false;
                field = new DecodedField(fieldNumber, wireType, 0, null);
                return true;

            case 5:
                if (!TryTakeBytes(data, valuePos, sizeof(uint), out var fixed32, out nextPos))
                    return false;
                field = new DecodedField(fieldNumber, wireType, 0, fixed32);
                return true;

            default:
                return false;
        }
    }

    private static bool TryTakeBytes(
        ReadOnlySpan<byte> data,
        int pos,
        int length,
        out byte[] bytes,
        out int nextPos)
    {
        bytes = Array.Empty<byte>();
        nextPos = pos;
        if ((uint)pos > (uint)data.Length || length > data.Length - pos)
            return false;

        bytes = data.Slice(pos, length).ToArray();
        nextPos = pos + length;
        return true;
    }

    private static bool TrySkipGroup(
        ReadOnlySpan<byte> data,
        int pos,
        int groupFieldNumber,
        int depth,
        out int nextPos)
    {
        nextPos = pos;
        if (depth >= MaxGroupDepth)
            return false;

        while (nextPos < data.Length)
        {
            if (!TryReadVarint(data, nextPos, out var header, out var valuePos))
                return false;

            var fieldNumberValue = header >> 3;
            var wireType = (int)(header & 0x07);
            if (fieldNumberValue is 0 or > MaxFieldNumber || wireType is 6 or 7)
                return false;

            var fieldNumber = (int)fieldNumberValue;
            if (wireType == 4)
            {
                if (fieldNumber != groupFieldNumber)
                    return false;
                nextPos = valuePos;
                return true;
            }

            if (!TrySkipValue(data, valuePos, fieldNumber, wireType, depth, out nextPos))
                return false;
        }

        return false;
    }

    private static bool TrySkipValue(
        ReadOnlySpan<byte> data,
        int valuePos,
        int fieldNumber,
        int wireType,
        int depth,
        out int nextPos)
    {
        nextPos = valuePos;
        switch (wireType)
        {
            case 0:
                return TryReadVarint(data, valuePos, out _, out nextPos);
            case 1:
                if (data.Length - valuePos < sizeof(ulong)) return false;
                nextPos = valuePos + sizeof(ulong);
                return true;
            case 2:
                if (!TryReadVarint(data, valuePos, out var lengthValue, out var bodyPos)
                    || lengthValue > int.MaxValue
                    || lengthValue > (ulong)(data.Length - bodyPos))
                {
                    return false;
                }
                nextPos = bodyPos + (int)lengthValue;
                return true;
            case 3:
                return TrySkipGroup(data, valuePos, fieldNumber, depth + 1, out nextPos);
            case 5:
                if (data.Length - valuePos < sizeof(uint)) return false;
                nextPos = valuePos + sizeof(uint);
                return true;
            default:
                return false;
        }
    }
}

public sealed record DecodedField(int FieldNumber, int WireType, ulong VarintValue, byte[]? BytesValue);
