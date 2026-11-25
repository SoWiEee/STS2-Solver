using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

static class RelicText
{
    public static Dictionary<string, string> Load(string gameDirectory) => Load(gameDirectory, "relics");

    /// <summary>One zht localization table (localization/zht/{table}.json); empty when it cannot be read.</summary>
    public static Dictionary<string, string> Load(string gameDirectory, string table)
    {
        try
        {
            string path = Path.Combine(gameDirectory, "SlayTheSpire2.pck");
            var index = PackIndex(path);
            if (!index.TryGetValue($"localization/zht/{table}.json", out var entry)) return new();
            using var pack = File.OpenRead(path);
            pack.Position = entry.Offset;
            byte[] bytes = new byte[entry.Size];
            pack.ReadExactly(bytes);
            return MD5.HashData(bytes).AsSpan().SequenceEqual(entry.Md5)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? new()
                : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new();
        }
    }

    static (string Key, Dictionary<string, (long Offset, int Size, byte[] Md5)> Index)? packCache;

    /// <summary>
    /// Godot 4 PCK (format 3) directory: header "GDPC", version, engine version, flags, file base,
    /// directory offset; each entry is path, offset (from the file base), size, MD5 and flags.
    /// </summary>
    static Dictionary<string, (long Offset, int Size, byte[] Md5)> PackIndex(string path)
    {
        string key = path + File.GetLastWriteTimeUtc(path).Ticks;
        if (packCache is { } cached && cached.Key == key) return cached.Index;
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadUInt32() != 0x43504447 || reader.ReadUInt32() != 3) throw new InvalidDataException("不支援的 PCK 格式");
        reader.BaseStream.Position += 12; // engine major, minor, patch
        uint flags = reader.ReadUInt32();
        long fileBase = (long)reader.ReadUInt64();
        long directory = (long)reader.ReadUInt64();
        if ((flags & 1) != 0) throw new InvalidDataException("加密的 PCK 目錄");
        long baseOffset = (flags & 2) != 0 ? fileBase : 0; // PACK_REL_FILEBASE
        reader.BaseStream.Position = directory;
        int count = reader.ReadInt32();
        var index = new Dictionary<string, (long, int, byte[])>(count);
        for (int i = 0; i < count; i++)
        {
            int length = reader.ReadInt32();
            string name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length)).TrimEnd('\0');
            long offset = (long)reader.ReadUInt64();
            long size = (long)reader.ReadUInt64();
            byte[] md5 = reader.ReadBytes(16);
            reader.ReadUInt32(); // flags
            if (name.StartsWith("res://", StringComparison.Ordinal)) name = name[6..];
            if (size <= int.MaxValue) index[name] = (baseOffset + offset, (int)size, md5);
        }
        packCache = (key, index);
        return index;
    }

    public static string? Describe(string? template, IReadOnlyDictionary<string, decimal> values)
    {
        if (template == null) return null;
        string plain = Regex.Replace(template, @"\[/?[A-Za-z][A-Za-z0-9]*(?:=[^\]\r\n]*)?\]", "");
        // Complex SmartFormat expressions need the game's context; keep the independent sentences.
        return Regex.Replace(plain,
            @"\{(\w+)(?::(energyIcons\(\)|diff\(\)))?\}|\{(?:[^{}]|(?<nested>\{)|(?<-nested>\}))*(?(nested)(?!))\}",
            match =>
        {
            if (!values.TryGetValue(match.Groups[1].Value, out decimal value)) return "（依遊戲狀態）";
            return value.ToString(CultureInfo.InvariantCulture) + (match.Groups[2].Value == "energyIcons()" ? " 點能量" : "");
        });
    }
}
