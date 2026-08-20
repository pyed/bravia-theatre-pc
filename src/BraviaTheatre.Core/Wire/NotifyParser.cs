using System;
using System.Collections.Generic;
using System.Text;

namespace BraviaTheatre.Core.Wire;

public static class NotifyParser
{
    public static (string? path, object? value) ParseNotifyMessage(byte[] raw)
    {
        var fields = new Dictionary<int, DecodedField>();
        int pos = 0;
        while (pos < raw.Length)
        {
            var (field, nextPos) = ProtobufWireCodec.DecodeField(raw, pos);
            if (field == null)
                break;
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
        int offset = 0;
        var (outer, off1) = ProtobufWireCodec.DecodeField(payload, offset);
        if (outer == null || outer.FieldNumber != 1 || outer.BytesValue == null)
            return (null, null);

        var (inner, _) = ProtobufWireCodec.DecodeField(outer.BytesValue, 0);
        if (inner == null || inner.FieldNumber != 1 || inner.BytesValue == null)
            return (null, null);

        var fields = new Dictionary<int, DecodedField>();
        int pos = 0;
        while (pos < inner.BytesValue.Length)
        {
            var (field, nextPos) = ProtobufWireCodec.DecodeField(inner.BytesValue, pos);
            if (field == null)
                break;
            fields[field.FieldNumber] = field;
            pos = nextPos;
        }

        string? path = null;
        if (fields.TryGetValue(1, out var f1) && f1.BytesValue != null)
        {
            path = Encoding.UTF8.GetString(f1.BytesValue);
        }

        object? value = ExtractValue(fields);

        // Sound field reports as int on wire, map to bool
        if (path == "sound_field" && value is int intVal)
        {
            value = intVal != 0;
        }

        return (path, value);
    }

    private static object? ExtractValue(Dictionary<int, DecodedField> fields)
    {
        if (fields.TryGetValue(2, out var f2))
        {
            if (f2.WireType == 0)
                return (int)f2.VarintValue;
            if (f2.BytesValue != null)
            {
                if (f2.BytesValue.Length == 0)
                    return 0;

                var nestedInt = NestedVarint(f2.BytesValue);
                if (nestedInt.HasValue)
                    return nestedInt.Value;

                try
                {
                    var text = Encoding.UTF8.GetString(f2.BytesValue);
                    if (!string.IsNullOrEmpty(text))
                        return text;
                }
                catch
                {
                    // Fallback
                }
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
        var (field, _) = ProtobufWireCodec.DecodeField(payload, 0);
        if (field != null && field.FieldNumber == 1 && field.WireType == 0)
        {
            return (int)field.VarintValue;
        }
        return null;
    }

    private static string? NestedString(byte[] payload)
    {
        var (field, _) = ProtobufWireCodec.DecodeField(payload, 0);
        if (field != null && field.FieldNumber == 1 && field.WireType == 2 && field.BytesValue != null)
        {
            try
            {
                return Encoding.UTF8.GetString(field.BytesValue);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }
}
