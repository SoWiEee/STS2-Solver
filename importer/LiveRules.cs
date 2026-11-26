// Builds the per-root Rules from the frozen catalog plus live root facts. Root-level semantics follow
// rank-Yu/sts2-solver src/Import/Combat/RulesCompiler.cs (MIT, commit f0167be); the parts that need
// game code execution (canonical card/relic/potion values, monster state machines) come from the
// catalog instead of being compiled in-process.
using System.Reflection;
using Microsoft.Diagnostics.Runtime;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using static Sts2Solver.LiveImport.Clr;

namespace Sts2Solver.LiveImport;

public sealed class RulesCatalog
{
    /// <summary>Build of the running game (set by the reader); newer builds patch card stats from card-overrides-{version}.json.</summary>
    public static string GameVersion { get; set; } = "0.113.1";
    readonly string _dataDirectory;
    readonly Dictionary<string, Dictionary<string, ParsedCard>> _overrides = new(StringComparer.Ordinal);

    /// <summary>The frozen 0.113.1 stats of a card adjusted to <see cref="GameVersion"/>.</summary>
    public CardStats? Adjust(string nativeId, int level, CardStats? stats)
    {
        if (stats == null || !Registry.TryCard(nativeId, out ushort id)) return stats;
        if (!_overrides.TryGetValue(GameVersion, out var cards))
            _overrides[GameVersion] = cards = CardOverrides.Load(Path.Combine(_dataDirectory, $"card-overrides-{GameVersion}.json"));
        return cards.TryGetValue(nativeId, out var card)
            ? CardOverrides.Apply(stats, card, level, Registry.Card(id).VarNames) ?? stats
            : stats;
    }

    readonly Rules _content;
    readonly Dictionary<string, List<MonsterRules>> _monsters = new(StringComparer.Ordinal);

    public RulesCatalog(string path)
    {
        _dataDirectory = Path.GetDirectoryName(path)!;
        Rules? content = null;
        foreach (RulesCatalogEntry entry in RulesCatalogFile.Read(path))
        {
            Rules rules = RulesCodec.Decode(entry.Rules);
            if (entry.Kind == RulesCatalogKind.Content) content = rules;
            else (_monsters.TryGetValue(entry.Name, out var list) ? list : _monsters[entry.Name] = []).Add(rules.MonsterRule(0));
        }
        _content = content ?? throw new InvalidDataException("規則目錄缺少內容表。");
        string statics = Path.Combine(Path.GetDirectoryName(path)!, "monster-statics.json");
        if (File.Exists(statics))
            _statics = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, StaticGetter>>>(
                File.ReadAllText(statics), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    // Integer getters of uncatalogued monsters, extracted from sts2.dll CIL by REA (tools/monster-statics).
    public sealed record StaticGetter(int? Value, int? Ascension, int? High, int? Low, int? Dread);

    // Set per request while Rules are built (each HTTP request compiles on its own thread).
    [ThreadStatic] internal static bool DreadMode, BlitzMode;

    // AscensionManager: Blitz level i grants AscensionHelper.GetLevelForBlitz(i), not level i.
    static readonly int[] BlitzLevels = [2, 4, 8, 9, 5]; // WearyTraveler, TightBelt, ToughEnemies, DeadlyEnemies, AscendersBane
    static bool HasAscension(int ascension, int level) =>
        BlitzMode ? BlitzLevels.Take(Math.Min(ascension, BlitzLevels.Length)).Contains(level) : ascension >= level;
    readonly Dictionary<string, Dictionary<string, StaticGetter>> _statics = [];

    /// <summary>An AI register from memory, else the REA table. Follows NativeAccess.MonsterInt: the
    /// Waterfall Giant keeps raw damage fields, and its base gun damage is a computed (table) getter.</summary>
    internal bool TryRegister(ClrObject monster, string monsterId, string name, int ascension, out int value)
    {
        value = 0;
        if (TypeName(monster) == "WaterfallGiant" && name is "PressureGunBonus" or "SteamEruptionDamage")
        {
            if (name == "SteamEruptionDamage") return LiveRules.TryMonsterInt(monster, "_steamEruptionDamage", out value);
            if (!LiveRules.TryMonsterInt(monster, "_currentPressureGunDamage", out int current)
                || !TryStatic(monsterId, "BasePressureGunDamage", ascension, out int baseDamage)) return false;
            value = current - baseDamage;
            return true;
        }
        return LiveRules.TryMonsterInt(monster, name, out value) || TryStatic(monsterId, name, ascension, out value);
    }

    internal bool TryStatic(string monster, string name, int ascension, out int value)
    {
        value = 0;
        if (!_statics.TryGetValue(monster, out var getters) || !getters.TryGetValue(name, out var g)) return false;
        if (g.Value is int constant) { value = constant + (DreadMode ? g.Dread ?? 0 : 0); return true; }
        if (g is { Ascension: int level, High: int high, Low: int low })
        {
            value = (HasAscension(ascension, level) ? high : low) + (DreadMode ? g.Dread ?? 0 : 0);
            return true;
        }
        return false;
    }

    internal Rules Content => _content;

    /// <summary>Canonical stats of a card at an upgrade level, or null when the catalog lacks it.</summary>
    public CardStats? CardStats(string nativeId, int upgrade)
    {
        if (!Registry.TryCard(nativeId, out ushort id)) return null;
        try { return Adjust(nativeId, upgrade, _content.Card(id, upgrade)); } catch (UnsupportedException) { return Adjust(nativeId, upgrade, CardSupplement.Stats(nativeId, upgrade)); }
    }
    public CardStats? RewardCardStats(string nativeId, int upgrade)
    {
        if (!Registry.TryCard(nativeId, out ushort id)) return null;
        try { return Adjust(nativeId, upgrade, _content.Card(id, upgrade)); }
        catch (UnsupportedException) { return null; }
    }
    internal IReadOnlyList<MonsterRules> Variants(string monster) =>
        _monsters.TryGetValue(monster, out var list) ? list : [];
}

internal static class LiveRules
{
    static T Field<T>(Rules rules, string name) =>
        (T)typeof(Rules).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rules)!;

    public static Rules Build(LiveState live, RulesCatalog catalog, List<string> notes)
    {
        Rules content = catalog.Content;
        var draft = new RulesDraft
        {
            BaseMaxEnergy = Get<int>(live.Player, "MaxEnergy"),
            BaseOrbSlotCount = Get<int>(live.Player, "BaseOrbSlotCount"),
            HasIdentityContent = false, // RulesDraft defaults to true; root and closure facts set it
        };

        // ---- Content tables from the catalog ----
        // Cards enter only through the root closure below: Rules derives flags (Fool mechanics,
        // turn-end-in-hand effects) from which cards are present, as upstream's per-root table does.
        var cards = Field<CardStats?[][]>(content, "_cards");
        var powerVars = Field<decimal[][]>(content, "_powerVars");
        for (ushort id = 0; id < powerVars.Length; id++)
        {
            if (powerVars[id].Length > 0) draft.SetPowerVars(id, powerVars[id]);
            if (MirrorRiskTables.PowerIds.Contains(Registry.Power(id).NativeId))
                draft.SetPowerDeathHistoryRisk(id, MirrorRiskTables.PowerRecordsDeathHistoryRisk(Registry.Power(id).NativeId));
        }
        var potions = Field<PotionStats?[]>(content, "_potions");
        for (ushort id = 0; id < potions.Length; id++)
            if (potions[id] is { } stats) draft.SetPotion(id, stats);
        var relicVars = Field<decimal[][]>(content, "_relicVars");
        for (ushort id = 0; id < relicVars.Length; id++)
            if (relicVars[id].Length > 0) draft.SetRelicVars(id, relicVars[id]);
        string character = CharacterPool(live.Character);
        var catalogPools = new Dictionary<string, ushort[]>(StringComparer.Ordinal);
        foreach (var (key, pool) in Field<Dictionary<string, CardPool>>(content, "_cardPools"))
        {
            int at = key.IndexOf('@');
            if (at < 0) catalogPools[key] = pool.Cards;
            else if (key[(at + 1)..] == character) catalogPools[key[..at]] = pool.Cards;
        }
        List<ushort> spawnClosure = SpawnClosure(live);
        Require(live.Enemies.Count + spawnClosure.Count <= Limits.MonsterRules, $"怪物規則 {live.Enemies.Count}+{spawnClosure.Count}");
        AddCardClosure(live, draft, cards, catalogPools, content, spawnClosure, catalog);
        if (RulesCatalog.GameVersion != "0.113.1" && live.Character == "FOOL")
            notes.Add($"v{RulesCatalog.GameVersion} 調整了愚者：卡牌數值已更新，但部分機制（旋律、箭矢、伸展等）仍依 0.113.1 模擬");

        // ---- Root facts (RulesCompiler.Compile) ----
        bool identity = live.Character is "REGENT" or "NECROBINDER" or "SILENT";
        foreach (ClrObject pile in live.Piles())
            foreach (ClrObject card in LiveState.Cards(pile))
            {
                Require(Registry.TryCard(Id(card), out ushort cardId), $"未支援的卡牌 {Id(card)}");
                CardDef def = Registry.Card(cardId);
                identity |= def.UsesIdentityLedger || def.TracksFetchPlay || def.MayGenerateIdentityContent || def.IsIdentityContent;
                draft.HasDecimalCardVars |= def.UsesDecimalDamageVar;
                int upgrade = Get<int>(card, "_currentUpgradeLevel");
                Require(draft.HasCard(cardId, upgrade), $"規則目錄沒有 {Id(card)}+{upgrade}");
            }
        var allCreatures = new List<ClrObject> { live.Creature };
        allCreatures.AddRange(live.Pets);
        allCreatures.AddRange(live.Enemies);
        int swordSage = 0;
        foreach (ClrObject creature in allCreatures)
            foreach (ClrObject power in LiveState.Powers(creature))
            {
                if (Get<int>(power, "_amount") == 0) continue;
                string powerId = Id(power);
                Require(Registry.TryPower(powerId, out ushort pid), $"未支援的能力 {powerId}");
                identity |= Registry.Power(pid).IsIdentityContent;
                if (powerId is "THIEVERY_POWER" or "HEIST_POWER" or "SWIPE_POWER" or "SURPRISE_POWER") draft.HasTheftContent = true;
                Require(powerId is not ("NIGHTMARE_POWER"), $"能力 {powerId} 暫不支援");
                if (powerId == "SWORD_SAGE_POWER" && creature.Address == live.Creature.Address) swordSage = Get<int>(power, "_amount");
            }
        draft.RootSwordSageAmount = swordSage;

        ushort lampId = ushort.MaxValue;
        var rosterRows = new List<Relic>();
        foreach (ClrObject relic in live.Relics())
        {
            string relicId = Id(relic);
            if (!Registry.TryRelic(relicId, out ushort rid))
            {
                notes.Add($"近似預測略過遺物 {relicId}");
                continue;
            }
            RelicDef def = Registry.Relic(rid);
            identity |= def.MayGenerateIdentityContent || def.IsIdentityContent;
            if (def.IsUnsettlingLamp && lampId == ushort.MaxValue) lampId = rid;
            bool melted = Get<bool>(relic, "_isMelted");
            if (!melted)
            {
                draft.HasHistoryCourse |= relicId == "HISTORY_COURSE";
                draft.HasPaelsLegion |= def.IsPaelsLegion;
                if (relicId == "FUR_COAT") draft.FurCoatSpawnHpOne |= FurCoatMarksCurrentRoom(relic, live);
            }
            rosterRows.Add(new Relic { Id = rid, Flags = melted ? RelicFlags.Melted : RelicFlags.None });
        }
        draft.UnsettlingLampRelicId = lampId;
        draft.HasIdentityContent |= identity;

        ClrObject encounter = Obj(live.Combat, "_encounter");
        draft.HasEncounter = !encounter.IsNull;
        // Encounter.Slots is a computed override (empty spawn slots such as wriggler1-4 included); the
        // registry's encounter definition carries the same list. Without one, use the filled slots.
        draft.EncounterSlots = encounter.IsNull ? []
            : Registry.TryEncounter(Id(encounter), out ushort encounterDef) && Registry.Encounter(encounterDef).Slots.Count > 0
                ? [.. Registry.Encounter(encounterDef).Slots]
                : EncounterSlots(encounter, live);
        string? encounterId = encounter.IsNull ? null : Id(encounter);
        draft.HasTheftContent = encounterId is "GREMLIN_MERC_NORMAL" or "THIEVING_HOPPER_WEAK";
        draft.Dominion = CompileDominion(Obj(live.Combat, "Dominion"), encounterId, draft.EncounterSlots.Length);
        draft.TotalFloor = List(Obj(live.Run, "_mapPointHistory")).Sum(act => Get<int>(act, "_size"));
        // RulesCompiler: native GameMode Standard=1, Daily=2, Dread=3, Blitz=4.
        draft.Mode = Get<int>(live.Run, "GameMode") switch
        {
            1 => CombatMode.Standard,
            3 => CombatMode.Dread,
            4 => CombatMode.Blitz,
            int other => throw new LiveUnsupportedException($"未支援的遊戲模式 {other}（每日挑戰等）"),
        };
        RulesCatalog.DreadMode = draft.Mode == CombatMode.Dread;
        RulesCatalog.BlitzMode = draft.Mode == CombatMode.Blitz;
        draft.RootEnemyCount = live.Enemies.Count;
        Require(live.Enemies.Count <= Limits.Enemies, $"敵人數 {live.Enemies.Count}");

        // ---- Monsters: catalog variant + live static values ----
        bool infiniteHp = false;
        for (int index = 0; index < live.Enemies.Count; index++)
        {
            ClrObject creature = live.Enemies[index];
            ClrObject monster = Obj(creature, "Monster");
            string monsterId = Id(monster);
            Require(Registry.TryMonster(monsterId, out ushort mid), $"未支援的敵人 {monsterId}");
            MonsterDef def = Registry.Monster(mid);
            draft.HasTheftContent |= monsterId is "GREMLIN_MERC" or "THIEVING_HOPPER" or "FAT_GREMLIN";
            infiniteHp |= def.HasInfiniteHpDisplay;
            draft.SetMonster(index, PickVariant(catalog, creature, monster, monsterId, def, notes, Get<int>(live.Run, "AscensionLevel")));
        }
        // RulesCompiler.CompileSpawnedMonster: one entry per type reachable by summoning. Upstream
        // compiles a fresh model in-process; here the catalog's spawn-compiled variant (no root
        // powers) stands in, so its numbers are those of the fixture it came from.
        foreach (ushort spawned in spawnClosure)
        {
            MonsterDef def = Registry.Monster(spawned);
            infiniteHp |= def.HasInfiniteHpDisplay;
            var variants = catalog.Variants(def.NativeId);
            MonsterRules? fresh = variants.FirstOrDefault(v => v.RootPowerAmounts.All(a => a == 0)) ?? variants.FirstOrDefault();
            Require(fresh != null, $"規則目錄沒有召喚物 {def.NativeId}");
            draft.AddSpawnableMonster(fresh!);
            notes.Add($"召喚物 {def.NativeId} 的數值取自規則目錄（未依本局進階等級重算）");
        }
        draft.HasInfiniteHpDisplayMonster = infiniteHp;
        return draft.Build().WithRelics(RelicRoster.Create(rosterRows.ToArray()));
    }

    /// <summary>
    /// RulesCompiler.AddGeneratedCardClosure + AddStatsWithUpgrade over the catalog: pile cards at
    /// 0/current/next upgrade, then everything the root's cards, powers, relics, potions and enemies
    /// declare they can generate, transitively.
    /// </summary>
    /// <summary>FurCoat.GetMarkedCoords() contains the current map coordinate (last visited coord).</summary>
    static bool FurCoatMarksCurrentRoom(ClrObject relic, LiveState live)
    {
        if (!Get<bool>(relic, "FurCoatCoordsSet")) return false;
        var visited = StructList(Obj(live.Run, "_visitedMapCoords"));
        if (visited.Count == 0) return false;
        int row = visited[^1].ReadField<int>("row"), col = visited[^1].ReadField<int>("col");
        ClrArray cols = Obj(relic, "FurCoatCoordCols").AsArray(), rows = Obj(relic, "FurCoatCoordRows").AsArray();
        for (int i = 0; i < cols.Length && i < rows.Length; i++)
            if (cols.GetValue<int>(i) == col && rows.GetValue<int>(i) == row) return true;
        return false;
    }

    /// <summary>RulesCompiler.SpawnClosure: summonable types, transitively, in first-seen BFS order.</summary>
    static List<ushort> SpawnClosure(LiveState live)
    {
        var closure = new List<ushort>();
        var seen = new HashSet<ushort>();
        var pending = new Queue<string>();
        foreach (ClrObject enemy in live.Enemies)
            if (Registry.TryMonster(Id(Obj(enemy, "Monster")), out ushort id))
                foreach (string spawned in Registry.Monster(id).SpawnedMonsterIds) pending.Enqueue(spawned);
        while (pending.Count > 0)
        {
            string nativeId = pending.Dequeue();
            Require(Registry.TryMonster(nativeId, out ushort id), $"召喚物 {nativeId} 沒有定義");
            if (!seen.Add(id)) continue;
            closure.Add(id);
            foreach (string next in Registry.Monster(id).SpawnedMonsterIds) pending.Enqueue(next);
        }
        return closure;
    }

    static void AddCardClosure(LiveState live, RulesDraft draft, CardStats?[][] catalogCards,
        Dictionary<string, ushort[]> catalogPools, Rules content, List<ushort> spawnClosure, RulesCatalog catalog)
    {
        var pendingCards = new Queue<(string Card, int Upgrade)>();
        var pendingPools = new Queue<string>();
        var pendingPowers = new Queue<string>();
        var seenCards = new HashSet<(string, int)>();
        var seenPools = new HashSet<string>(StringComparer.Ordinal);
        var seenPowers = new HashSet<string>(StringComparer.Ordinal);
        var seenPotions = new HashSet<ushort>();
        bool needsPotionPool = false, needsOutOfCombatPotionPool = false, potionPoolSet = false, outPoolSet = false;
        bool identity = false;

        void AddStats(ushort id, int upgrade)
        {
            string name = Registry.Card(id).NativeId;
            CardStats? Stat(int level) => catalog.Adjust(name, level, (level < catalogCards[id].Length ? catalogCards[id][level] : null)
                ?? CardSupplement.Stats(name, level));
            if (name == "DINGY_DAGGER")
            {
                for (int level = 0; level < catalogCards[id].Length; level++)
                    if (Stat(level) is { } s && !draft.HasCard(id, level)) draft.SetCard(id, level, s);
                return;
            }
            foreach (int level in new[] { 0, upgrade })
            {
                if (draft.HasCard(id, level)) continue;
                draft.SetCard(id, level, Stat(level) ?? throw new LiveUnsupportedException($"規則目錄沒有 {name}+{level}"));
            }
            CardStats current = Stat(upgrade)!;
            if (upgrade < current.MaxUpgradeLevel && !draft.HasCard(id, upgrade + 1))
                draft.SetCard(id, upgrade + 1, Stat(upgrade + 1) ?? throw new LiveUnsupportedException($"規則目錄沒有 {name}+{upgrade + 1}"));
        }
        // RulesCompiler.FreezeCardPool for pools no frozen table recorded, built from card stats.
        // shortcut: treats every card as unlocked and skips FilterForCombat's per-card flags; read UnlockState if a run lacks cards.
        TransformPool own = Enum.Parse<TransformPool>(CharacterPool(live.Character));
        ushort[]? TaggedPool(string key)
        {
            const CardTag melody = CardTag.Wah | CardTag.Cho | CardTag.Ki;
            static bool Character(CardStats c) => c.Pool is not (TransformPool.Colorless or TransformPool.Status or TransformPool.Curse);
            static bool Generable(CardStats c) => c.Rarity is not (CardRarity.Basic or CardRarity.Token or CardRarity.Event or CardRarity.Ancient);
            Func<CardStats, bool>? keep = key switch
            {
                CardPools.IroncladAll => c => c.Pool == TransformPool.Ironclad && Generable(c),
                CardPools.SilentAll => c => c.Pool == TransformPool.Silent && Generable(c),
                CardPools.DefectAll => c => c.Pool == TransformPool.Defect && Generable(c),
                CardPools.NecrobinderAll => c => c.Pool == TransformPool.Necrobinder && Generable(c),
                CardPools.RegentAll => c => c.Pool == TransformPool.Regent && Generable(c),
                CardPools.FoolAll => c => c.Pool == TransformPool.Fool && Generable(c),
                CardPools.CharacterAll => c => c.Pool == own && Generable(c),
                CardPools.CharacterAttacks => c => c.Pool == own && Generable(c) && c.Type == CardType.Attack,
                CardPools.CharacterPowers => c => c.Pool == own && Generable(c) && c.Type == CardType.Power,
                CardPools.CharacterEthereal => c => c.Pool == own && Generable(c) && (c.Keywords & Keywords.Ethereal) != 0,
                CardPools.CharacterZeroCost => c => c.Pool == own && Generable(c) && c.EnergyCost == 0 && !c.CostsX,
                CardPools.CharacterShots => c => c.Pool == own && Generable(c) && (c.Tags & CardTag.Shoot) != 0,
                CardPools.OtherCharacterCards => c => Character(c) && c.Pool != own && Generable(c),
                CardPools.OtherCharacterAttacks => c => Character(c) && c.Pool != own && Generable(c) && c.Type == CardType.Attack,
                CardPools.Melodies => c => Character(c) && (c.Tags & melody) != 0,
                CardPools.Wah => c => Character(c) && (c.Tags & CardTag.Wah) != 0,
                CardPools.Cho => c => Character(c) && (c.Tags & CardTag.Cho) != 0,
                CardPools.Ki => c => Character(c) && (c.Tags & CardTag.Ki) != 0,
                CardPools.ColorlessAll => c => c.Pool == TransformPool.Colorless && c.IsColorless && Generable(c),
                CardPools.ColorlessExceptJackOfAllTrades => c => c.Pool == TransformPool.Colorless && c.IsColorless && Generable(c),
                CardPools.StatusAll => c => c.Type == CardType.Status,
                CardPools.CurseAll => c => c.Type == CardType.Curse && c.Rarity == CardRarity.Curse,
                _ => null,
            };
            if (keep == null) return null;
            var ids = new List<ushort>();
            for (int id = 0; id < Registry.CardCount; id++)
            {
                string name = Registry.Card((ushort)id).NativeId;
                CardStats? stats = (catalogCards[id].Length > 0 ? catalogCards[id][0] : null) ?? CardSupplement.Stats(name, 0);
                if (stats != null && keep(stats) && !(key == CardPools.ColorlessExceptJackOfAllTrades && name == "JACK_OF_ALL_TRADES")) ids.Add((ushort)id);
            }
            return [.. ids];
        }
        void Declare(ContentDef def)
        {
            identity |= def is CardDef { IsIdentityContent: true } or CardDef { UsesIdentityLedger: true } or PowerDef { IsIdentityContent: true };
            needsPotionPool |= def.GeneratesRandomPotion;
            needsOutOfCombatPotionPool |= def.GeneratesOutOfCombatPotion;
            if (def is PotionDef && !seenPotions.Add(def.Id)) return;
            foreach ((string card, int upgrade) in def.GeneratedCards)
                if (seenCards.Add((card, upgrade))) pendingCards.Enqueue((card, upgrade));
            foreach (string pool in def.GeneratedCardPools)
                if (seenPools.Add(pool)) pendingPools.Enqueue(pool);
            foreach (string power in def.AppliedPowers) pendingPowers.Enqueue(power);
        }

        foreach (ClrObject pile in live.Piles())
            foreach (ClrObject card in LiveState.Cards(pile))
            {
                Require(Registry.TryCard(Id(card), out ushort id), $"未支援的卡牌 {Id(card)}");
                AddStats(id, Get<int>(card, "_currentUpgradeLevel"));
                Declare(Registry.Card(id));
                ClrObject enchantment = Obj(card, "Enchantment");
                if (!enchantment.IsNull && Registry.TryEnchantment(Id(enchantment), out ushort eid))
                    Declare(Registry.Enchantment(eid));
                ClrObject affliction = Obj(card, "Affliction");
                if (!affliction.IsNull && Registry.TryAffliction(Id(affliction), out ushort aid))
                    Declare(Registry.Affliction(aid));
            }
        foreach (ClrObject creature in new[] { live.Creature }.Concat(live.Pets).Concat(live.Enemies))
            foreach (ClrObject power in LiveState.Powers(creature))
                if (Get<int>(power, "_amount") != 0 && Registry.TryPower(Id(power), out ushort id))
                    Declare(Registry.Power(id));
        foreach (ClrObject relic in live.Relics())
            if (Registry.TryRelic(Id(relic), out ushort id)) Declare(Registry.Relic(id));
        var potionSlots = List(Obj(live.Player, "_potionSlots"), Limits.PotionSlots, allowNull: true);
        for (int slot = 0; slot < potionSlots.Count; slot++)
            if (!potionSlots[slot].IsNull)
            {
                Require(Registry.TryPotion(Id(potionSlots[slot]), out ushort id), $"未支援的藥水 {Id(potionSlots[slot])}");
                Declare(Registry.Potion(id));
                draft.SetRootPotion(slot, id);
            }
        foreach (ClrObject enemy in live.Enemies)
            if (Registry.TryMonster(Id(Obj(enemy, "Monster")), out ushort id)) Declare(Registry.Monster(id));
        foreach (ushort spawned in spawnClosure) Declare(Registry.Monster(spawned));

        while (true)
        {
            while (pendingCards.Count > 0 || pendingPools.Count > 0 || pendingPowers.Count > 0)
            {
                while (pendingPowers.Count > 0)
                {
                    string power = pendingPowers.Dequeue();
                    if (!seenPowers.Add(power)) continue;
                    Require(Registry.TryPower(power, out ushort id), $"生成的能力 {power} 沒有定義");
                    Declare(Registry.Power(id));
                }
                while (pendingPools.Count > 0)
                {
                    string key = pendingPools.Dequeue();
                    ushort[]? pool = catalogPools.TryGetValue(key, out var frozen) ? frozen : TaggedPool(key);
                    Require(pool != null, $"規則目錄沒有卡池 {key}");
                    draft.SetCardPool(key, pool!);
                    foreach (ushort id in pool!) { Declare(Registry.Card(id)); AddStats(id, 0); }
                }
                if (pendingCards.Count == 0) break;
                (string cardId, int upgrade) = pendingCards.Dequeue();
                Require(Registry.TryCard(cardId, out ushort cid), $"生成的卡牌 {cardId} 沒有定義");
                AddStats(cid, upgrade);
                Declare(Registry.Card(cid));
            }
            if (needsPotionPool && !potionPoolSet)
            {
                potionPoolSet = true;
                draft.SetPotionPool([.. content.PotionPool]);
                foreach (ushort id in content.PotionPool) Declare(Registry.Potion(id));
                continue;
            }
            if (needsOutOfCombatPotionPool && !outPoolSet)
            {
                outPoolSet = true;
                draft.SetOutOfCombatPotionPool([.. content.OutOfCombatPotionPool]);
                foreach (ushort id in content.OutOfCombatPotionPool) Declare(Registry.Potion(id));
                continue;
            }
            break;
        }
        draft.HasIdentityContent |= identity;
    }

    /// <summary>Port of upstream DominionCompiler.CompileDominion: Dread mode's per-boss rule frozen at the root.</summary>
    static DominionRules CompileDominion(ClrObject dominion, string? encounterId, int slotCount)
    {
        if (dominion.IsNull) return default;
        string type = dominion.Type?.Name?.Split('.').Last() ?? "";
        (DominionKind kind, string? power, int amount) = type switch
        {
            "AeonglassDominion" => (DominionKind.Aeonglass, null, 0),
            "CeremonialBeastDominion" => (DominionKind.CeremonialBeast, "RITUAL_POWER", 1),
            "InsatiableDominion" => (DominionKind.Insatiable, null, 0),
            "KaiserCrabDominion" => (DominionKind.KaiserCrab, "PLATING_POWER", 7),
            "KinDominion" => (DominionKind.Kin, null, 0),
            "KnowledgeDemonDominion" => (DominionKind.KnowledgeDemon, null, 0),
            "LagavulinMatriarchDominion" => (DominionKind.LagavulinMatriarch, null, 0),
            "QueenDominion" => (DominionKind.Queen, null, 0),
            "SoulFyshDominion" => (DominionKind.SoulFysh, null, 0),
            "TestSubjectDominion" => (DominionKind.TestSubject, "STRENGTH_POWER", 3),
            "VantomDominion" => (DominionKind.Vantom, "SLIPPERY_POWER", 1),
            "WaterfallGiantDominion" => (DominionKind.WaterfallGiant, "EXPLOSIVE_POWER",
                slotCount > 1 ? 3 : encounterId?.EndsWith("_WEAK", StringComparison.Ordinal) == true ? 4
                    : encounterId?.EndsWith("_ELITE", StringComparison.Ordinal) == true ? 7 : 5),
            _ => throw new LiveUnsupportedException($"領域 {type} 尚未建模"),
        };
        ushort id = 0;
        Require(power is null || Registry.TryPower(power, out id), $"未支援的能力 {power}");
        return new(kind, id, amount);
    }

    static string CharacterPool(string character) => character switch
    {
        "IRONCLAD" => nameof(TransformPool.Ironclad),
        "SILENT" => nameof(TransformPool.Silent),
        "DEFECT" => nameof(TransformPool.Defect),
        "NECROBINDER" => nameof(TransformPool.Necrobinder),
        "REGENT" => nameof(TransformPool.Regent),
        "FOOL" => nameof(TransformPool.Fool),
        _ => throw new LiveUnsupportedException($"未支援的角色 {character}"),
    };

    // Encounter.Slots is a computed property; the generated (monster, slot) list keeps the slots
    // the encounter filled. Encounters that summon into empty slots are rejected earlier.
    static string[] EncounterSlots(ClrObject encounter, LiveState live)
    {
        ClrObject withSlots = Obj(encounter, "_monstersWithSlots");
        if (withSlots.IsNull) return [];
        var slots = new List<string>();
        if (Has(withSlots, "_item")) // <>z__ReadOnlySingleElementList<T>: one-monster encounters
        {
            ClrObject only = Struct(withSlots, "_item").ReadObjectField("Item2");
            if (!only.IsNull && only.AsString() is { Length: > 0 } single) slots.Add(single);
            return [.. slots];
        }
        // A plain array, a List<T> (_items/_size), or the compiler's <>z__ReadOnlyArray<T> (_items only).
        ClrArray array = withSlots.Type!.IsArray ? withSlots.AsArray() : Obj(withSlots, "_items").AsArray();
        int count = Has(withSlots, "_size") ? Get<int>(withSlots, "_size") : array.Length;
        for (int i = 0; i < count; i++)
        {
            ClrValueType tuple = array.GetStructValue(i);
            ClrObject slotObject = tuple.ReadObjectField("Item2"); // generic tuple field is typed __Canon
            string? slot = slotObject.IsNull ? null : slotObject.AsString();
            if (!string.IsNullOrEmpty(slot)) slots.Add(slot);
        }
        return [.. slots];
    }

    static MonsterRules PickVariant(RulesCatalog catalog, ClrObject creature, ClrObject monster, string monsterId,
        MonsterDef def, List<string> notes, int ascension)
    {
        var variants = catalog.Variants(monsterId);
        if (variants.Count == 0) return LiveMonsterRules.Compile(creature, monster, monsterId, def, notes, catalog, ascension);
        int baseHp = Nullable<int>(creature, "MonsterMaxHpBeforeModification") ?? Get<int>(creature, "_maxHp");
        var matching = variants.Where(v => baseHp >= v.InitialHpMin && baseHp <= v.InitialHpMax).ToList();
        // Variants differ mainly by ascension numbers, which are re-read below; the HP match only
        // prefers the variant captured at a comparable ascension.
        MonsterRules chosen = matching.Count > 0 ? matching[0] : variants[0];

        // The catalog variant supplies the state-machine structure; ascension-dependent numbers are
        // re-read for this run: attacks from the live intents, static ints from memory or REA.
        var liveStates = HashEntries(Obj(Obj(monster, "_moveStateMachine"), "States"))
            .Select(e => e.ReadObjectField("value")).Where(s => Has(s, "StateId"))
            .ToDictionary(s => Str(s, "StateId")!, StringComparer.Ordinal);
        var nodes = new MonsterNode[chosen.Nodes.Length];
        for (int n = 0; n < nodes.Length; n++)
        {
            MonsterNode node = chosen.Nodes[n];
            nodes[n] = node;
            if (node.Kind != MonsterNodeKind.Move || node.Attacks.Length == 0 || !liveStates.TryGetValue(node.NativeId, out ClrObject move))
                continue;
            MonsterAttack[] live;
            try { live = LiveMonsterRules.LiveAttacks(move, $"{monsterId}.{node.NativeId}", monsterId, catalog, ascension); }
            catch (LiveUnsupportedException error)
            {
                notes.Add($"{error.Message}，沿用目錄值");
                continue;
            }
            Require(live.Length == node.Attacks.Length, $"{monsterId}.{node.NativeId} 的攻擊段數與目錄不符");
            // Upstream compiles Test Subject's MULTI_CLAW with its base count; the kernel adds the live extra.
            if (monsterId == "TEST_SUBJECT" && node.NativeId == "MULTI_CLAW_MOVE")
                live = live.Select((a, i) => a with { Repeats = node.Attacks[i].Repeats }).ToArray();
            nodes[n] = new MonsterNode
            {
                NativeId = node.NativeId, Kind = node.Kind, FollowUp = node.FollowUp,
                MustPerformOnceBeforeTransitioning = node.MustPerformOnceBeforeTransitioning,
                Attacks = live, Options = node.Options, ConditionalTarget = node.ConditionalTarget,
            };
        }

        int[] staticInts = (int[])chosen.StaticInts.Clone();
        for (int i = 0; i < def.StaticIntNames.Count; i++)
            if (!TryMonsterInt(monster, def.StaticIntNames[i], out staticInts[i])
                && !catalog.TryStatic(monsterId, def.StaticIntNames[i], ascension, out staticInts[i]))
            {
                staticInts[i] = chosen.StaticInts[i];
                notes.Add($"{monsterId}.{def.StaticIntNames[i]} 無法從記憶體或 REA 表取得，沿用目錄值 {staticInts[i]}");
            }
        int[] registers = new int[def.AiRegisterNames.Count];
        for (int i = 0; i < registers.Length; i++)
            Require(catalog.TryRegister(monster, monsterId, def.AiRegisterNames[i], ascension, out registers[i]),
                $"{monsterId} 的 AI 狀態 {def.AiRegisterNames[i]} 無法讀取");
        int[] rootPowers = new int[def.RootPowerAmountNames.Count];
        var powers = LiveState.Powers(creature);
        for (int i = 0; i < rootPowers.Length; i++)
            foreach (ClrObject power in powers)
                if (Get<int>(power, "_amount") != 0 && Id(power) == def.RootPowerAmountNames[i])
                { rootPowers[i] = Get<int>(power, "_amount"); break; }
        return new MonsterRules
        {
            MonsterId = chosen.MonsterId,
            Nodes = nodes,
            InitialNode = chosen.InitialNode,
            PendingInitialMoveNode = chosen.PendingInitialMoveNode,
            InitialMoveLogged = chosen.InitialMoveLogged,
            StunnedNode = chosen.StunnedNode,
            ReviveNode = chosen.ReviveNode,
            InitialHpMin = catalog.TryStatic(monsterId, "MinInitialHp", ascension, out int hpMin) ? hpMin : chosen.InitialHpMin,
            InitialHpMax = catalog.TryStatic(monsterId, "MaxInitialHp", ascension, out int hpMax) ? hpMax
                : Math.Max(hpMin == 0 ? chosen.InitialHpMin : hpMin, chosen.InitialHpMax),
            StaticInts = staticInts,
            RootPowerAmounts = rootPowers,
            InitialAiRegisters = registers,
        };
    }

    /// <summary><c>NativeAccess.MonsterInt</c> over ClrMD: a field or auto-property; computed getters are unreadable.</summary>
    internal static bool TryMonsterInt(ClrObject monster, string name, out int value)
    {
        value = 0;
        foreach (string field in new[] { name, Backing(name), "_" + char.ToLowerInvariant(name[0]) + name[1..] })
        {
            if (!Has(monster, field)) continue;
            ClrInstanceField? f = null;
            for (ClrType? t = monster.Type; t != null && f == null; t = t.BaseType)
                f = t.Fields.FirstOrDefault(x => x.Name == field);
            if (f == null) continue;
            switch (f.ElementType)
            {
                case ClrElementType.Boolean: value = monster.ReadField<bool>(field) ? 1 : 0; return true;
                case ClrElementType.Int32: value = monster.ReadField<int>(field); return true;
                case ClrElementType.UInt32: value = (int)monster.ReadField<uint>(field); return true;
                case ClrElementType.Int16: value = monster.ReadField<short>(field); return true;
                case ClrElementType.UInt8: value = monster.ReadField<byte>(field); return true;
                default: return false;
            }
        }
        return false;
    }
}
