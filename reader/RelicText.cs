using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

static class RelicText
{
    // REA inventory (research-data/rea-research/sts2-pck-evidence.json) for localization/zht/*.json
    // in the supported v0.113.1 PCK: offset, length and MD5 of each file.
    static readonly Dictionary<string, (long Offset, int Length, string Md5)> Tables = new()
    {
        ["relics"] = (2130609984, 93298, "FE75EF550A7D430469F69F135653D76A"),
        ["cards"] = (2130102128, 124182, "A0FD26C6E233BEE1FDD816EEC8C08823"),
        ["potions"] = (2130503616, 11323, "EE59B4BCFD174535C323D5013A90BD3F"),
        ["monsters"] = (2130476304, 25370, "3D0117DDA1B0B5AF8A73F4EBE16C092B"),
        ["powers"] = (2130515408, 94567, "5461AA990E11D1143C8BD7A1B42FCD90"),
    };

    public static Dictionary<string, string> Load(string gameDirectory) => Load(gameDirectory, "relics");

    /// <summary>One zht localization table; empty when the PCK is not the supported build.</summary>
    public static Dictionary<string, string> Load(string gameDirectory, string table)
    {
        var (Offset, Length, Md5) = Tables[table];
        try
        {
            using var pack = File.OpenRead(Path.Combine(gameDirectory, "SlayTheSpire2.pck"));
            if (pack.Length < Offset + Length) return new();
            pack.Position = Offset;
            byte[] bytes = new byte[Length];
            pack.ReadExactly(bytes);
            return Convert.ToHexString(MD5.HashData(bytes)) == Md5
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? new()
                : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
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
