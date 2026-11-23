using System.IO.Compression;
using System.Text;

// Container for the frozen rules catalog: each entry is a RulesCodec-encoded Rules blob.
// Shared by the generator (tools/rules-catalog) and the reader.
public sealed record RulesCatalogEntry(RulesCatalogKind Kind, string Name, string[] Sources, byte[] Rules);

public enum RulesCatalogKind : byte
{
    // Card, power, relic, potion and pool tables merged from every Standard-mode root.
    Content = 0,
    // One monster variant: a Rules whose monster table holds only that monster at index 0.
    Monster = 1,
}

public static class RulesCatalogFile
{
    const uint Magic = 0x43535453; // "STSC"
    const ushort Version = 1;

    public static void Write(string path, IReadOnlyList<RulesCatalogEntry> entries)
    {
        using var file = File.Create(path);
        using var brotli = new BrotliStream(file, CompressionLevel.SmallestSize);
        using var writer = new BinaryWriter(brotli, Encoding.UTF8);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(entries.Count);
        foreach (var entry in entries)
        {
            writer.Write((byte)entry.Kind);
            writer.Write(entry.Name);
            writer.Write(entry.Sources.Length);
            foreach (string source in entry.Sources) writer.Write(source);
            writer.Write(entry.Rules.Length);
            writer.Write(entry.Rules);
        }
    }

    public static List<RulesCatalogEntry> Read(string path)
    {
        using var file = File.OpenRead(path);
        using var brotli = new BrotliStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(brotli, Encoding.UTF8);
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("規則目錄魔數不符。");
        ushort version = reader.ReadUInt16();
        if (version != Version) throw new InvalidDataException($"規則目錄版本 {version} 不受支援。");
        int count = reader.ReadInt32();
        var entries = new List<RulesCatalogEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var kind = (RulesCatalogKind)reader.ReadByte();
            if (!Enum.IsDefined(kind)) throw new InvalidDataException($"未知的目錄項目類型 {(byte)kind}。");
            string name = reader.ReadString();
            var sources = new string[reader.ReadInt32()];
            for (int s = 0; s < sources.Length; s++) sources[s] = reader.ReadString();
            byte[] rules = reader.ReadBytes(reader.ReadInt32());
            entries.Add(new RulesCatalogEntry(kind, name, sources, rules));
        }
        return entries;
    }
}
