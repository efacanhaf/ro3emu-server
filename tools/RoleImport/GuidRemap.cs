using System.Collections;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace RoleImport;

/// <summary>
/// Replaces the official item instance ids inside a message with the local ids the import created.
///
/// A guid is only ever referenced through an int64 (a field, a repeated element or a map key/value), and
/// official instance ids are ~3e17 while every other int64 in these datasets is orders of magnitude
/// smaller (levels, exp, timestamps, entries, attribute values). So instead of listing which field of
/// which message is a guid, the walk reprojects any int64 that the guid map knows: that covers the
/// equipment plan grids, the Soul Echo slots, the pet and totem selections and the bag's quick bar,
/// including messages that gain a guid field later.
/// </summary>
public static class GuidRemap
{
    public static void Apply(IMessage message, IReadOnlyDictionary<long, long> map)
    {
        if (map.Count == 0) return;
        Walk(message, map, 0);
    }

    private static void Walk(IMessage message, IReadOnlyDictionary<long, long> map, int depth)
    {
        if (depth > 32) throw new InvalidDataException("Guid remap nesting exceeds the decoder bound.");
        var descriptor = message.Descriptor;
        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            if (field.IsMap)
            {
                RemapMap(message, field, map, depth);
                continue;
            }
            if (field.IsRepeated)
            {
                if (field.FieldType == FieldType.Message) RemapRepeatedMessages(message, field, map, depth);
                else if (IsInt64(field.FieldType)) RemapRepeatedInt64(message, field, map);
                continue;
            }
            if (field.FieldType == FieldType.Message)
            {
                if (field.Accessor.GetValue(message) is IMessage child) Walk(child, map, depth + 1);
                continue;
            }
            if (IsInt64(field.FieldType)
                && field.Accessor.GetValue(message) is long value && map.TryGetValue(value, out long mapped))
                field.Accessor.SetValue(message, mapped);
        }
    }

    private static void RemapRepeatedMessages(IMessage message, FieldDescriptor field, IReadOnlyDictionary<long, long> map, int depth)
    {
        if (field.Accessor.GetValue(message) is not IEnumerable children) return;
        foreach (var child in children.OfType<IMessage>().ToArray()) Walk(child, map, depth + 1);
    }

    private static void RemapRepeatedInt64(IMessage message, FieldDescriptor field, IReadOnlyDictionary<long, long> map)
    {
        if (field.Accessor.GetValue(message) is not IList list) return;
        for (int index = 0; index < list.Count; index++)
            if (list[index] is long value && map.TryGetValue(value, out long mapped)) list[index] = mapped;
    }

    private static void RemapMap(IMessage message, FieldDescriptor field, IReadOnlyDictionary<long, long> map, int depth)
    {
        if (field.Accessor.GetValue(message) is not IDictionary source || source.Count == 0) return;
        var keyField = field.MessageType.FindFieldByNumber(1);
        var valueField = field.MessageType.FindFieldByNumber(2);
        bool keyIsGuid = IsInt64(keyField.FieldType), valueIsGuid = IsInt64(valueField.FieldType);
        bool valueIsMessage = valueField.FieldType == FieldType.Message;
        if (!keyIsGuid && !valueIsGuid && !valueIsMessage) return;
        var target = new List<(object Key, object Value)>(source.Count);
        foreach (DictionaryEntry entry in source)
        {
            object key = entry.Key, value = entry.Value!;
            if (keyIsGuid && key is long guid && map.TryGetValue(guid, out long mappedKey)) key = mappedKey;
            if (valueIsGuid && value is long guidValue && map.TryGetValue(guidValue, out long mappedValue)) value = mappedValue;
            if (valueIsMessage && value is IMessage child) Walk(child, map, depth + 1);
            target.Add((key, value));
        }
        source.Clear();
        foreach (var (key, value) in target) source[key] = value;
    }

    private static bool IsInt64(FieldType type) => type is FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64;
}
