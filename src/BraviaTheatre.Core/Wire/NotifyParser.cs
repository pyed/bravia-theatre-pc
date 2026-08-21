using System;
using System.Collections.Generic;
using System.Text;

namespace BraviaTheatre.Core.Wire;

public static class NotifyParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static (string? path, object? value) ParseNotifyMessage(byte[] raw)
    {
        var fields = new Dictionary<int, DecodedField>();
        int pos = 0;
        while (pos < raw.Length)
        {
            if (!ProtobufWireCodec.TryDecodeField(raw, pos, out var field, out var nextPos)
                || field == null)
            {
                break;
            }
            fields[field.FieldNumber] = field;
            pos = nextPos;
        }

        if (fields.TryGetValue(3, out var f3) && f3.BytesValue != null && f3.BytesValue.Length > 256)
        {
            return ("system_setting.application_list", null);
        }

        if (fields.TryGetValue(2, out var f2) && f2.BytesValue != null)
        {
            return DecodeNotifyDelta(f2.BytesValue);
        }

        return (null, null);
    }

    public static (string? path, object? value) DecodeNotifyDelta(byte[] payload)
    {
        if (!ProtobufWireCodec.TryDecodeField(payload, 0, out var outer, out _)
            || outer == null
            || outer.FieldNumber != 1
            || outer.WireType != 2
            || outer.BytesValue == null)
        {
            return (null, null);
        }

        if (!ProtobufWireCodec.TryDecodeField(outer.BytesValue, 0, out var inner, out _)
            || inner == null
            || inner.FieldNumber != 1
            || inner.WireType != 2
            || inner.BytesValue == null)
        {
            return (null, null);
        }

        var fields = new Dictionary<int, DecodedField>();
        int pos = 0;
        while (pos < inner.BytesValue.Length)
        {
            if (!ProtobufWireCodec.TryDecodeField(inner.BytesValue, pos, out var field, out var nextPos)
                || field == null)
            {
                break;
            }
            fields[field.FieldNumber] = field;
            pos = nextPos;
        }

        string? path = null;
        if (fields.TryGetValue(1, out var f1) && f1.BytesValue != null)
        {
            path = DecodeUtf8(f1.BytesValue);
        }

        object? value = ExtractValue(fields);

        // Some firmware uses the short wire path and reports its value as an int.
        if (path == "sound_field" && value is int intVal)
        {
            path = "sound_setting.sound_field";
            value = intVal != 0;
        }
        else if (path == "sound_field")
        {
            path = "sound_setting.sound_field";
        }

        return (path, value);
    }

    private static object? ExtractValue(Dictionary<int, DecodedField> fields)
    {
        if (fields.TryGetValue(2, out var f2))
        {
            if (f2.WireType == 0)
                return DecodeSignedInteger(f2.VarintValue);
            if (f2.BytesValue != null)
            {
                if (f2.BytesValue.Length == 0)
                    return 0;

                var nestedInt = NestedVarint(f2.BytesValue);
                if (nestedInt.HasValue)
                    return nestedInt.Value;

                var text = DecodeUtf8(f2.BytesValue);
                if (IsPrintable(text))
                    return text;
                return Convert.ToHexString(f2.BytesValue);
            }
        }

        if (fields.TryGetValue(3, out var f3))
        {
            if (f3.BytesValue != null)
            {
                if (f3.BytesValue.Length == 0)
                    return false;
                var nested = NestedVarint(f3.BytesValue);
                if (nested.HasValue)
                    return nested.Value != 0;
            }
        }

        foreach (var key in new[] { 4, 5 })
        {
            if (fields.TryGetValue(key, out var fk) && fk.BytesValue != null && fk.BytesValue.Length > 0)
            {
                var text = NestedString(fk.BytesValue);
                if (text != null)
                    return text;
            }
        }

        return null;
    }

    private static int? NestedVarint(byte[] payload)
    {
        if (ProtobufWireCodec.TryDecodeField(payload, 0, out var field, out _)
            && field != null
            && field.FieldNumber == 1
            && field.WireType == 0)
        {
            var decoded = DecodeSignedInteger(field.VarintValue);
            return decoded is int value ? value : null;
        }
        return null;
    }

    private static string? NestedString(byte[] payload)
    {
        if (ProtobufWireCodec.TryDecodeField(payload, 0, out var field, out _)
            && field != null
            && field.FieldNumber == 1
            && field.WireType == 2
            && field.BytesValue != null)
        {
            return DecodeUtf8(field.BytesValue);
        }
        return null;
    }

    private static object DecodeSignedInteger(ulong value)
    {
        var signed = unchecked((long)value);
        if (signed is >= int.MinValue and <= int.MaxValue)
            return (int)signed;
        return signed;
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

    private static bool IsPrintable(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
        {
            if (!char.IsControl(c) || c is '\r' or '\n' or '\t') continue;
            return false;
        }
        return true;
    }
}
