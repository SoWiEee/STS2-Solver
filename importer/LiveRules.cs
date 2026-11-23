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
    readonly Rules _content;
    readonly Dictionary<string, List<MonsterRules>> _monsters = new(StringComparer.Ordinal);

    public RulesCatalog(string path)
    {
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
    public sealed record StaticGetter(int? Value, int? Ascension, int? High, int? Low);
    readonly Dictionary<string, Dictionary<string, StaticGetter>> _statics = [];

    internal bool TryStatic(string monster, string name, int ascension, out int value)
    {
        value = 0;
        if (!_statics.TryGetValue(monster, out var getters) || !getters.TryGetValue(name, out var g)) return false;
        if (g.Value is int constant) { value = constant; return true; }
        if (g is { Ascension: int level, High: int high, Low: int low }) { value = ascension >= level ? high : low; return true; }
        return false;
    }

    internal Rules Content => _content;

    /// <summary>Canonical stats of a card at an upgrade level, or null when the catalog lacks it.</summary>
    public CardStats? CardStats(string nativeId, int upgrade)
    {
        if (!Registry.TryCard(nativeId, out ushort id)) return null;
        try { return _content.Card(id, upgrade); } catch (UnsupportedException) { return CardSupplement.Stats(nativeId, upgrade); }
    }
    public CardStats? RewardCardStats(string nativeId, int upgrade)
    {
        if (!Registry.TryCard(nativeId, out ushort id)) return null;
        try { return _content.Card(id, upgrade); }
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
        AddCardClosure(live, draft, cards, catalogPools, content, spawnClosure);

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
        allCreatures.AddRange(live.Enemies);
        int swordSage = 0;
        foreach (ClrObject creature in allCreatures)
            foreach (ClrObject power in LiveState.Powers(creature))
            {
                if (Get<int>(power, "_amount") == 0) continue;
                string powerId = Id(power);
                Require(Registry.TryPower(powerId, out ushort pid), $"未支援的能力 {powerId}");
                identity |= Registry.Power(pid).IsIdentityContent;
                Require(powerId is not ("THIEVERY_POWER" or "HEIST_POWER" or "SWIPE_POWER" or "SURPRISE_POWER"),
                    $"偷竊相關能力 {powerId} 暫不支援");
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
        draft.EncounterSlots = encounter.IsNull ? [] : EncounterSlots(encounter, live);
        string? encounterId = encounter.IsNull ? null : Id(encounter);
        draft.HasTheftContent = encounterId is "GREMLIN_MERC_NORMAL" or "THIEVING_HOPPER_WEAK";
        Require(Obj(live.Combat, "Dominion").IsNull, "恐懼模式領域暫不支援");
        draft.TotalFloor = List(Obj(live.Run, "_mapPointHistory")).Sum(act => Get<int>(act, "_size"));
        int mode = Get<int>(live.Run, "GameMode");
        Require(mode == 1, "目前只支援標準模式");
        draft.Mode = CombatMode.Standard;
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
        Dictionary<string, ushort[]> catalogPools, Rules content, List<ushort> spawnClosure)
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
            CardStats? Stat(int level) => (level < catalogCards[id].Length ? catalogCards[id][level] : null)
                ?? CardSupplement.Stats(name, level);
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
        foreach (ClrObject creature in new[] { live.Creature }.Concat(live.Enemies))
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
                    Require(catalogPools.TryGetValue(key, out ushort[]? pool), $"規則目錄沒有卡池 {key}");
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
            Require(TryMonsterInt(monster, def.AiRegisterNames[i], out registers[i])
                || catalog.TryStatic(monsterId, def.AiRegisterNames[i], ascension, out registers[i]),
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
