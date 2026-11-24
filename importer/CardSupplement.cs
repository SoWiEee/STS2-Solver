using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.LiveImport;

/// <summary>
/// Cards no upstream frozen RULE table covers (mostly the beta Fool's), transcribed from the
/// 0.113.1 decompiled models: constructor (cost, type, rarity, target), CanonicalVars in
/// <see cref="CardDef.VarNames"/> order, CanonicalKeywords/Tags, and OnUpgrade deltas.
/// </summary>
static class CardSupplement
{
    // (cost, type, rarity, target, pool, keywords, tags, gainsBlock, maxUpgrade, base vars, upgrade deltas, upgraded cost)
    record Entry(int Cost, CardType Type, CardRarity Rarity, TargetType Target, TransformPool Pool,
        Keywords Keywords, CardTag Tags, bool GainsBlock, int MaxUpgrade, decimal[] Vars, decimal[] Delta, int? UpgradedCost = null);

    static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal)
    {
        ["AMNESIA"] = new(-1, CardType.Status, CardRarity.Status, TargetType.None, TransformPool.Colorless, Keywords.None, CardTag.None, false, 0, [1], []),
        ["CALAMITY"] = new(3, CardType.Power, CardRarity.Rare, TargetType.Self, TransformPool.Colorless, Keywords.None, CardTag.None, false, 1, [], [], 2),
        ["ARROW_STAB"] = new(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy, TransformPool.Fool, Keywords.None, CardTag.None, false, 1, [9], [3]),
        ["BLOSSOM_SHOT"] = new(2, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy, TransformPool.Fool, Keywords.None, CardTag.Shoot, false, 1, [11], [3]),
        ["CHO_LIMBS"] = new(2, CardType.Skill, CardRarity.Uncommon, TargetType.Self, TransformPool.Fool, Keywords.None, CardTag.Cho, true, 1, [5], [2]),
        ["DIVINE_SHOT"] = new(0, CardType.Attack, CardRarity.Ancient, TargetType.AnyEnemy, TransformPool.Fool, Keywords.Retain, CardTag.Shoot, false, 1, [40], [10]),
        ["ENNUI"] = new(1, CardType.Power, CardRarity.Rare, TargetType.Self, TransformPool.Fool, Keywords.None, CardTag.None, false, 1, [2], [1]),
        ["GROWTH"] = new(1, CardType.Skill, CardRarity.Uncommon, TargetType.Self, TransformPool.Fool, Keywords.None, CardTag.None, false, 1, [4], [2]),
        ["KI_VERSE"] = new(1, CardType.Skill, CardRarity.Rare, TargetType.Self, TransformPool.Fool, Keywords.None, CardTag.Ki, false, 1, [1], [1]),
        ["LOST_LUTE"] = new(3, CardType.Power, CardRarity.Ancient, TargetType.Self, TransformPool.Fool, Keywords.None, CardTag.None, false, 1, [1], [0], 2),
        ["SORROWFUL_STRIKE"] = new(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy, TransformPool.Fool, Keywords.None, CardTag.Strike, false, 1, [13, 2], [4, 0]),
        ["WAH_TOUCH"] = new(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy, TransformPool.Fool, Keywords.None, CardTag.Wah, false, 1, [6, 1], [2, 1]),
    };

    public static CardStats? Stats(string nativeId, int level)
    {
        if (!Entries.TryGetValue(nativeId, out Entry? e) || level > e.MaxUpgrade) return null;
        Registry.TryCard(nativeId, out ushort id);
        CardDef def = Registry.Card(id);
        if (def.VarNames.Count != e.Vars.Length)
            throw new LiveUnsupportedException($"{nativeId} 的補充變數數量與定義不符");
        return new CardStats
        {
            Type = e.Type, Target = e.Target, Rarity = e.Rarity, Pool = e.Pool,
            EnergyCost = level > 0 && e.UpgradedCost is int c ? c : e.Cost,
            CostsX = false, StarCost = 0, StarCostsX = false,
            Keywords = e.Keywords, MaxUpgradeLevel = e.MaxUpgrade, Tags = e.Tags,
            GainsBlock = e.GainsBlock, IsColorless = e.Pool == TransformPool.Colorless,
            HasDynamicType = def.HasDynamicCardType,
            PlaysRecordRisk = MirrorRiskTables.CardPlaysRecordRisk(nativeId),
            Vars = e.Vars.Select((v, i) => level > 0 && i < e.Delta.Length ? v + e.Delta[i] : v).ToArray(),
        };
    }
}
