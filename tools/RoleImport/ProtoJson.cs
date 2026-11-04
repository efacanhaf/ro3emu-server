using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace RoleImport;

/// <summary>
/// Reads the exporter's JSON (the client's LIVE tables dumped through the probe) into the server's own
/// generated protobuf messages.
///
/// Google.Protobuf's <c>JsonParser</c> is deliberately not used: it is a protobuf-JSON parser, and the
/// probe dump is not protobuf-JSON. Three differences matter here:
///   * int64 values arrive as plain JSON numbers and JsonParser reads them through a double, which
///     turns the official item guids (~3e17) into a different number (verified: 302230089544616186
///     came back as 302230089544616192). This reader takes the raw JSON text and parses it exactly.
///   * a repeated field arrives as a Lua table, i.e. a JSON object keyed "1".."n", not an array.
///   * the dump carries oneof case names and other Lua-only keys, which are skipped instead of
///     failing the whole message.
/// </summary>
public static class ProtoJson
{
    /// <summary>Builds a new message of <paramref name="descriptor"/>'s type from the dump object.</summary>
    public static IMessage Parse(MessageDescriptor descriptor, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{descriptor.FullName}: expected a JSON object, got {value.ValueKind}.");
        var message = New(descriptor);
        Merge(message, descriptor, value);
        return message;
    }

    public static IMessage New(MessageDescriptor descriptor) =>
        (IMessage)Activator.CreateInstance(descriptor.ClrType)!;

    private static void Merge(IMessage message, MessageDescriptor descriptor, JsonElement value)
    {
        foreach (var property in value.EnumerateObject())
        {
            var field = descriptor.FindFieldByName(property.Name);
            // Oneof case names ("val" in ItemAttrValue) and Lua-only keys: nothing to store.
            if (field == null) continue;
            if (property.Value.ValueKind == JsonValueKind.Null) continue;
            if (field.IsMap)
            {
                SetMap(message, field, property.Value);
                continue;
            }
            if (field.IsRepeated)
            {
                var list = (System.Collections.IList)field.Accessor.GetValue(message);
                foreach (var element in Elements(property.Value))
                    list.Add(Value(element, field));
                continue;
            }
            field.Accessor.SetValue(message, Value(property.Value, field));
        }
    }

    /// <summary>A repeated field: a JSON array, or a Lua table's numeric keys in ascending order.</summary>
    private static IEnumerable<JsonElement> Elements(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray()) yield return element;
            yield break;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var element in value.EnumerateObject()
                .OrderBy(p => long.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long key) ? key : long.MaxValue))
                yield return element.Value;
            yield break;
        }
        // A single value where the schema wants a list.
        yield return value;
    }

    private static void SetMap(IMessage message, FieldDescriptor field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException($"{field.FullName}: expected a map object.");
        // Map fields are read-only through the accessor: fill the map instance it returns.
        var map = (System.Collections.IDictionary)field.Accessor.GetValue(message);
        var keyField = field.MessageType.FindFieldByNumber(1);
        var valueField = field.MessageType.FindFieldByNumber(2);
        foreach (var entry in value.EnumerateObject())
        {
            object key;
            try { key = Scalar(JsonDocument.Parse(JsonSerializer.Serialize(entry.Name)).RootElement, keyField); }
            catch (Exception e) when (e is FormatException or InvalidOperationException)
            { throw new FormatException($"{field.FullName}: map key '{entry.Name}' is not a {keyField.FieldType}.", e); }
            if (map.Contains(key)) throw new FormatException($"{field.FullName}: duplicate map key '{entry.Name}'.");
            map[key] = Value(entry.Value, valueField);
        }
    }

    private static object Value(JsonElement value, FieldDescriptor field) =>
        field.FieldType switch
        {
            FieldType.Message or FieldType.Group => Parse(field.MessageType, value),
            _ => Scalar(value, field),
        };

    /// <summary>One scalar; integers keep every digit (raw text, never a double).</summary>
    private static object Scalar(JsonElement value, FieldDescriptor field)
    {
        if (field.FieldType == FieldType.Enum)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var named = field.EnumType.FindValueByName(value.GetString()!)
                    ?? throw new FormatException($"{field.FullName}: unknown enum name '{value.GetString()}'.");
                return named.Number;
            }
            return (int)CheckedInt(value, field);
        }
        return field.FieldType switch
        {
            FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => CheckedLong(value, field),
            FieldType.UInt64 or FieldType.Fixed64 => CheckedULong(value, field),
            FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => (int)CheckedInt(value, field),
            FieldType.UInt32 or FieldType.Fixed32 => (uint)CheckedULong(value, field),
            FieldType.Bool => Bool(value, field),
            FieldType.String => value.GetString() ?? "",
            FieldType.Bytes => Convert.FromBase64String(value.GetString() ?? ""),
            FieldType.Double => Double(value, field),
            FieldType.Float => (float)Double(value, field),
            _ => throw new FormatException($"{field.FullName}: unsupported field type {field.FieldType}."),
        };
    }

    private static long CheckedLong(JsonElement value, FieldDescriptor field) =>
        value.ValueKind switch
        {
            // Raw text: 302230089544616186 stays that number.
            JsonValueKind.Number when value.TryGetInt64(out long number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => throw new FormatException($"{field.FullName}: '{value}' is not an int64."),
        };

    private static ulong CheckedULong(JsonElement value, FieldDescriptor field) =>
        value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt64(out ulong number) => number,
            JsonValueKind.String when ulong.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed) => parsed,
            _ => throw new FormatException($"{field.FullName}: '{value}' is not a uint64."),
        };

    private static long CheckedInt(JsonElement value, FieldDescriptor field)
    {
        long number = CheckedLong(value, field);
        if (number is < int.MinValue or > int.MaxValue) throw new FormatException($"{field.FullName}: {number} does not fit an int32.");
        return number;
    }
    private static bool Bool(JsonElement value, FieldDescriptor field) =>
        value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"{field.FullName}: '{value}' is not a boolean."),
        };

    private static double Double(JsonElement value, FieldDescriptor field) =>
        value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when value.GetString() == "NaN" => double.NaN,
            JsonValueKind.String when value.GetString() == "Infinity" => double.PositiveInfinity,
            JsonValueKind.String when value.GetString() == "-Infinity" => double.NegativeInfinity,
            _ => throw new FormatException($"{field.FullName}: '{value}' is not a number."),
        };
}
