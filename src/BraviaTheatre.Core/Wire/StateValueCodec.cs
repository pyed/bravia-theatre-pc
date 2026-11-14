using System.Collections.Generic;
using System.Text;

namespace BraviaTheatre.Core.Wire;

/// <summary>Decodes a state entry's value the same way for snapshots and notifications.</summary>
/// <remarks>
/// Both carry the same entry: the path in field 1 and the value in field 2, 3, 4 or 5. They used to
/// be decoded by two functions that disagreed on the order of the fields and on field 2: a
/// notification read a string nested there as its raw bytes, tag and length included, or as a hex
/// string. These are the snapshot's rules, which every connection's first state and every keepalive
/// already rely on.
/// </remarks>
internal static class StateValueCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <param name="fields">The entry's fields by number (the last occurrence of each).</param>
    public static object? Decode(IReadOnlyDictionary<int, DecodedField> fields)
    {
        if (fields.TryGetValue(4, out var f4) && f4.BytesValue is { Length: > 0 })
            return DecodeNestedString(f4.BytesValue);

        if (fields.TryGetValue(2, out var f2))
        {
            if (f2.WireType == 0)
                return DecodeSignedInteger(f2.VarintValue);
            if (f2.BytesValue is null)
                return null;
            if (f2.BytesValue.Length == 0)
                return 0;
            if (!ProtobufWireCodec.TryDecodeField(f2.BytesValue, 0, out var nested, out _))
                return null;
            if (nested is { WireType: 0 })
                return DecodeSignedInteger(nested.VarintValue);
            return nested is { FieldNumber: 1, WireType: 2, BytesValue: not null }
                ? DecodeUtf8(nested.BytesValue)
                : null;
        }

        if (fields.TryGetValue(3, out var f3) && f3.BytesValue != null)
        {
            if (f3.BytesValue.Length == 0)
                return false;
            return ProtobufWireCodec.TryDecodeField(f3.BytesValue, 0, out var nested, out _)
                && nested is { WireType: 0 }
                    ? nested.VarintValue != 0
                    : null;
        }

        if (fields.TryGetValue(5, out var f5) && f5.BytesValue is { Length: > 0 })
            return DecodeNestedString(f5.BytesValue);

        return null;
    }

    internal static string? DecodeUtf8(byte[] bytes)
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
}
