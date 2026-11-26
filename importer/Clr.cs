using Microsoft.Diagnostics.Runtime;

namespace Sts2Solver.LiveImport;

/// <summary>Thrown when the live state cannot be represented; the message names what is missing.</summary>
public sealed class LiveUnsupportedException(string message) : Exception(message);

/// <summary>Read-only ClrMD field access over the game's heap. Every miss is an explicit failure.</summary>
internal static class Clr
{
    public static string Backing(string name) => $"<{name}>k__BackingField";

    public static void Require(bool ok, string message)
    {
        if (!ok) throw new LiveUnsupportedException(message);
    }

    static ClrInstanceField FieldOf(ClrObject obj, string name)
    {
        for (ClrType? type = obj.Type; type != null; type = type.BaseType)
            foreach (ClrInstanceField field in type.Fields)
                if (field.Name == name) return field;
        throw new LiveUnsupportedException($"{obj.Type?.Name}.{name} 欄位不存在");
    }

    static string Resolve(ClrObject obj, string name)
    {
        for (ClrType? type = obj.Type; type != null; type = type.BaseType)
            foreach (ClrInstanceField field in type.Fields)
                if (field.Name == name || field.Name == Backing(name) || field.Name == "_" + char.ToLowerInvariant(name[0]) + name[1..])
                    return field.Name!; // property, auto-property backing field, or the C# _camelCase field behind it
        throw new LiveUnsupportedException($"{obj.Type?.Name}.{name} 欄位不存在");
    }

    public static bool Has(ClrObject obj, string name)
    {
        for (ClrType? type = obj.Type; type != null; type = type.BaseType)
            foreach (ClrInstanceField field in type.Fields)
                if (field.Name == name || field.Name == Backing(name) || field.Name == "_" + char.ToLowerInvariant(name[0]) + name[1..]) return true;
        return false;
    }

    /// <summary>Reads a primitive or enum field (name or auto-property name).</summary>
    public static T Get<T>(ClrObject obj, string name) where T : unmanaged
    {
        Require(!obj.IsNull, $"讀取 {name} 時物件為 null");
        return obj.ReadField<T>(Resolve(obj, name));
    }

    public static ClrObject Obj(ClrObject obj, string name)
    {
        Require(!obj.IsNull, $"讀取 {name} 時物件為 null");
        return obj.ReadObjectField(Resolve(obj, name));
    }

    public static string? Str(ClrObject obj, string name)
    {
        Require(!obj.IsNull, $"讀取 {name} 時物件為 null");
        return obj.ReadStringField(Resolve(obj, name));
    }

    public static ClrValueType Struct(ClrObject obj, string name) => obj.ReadValueTypeField(Resolve(obj, name));

    /// <summary>Reads a <c>Nullable&lt;T&gt;</c> field.</summary>
    public static T? Nullable<T>(ClrObject obj, string name) where T : unmanaged
    {
        ClrValueType value = Struct(obj, name);
        return value.ReadField<bool>("hasValue") ? value.ReadField<T>("value") : null;
    }

    /// <summary>Content id (<c>AbstractModel.Id.Entry</c>), e.g. "STRIKE_IRONCLAD".</summary>
    public static string Id(ClrObject model)
    {
        ClrObject id = Obj(model, "Id");
        return Str(id, "Entry") ?? throw new LiveUnsupportedException($"{model.Type?.Name} 沒有內容 id");
    }

    public static string TypeName(ClrObject obj) => obj.Type?.Name?.Split('.').Last() ?? "?";

    public static bool Is(ClrObject obj, string fullName)
    {
        for (ClrType? type = obj.Type; type != null; type = type.BaseType)
            if (type.Name == fullName) return true;
        return false;
    }

    public static List<ClrObject> List(ClrObject list, int limit = 4096, bool allowNull = false)
    {
        Require(!list.IsNull, "清單為 null");
        int size = list.ReadField<int>("_size");
        ClrArray items = list.ReadObjectField("_items").AsArray();
        Require(size >= 0 && size <= limit && size <= items.Length, $"清單長度 {size} 超出範圍");
        var result = new List<ClrObject>(size);
        for (int i = 0; i < size; i++)
        {
            ClrObject item = items.GetObjectValue(i);
            Require(allowNull || !item.IsNull, "清單含有 null");
            result.Add(item);
        }
        return result;
    }

    /// <summary>Elements of a <c>List&lt;T&gt;</c> whose T is a value type.</summary>
    public static List<ClrValueType> StructList(ClrObject list, int limit = 4096)
    {
        int size = list.ReadField<int>("_size");
        ClrArray items = list.ReadObjectField("_items").AsArray();
        Require(size >= 0 && size <= limit && size <= items.Length, $"清單長度 {size} 超出範圍");
        return Enumerable.Range(0, size).Select(items.GetStructValue).ToList();
    }

    /// <summary>Live entries of a <c>HashSet&lt;T&gt;</c> or <c>Dictionary&lt;K,V&gt;</c> (both use _entries/_count).</summary>
    public static IEnumerable<ClrValueType> HashEntries(ClrObject set)
    {
        if (set.IsNull) yield break;
        int count = set.ReadField<int>("_count");
        if (count == 0) yield break;
        ClrArray entries = set.ReadObjectField("_entries").AsArray();
        Require(count <= entries.Length, "雜湊表長度超出範圍");
        for (int i = 0; i < count; i++)
        {
            ClrValueType entry = entries.GetStructValue(i);
            if (entry.ReadField<int>(entry.Type!.Fields.Any(f => f.Name == "Next") ? "Next" : "next") < -1) continue;
            yield return entry;
        }
    }

    public static IEnumerable<int> IntSet(ClrObject set) => HashEntries(set).Select(e => e.ReadField<int>("Value"));
}
