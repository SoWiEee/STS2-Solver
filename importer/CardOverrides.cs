using System.Text.Json;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.LiveImport;

/// <summary>
/// Card stats of a newer game build, parsed from its decompilation by tools/card-stats/parse.mjs.
/// The frozen catalog holds 0.113.1 stats; for a card listed here, cost, type, rarity, target,
/// keywords and variables come from the parse while everything else stays the catalog's.
/// </summary>
public sealed class ParsedCard
{
    public int Cost { get; init; }
    public string Type { get; init; } = "";
    public string Rarity { get; init; } = "";
    public string Target { get; init; } = "";
    public string[] Keywords { get; init; } = [];
    public string[] Tags { get; init; } = [];
    public Dictionary<string, decimal> Vars { get; init; } = [];
    public ParsedUpgrade Upgrade { get; init; } = new();
    public int MaxUpgrade { get; init; } = 1;
}

public sealed class ParsedUpgrade
{
    public Dictionary<string, decimal> Vars { get; init; } = [];
    public int Cost { get; init; }
    public string[] Add { get; init; } = [];
    public string[] Remove { get; init; } = [];
}

public static class CardOverrides
{
    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static Dictionary<string, ParsedCard> Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, ParsedCard>>(File.ReadAllText(path), Options) ?? [] : [];

    /// <summary>The catalog's stats at <paramref name="level"/> with the parsed card's numbers applied.</summary>
    /// <summary>Kernel vars of 0.113.1 effects that 0.114 removed; they read as 0 (Wah Ki lost its energy/draw, Tune now upgrades cards instead of granting energy).</summary>
    public static bool Removed(string nativeId, string varName) =>
        (nativeId, varName) is ("WAH_KI", "Energy" or "Cards") or ("TUNE", "Energy");

    public static CardStats? Apply(CardStats catalog, ParsedCard card, int level, IReadOnlyList<string> varNames, string nativeId = "")
    {
        bool up = level > 0;
        var vars = new decimal[varNames.Count];
        for (int i = 0; i < varNames.Count; i++)
        {
            // A var computed at run time (not a literal in CanonicalVars) keeps the catalog's value.
            vars[i] = card.Vars.TryGetValue(varNames[i], out decimal value)
                ? value + (up ? card.Upgrade.Vars.GetValueOrDefault(varNames[i]) : 0)
                : Removed(nativeId, varNames[i]) ? 0 : i < catalog.Vars.Length ? catalog.Vars[i] : 0;
        }
        var keywords = card.Keywords.Concat(up ? card.Upgrade.Add : []).Except(up ? card.Upgrade.Remove : []);
        return new CardStats
        {
            Type = Enum.Parse<CardType>(card.Type),
            Target = Enum.Parse<TargetType>(card.Target),
            Rarity = Enum.Parse<CardRarity>(card.Rarity),
            Pool = catalog.Pool,
            EnergyCost = catalog.CostsX ? catalog.EnergyCost : card.Cost + (up ? card.Upgrade.Cost : 0), // X costs are -1 in the source, 0 in stats
            CostsX = catalog.CostsX,
            StarCost = catalog.StarCost,
            StarCostsX = catalog.StarCostsX,
            Keywords = keywords.Aggregate(catalog.Keywords & Keywords.CreatedAfterRoot,
                (k, name) => Enum.TryParse(name, out Keywords flag) ? k | flag : k), // keywords the kernel does not model are dropped, as upstream's MapKeywords would reject them
            MaxUpgradeLevel = card.MaxUpgrade,
            Tags = catalog.Tags,
            IsSoul = catalog.IsSoul,
            GainsBlock = catalog.GainsBlock,
            IsColorless = catalog.IsColorless,
            HasDynamicType = catalog.HasDynamicType,
            PlaysRecordRisk = catalog.PlaysRecordRisk,
            Vars = vars,
        };
    }
}
