using System;
using System.Collections.Generic;

namespace BraviaTheatre.Core.Wire;

public static class NotifyParser
{
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

        object? value = StateValueCodec.Decode(fields);

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

    private static string? DecodeUtf8(byte[] bytes) => StateValueCodec.DecodeUtf8(bytes);
}
