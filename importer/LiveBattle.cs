// ClrMD port of rank-Yu/sts2-solver src/Import/Combat/BattleImporter.cs and BattleProjections.cs
// (MIT, commit f0167be; itself ported from CombatSolver, MIT, Copyright (c) 2026 Torch1230 and
// contributors). Field mapping and the all-or-nothing rejection follow upstream; state the upstream
// importer projects but this port does not read yet is rejected explicitly instead of defaulted.
using Microsoft.Diagnostics.Runtime;
using Sts2Solver.Simulation.Combat;
using static Sts2Solver.LiveImport.Clr;

namespace Sts2Solver.LiveImport;

internal static class LiveBattle
{
    const short FetchPlayedBit = 1;

    public static Battle Import(LiveState live, Rules rules, List<string> notes)
    {
        var history = new LiveHistory(live, rules);
        partnered = live.Partnered;
        if (partnered) notes.Add("多人戰鬥：只模擬你的角色，隊友的出牌、傷害與承受的攻擊都不計入，建議僅供參考");
        var ownerOf = new Dictionary<ulong, byte> { [live.Creature.Address] = Limits.PlayerOwner };
        for (int i = 0; i < live.Enemies.Count; i++) ownerOf[live.Enemies[i].Address] = (byte)(Limits.FirstEnemyOwner + i);
        ClrObject osty = live.Pet("Osty"), wuppy = live.Pet("Wuppy");
        Require(live.Pets.All(p => p.Address == osty.Address || p.Address == wuppy.Address), "未支援的召喚物");
        // Partners and their pets are left out of a multiplayer import (LiveState.Partnered).
        Require(live.Partnered || List(Obj(live.Combat, "_allies")).All(a => a.Address == live.Creature.Address || live.Pets.Any(p => p.Address == a.Address)),
            "場上有其他友方生物，暫不支援");
        if (!osty.IsNull) ownerOf[osty.Address] = Limits.OstyOwner;
        if (!wuppy.IsNull) ownerOf[wuppy.Address] = Limits.WuppyOwner;

        Battle battle = default;
        battle.Round = live.Round;
        battle.PlayerTurn = live.TurnNumber;
        battle.ShuffleEventCount = 0;
        battle.NextCombatId = Get<uint>(live.Combat, "_nextCreatureId");
        battle.Energy = Get<int>(live.PlayerState, "_energy");
        battle.Stars = Get<int>(live.PlayerState, "_stars");
        battle.Gold = Get<int>(live.Player, "_gold");
        battle.Side = (Side)(byte)live.Side;
        battle.Phase = PlayerPhase.Play;
        battle.Outcome = Outcome.None;
        if (Get<bool>(live.Player, "IsActiveForHooks")) battle.Flags |= BattleFlags.PlayerHooksActive;
        battle.MaxHandSize = 10; // CardPile.MaxCardsInHand: ldc.i4.s 10 (REA, docs/game.md)
        battle.Player = Vitals(live.Creature, history);
        // BattleImporter: Osty in _allies (alive or retained) marks OstyEverPresent; Player.Osty fills the slot.
        if (!osty.IsNull)
        {
            battle.Osty = Vitals(osty, history);
            battle.Flags |= BattleFlags.OstyPresent | BattleFlags.OstyEverPresent;
        }
        if (!wuppy.IsNull) battle.Wuppy = Vitals(wuppy, history);

        var dampen = ReadDampen(live);
        ImportCounters(ref battle, live, history);
        battle.SetCounter(Counter.DampenCasterMask, dampen.Mask);
        ImportRng(ref battle, live);
        ImportEnemies(ref battle, live, rules, history);
        ImportPowers(ref battle, live, ownerOf);
        ImportTheft(ref battle, live, ownerOf, rules, notes);
        ImportCards(ref battle, live, rules, history, notes, dampen.Original);
        ImportRelics(ref battle, live, rules, history);
        ImportPotions(ref battle, live);
        ImportOrbs(ref battle, live);
        if (rules.HasFoolMechanics || List(Obj(live.PlayerState, "_arrows")).Count > 0) ImportFool(ref battle, live, history, ownerOf);
        Require(!rules.HasDecimalCardVars, "小數傷害變數的卡牌暫不支援");
        return battle;
    }

    /// <summary>Port of upstream FoolImporter.ImportFool: arrows, Impaled chains, Show Off/Aim card links, melodies.</summary>
    static void ImportFool(ref Battle battle, LiveState live, LiveHistory history, Dictionary<ulong, byte> ownerOf)
    {
        List<ClrObject> arrows = List(Obj(live.PlayerState, "_arrows"), FoolState.ArrowCapacity + 1);
        Require(arrows.Count <= FoolState.ArrowCapacity, $"箭數量 {arrows.Count}");
        var arrowAt = new Dictionary<ulong, int>();
        battle.Fool.ArrowCount = (byte)arrows.Count;
        for (int i = 0; i < arrows.Count; i++)
        {
            arrowAt[arrows[i].Address] = i;
            battle.Fool.Arrows[i].SetStatus(Get<int>(arrows[i], "_status"));
            // 0.114 removed the arrow Destroyed state; 0.113.1 slots still carry DestroyAfterImpaled.
            battle.Fool.Arrows[i].DestroyAfterImpaled = Has(arrows[i], "DestroyAfterImpaled") && Get<bool>(arrows[i], "DestroyAfterImpaled");
        }
        var slotOf = new Dictionary<ulong, int>();
        int position = 0;
        foreach (ClrObject pile in live.Piles())
            foreach (ClrObject card in LiveState.Cards(pile)) slotOf[card.Address] = position++;
        foreach (ClrObject creature in new[] { live.Creature }.Concat(live.Pets).Concat(live.Enemies))
        {
            byte owner = ownerOf[creature.Address];
            int showOffOccurrence = 0;
            foreach (ClrObject power in LiveState.Powers(creature))
            {
                if (Get<int>(power, "_amount") == 0) continue;
                switch (Id(power))
                {
                    case "IMPALED_POWER":
                    {
                        int row = Kernel.ImpaledPowerIndex(battle, owner);
                        if (row < 0) break;
                        battle.Powers[row].Aux0 = Var(power, "HitCounter");
                        List<ClrObject> source = List(Obj(Obj(power, "_internalData"), "arrowSources"), 64);
                        // Multiplayer: a partner's arrows in the same Impaled stack are not simulated; keep ours.
                        if (live.Partnered) source = source.Where(a => arrowAt.ContainsKey(a.Address)).ToList();
                        // Only the partner's arrows: the stack is theirs, so it is not part of this player's fight.
                        if (live.Partnered && source.Count == 0) { Kernel.RemovePowerAt(ref battle, row); break; }
                        for (int i = 0; i < source.Count; i++)
                        {
                            Require(arrowAt.TryGetValue(source[i].Address, out int arrow), "穿刺引用了其他玩家的箭");
                            if (i == 0) battle.Powers[row].Aux1 = arrow + 1;
                            battle.Fool.Arrows[arrow].Next = i + 1 < source.Count ? checked((byte)(arrowAt[source[i + 1].Address] + 1)) : (byte)0;
                        }
                        break;
                    }
                    case "SHOW_OFF_POWER":
                    {
                        Registry.TryPower("SHOW_OFF_POWER", out ushort id);
                        int row = Kernel.FindPowerOccurrence(battle, owner, id, showOffOccurrence++);
                        Require(row >= 0, "炫技能力實例缺失");
                        ClrObject source = Obj(power, "_showOffInstance");
                        battle.Powers[row].Aux0 = source.IsNull ? 0 : -1;
                        battle.Powers[row].Aux1 = source.IsNull || Obj(source, "_deckVersion").IsNull ? 0 : 1;
                        if (Link(ref battle, slotOf, source) is int slot and > 0) battle.Powers[row].Aux0 = slot;
                        break;
                    }
                    case "AIM_POWER":
                    {
                        Registry.TryPower("AIM_POWER", out ushort id);
                        int row = Kernel.FindPower(battle, owner, id);
                        ClrObject command = Obj(Obj(power, "_internalData"), "commandToModify");
                        if (command.IsNull) break;
                        battle.Powers[row].Aux0 = -1;
                        if (Link(ref battle, slotOf, Obj(command, "ModelSource")) is int slot and > 0) battle.Powers[row].Aux0 = slot;
                        break;
                    }
                }
            }
        }
        CardTag[] tags = [CardTag.Wah, CardTag.Cho, CardTag.Ki];
        for (int i = 0; i < tags.Length; i++)
        {
            battle.Fool.Started[i] = history.MelodyStarted(tags[i]);
            battle.Fool.Finished[i] = history.MelodyFinished(tags[i]);
        }
        battle.Fool.MelodyCount = history.MelodyCount;
        battle.Fool.SadnessGenerated = history.SadnessGenerated;
        Kernel.NormalizeFoolState(ref battle);
    }

    /// <summary>
    /// BattleImporter.ImportSwipeSelection and ScanHeldLoot: each SwipePower row holds the stolen card in a detached
    /// slot (Aux0 = slot + 1), and the stolen gold/card counts feed the theft score.
    /// </summary>
    static void ImportTheft(ref Battle battle, LiveState live, Dictionary<ulong, byte> ownerOf, Rules rules, List<string> notes)
    {
        int gold = 0, cards = 0;
        Registry.TryPower("SWIPE_POWER", out ushort swipeId);
        foreach (ClrObject enemy in live.Enemies)
        {
            if (Get<int>(enemy, "_currentHp") <= 0) continue;
            byte owner = ownerOf[enemy.Address];
            int occurrence = 0;
            foreach (ClrObject power in LiveState.Powers(enemy))
            {
                if (Get<int>(power, "_amount") == 0) continue;
                switch (Id(power))
                {
                    case "THIEVERY_POWER": gold += Math.Max(0, Var(power, "Gold")); break;
                    case "HEIST_POWER": gold += Math.Max(0, Get<int>(power, "_amount")); break;
                    case "SWIPE_POWER":
                    {
                        int row = Kernel.FindPowerOccurrence(battle, owner, swipeId, occurrence++);
                        ClrObject stolen = Obj(power, "_stolenCard");
                        if (stolen.IsNull || row < 0) break;
                        cards++;
                        Card value = ImportCard(stolen, rules, notes);
                        int slot = 0;
                        while (slot < Limits.DetachedCards && battle.DetachedCards[slot].IsDetachedCardPresent) slot++;
                        Require(slot < Limits.DetachedCards, "被偷走的牌超過可記錄數量");
                        battle.DetachedCards[slot] = value;
                        battle.Powers[row].Aux0 = slot + 1;
                        break;
                    }
                }
            }
        }
        battle.SetCounter(Counter.OutstandingStolenGold, gold);
        battle.SetCounter(Counter.OutstandingStolenCards, cards);
    }

    static int Link(ref Battle battle, Dictionary<ulong, int> slotOf, ClrObject card)
    {
        if (card.IsNull || !slotOf.TryGetValue(card.Address, out int slot)) return -1;
        battle.Cards[slot].FoolIdentity = checked((ushort)(slot + 1));
        return slot + 1;
    }

    static Sts2Solver.Simulation.Combat.Creature Vitals(ClrObject creature, LiveHistory history) => new()
    {
        CurrentHp = Get<int>(creature, "_currentHp"),
        MaxHp = Get<int>(creature, "_maxHp"),
        Block = Get<int>(creature, "_block"),
        Flags = CreatureFlags.Present | (history.HasLostHpThisTurn(creature.Address) ? CreatureFlags.LostHpThisTurn : CreatureFlags.None),
    };

    /// <summary>
    /// BattleImporter.DampenCasterMask and the per-card original upgrades (DampenPower._internalData:
    /// casters, downgradedCardsToOldUpgradeLevels). With no recorded caster the applier stands in.
    /// </summary>
    static (int Mask, Dictionary<ulong, int> Original) ReadDampen(LiveState live)
    {
        int mask = 0;
        var original = new Dictionary<ulong, int>();
        foreach (ClrObject power in LiveState.Powers(live.Creature))
        {
            if (Id(power) != "DAMPEN_POWER" || Get<int>(power, "_amount") <= 0) continue;
            ClrObject data = Obj(power, "_internalData");
            Require(!data.IsNull, "Dampen 的內部資料缺失");
            var casters = HashEntries(Obj(data, "casters")).Select(e => e.ReadObjectField("Value").Address).ToList();
            if (casters.Count == 0)
            {
                ClrObject applier = Obj(power, "_applier");
                Require(!applier.IsNull && Get<int>(applier, "_currentHp") > 0, "壓制存在但沒有存活的施法者");
                casters.Add(applier.Address);
            }
            foreach (ulong caster in casters)
            {
                int index = live.Enemies.FindIndex(e => e.Address == caster);
                Require(index is >= 0 and < 32, "Dampen 施法者不在敵人名單");
                mask |= 1 << index;
            }
            foreach (ClrValueType entry in HashEntries(Obj(data, "downgradedCardsToOldUpgradeLevels")))
                original[entry.ReadObjectField("key").Address] = entry.ReadField<int>("value");
        }
        return (mask, original);
    }

    static void ImportCounters(ref Battle battle, LiveState live, LiveHistory h)
    {
        battle.SetCounter(Counter.CardsPlayedThisTurn, h.CardsPlayedThisTurn);
        battle.SetCounter(Counter.ManualCardsPlayedThisTurn, h.ManualCardsPlayedThisTurn);
        battle.SetCounter(Counter.CardPlayStartsThisTurn, h.CardPlayStartsThisTurn);
        battle.SetCounter(Counter.AttackPlayStartsThisTurn, h.AttackPlayStartsThisTurn);
        battle.SetCounter(Counter.AttackSkillStartsThisTurn, h.AttackSkillStartsThisTurn);
        battle.SetCounter(Counter.ZeroCostAttackStartsThisTurn, h.ZeroCostAttackStartsThisTurn);
        battle.SetCounter(Counter.CardPlaySeriesStartedThisTurn, h.CardsPlayedThisTurn);
        battle.SetCounter(Counter.AttacksPlayedThisTurn, h.AttacksPlayedThisTurn);
        battle.SetCounter(Counter.ShivsPlayedThisTurn, h.ShivsPlayedThisTurn);
        battle.SetCounter(Counter.SkillCardsPlayedThisTurn, h.SkillCardsPlayedThisTurn);
        battle.SetCounter(Counter.BlockCardsPlayedThisTurn, h.BlockCardsPlayedThisTurn);
        battle.SetCounter(Counter.CardsExhaustedThisTurn, h.CardsExhaustedThisTurn);
        battle.SetCounter(Counter.CardsDiscardedThisTurn, h.CardsDiscardedThisTurn);
        battle.SetCounter(Counter.PlayerAttacksThisTurn, h.PlayerAttacksThisTurn);
        battle.SetCounter(Counter.NonHandDrawsThisTurn, h.NonHandDrawsThisTurn);
        battle.SetCounter(Counter.StatusCardsDrawnThisTurn, h.StatusCardsDrawnThisTurn);
        battle.SetCounter(Counter.EnergySpentThisTurn, h.EnergySpentThisTurn);
        battle.SetCounter(Counter.StarsGainedThisTurn, h.StarsGainedThisTurn);
        // A fresh root (not a reconciliation): cumulative ledgers start at zero, as upstream.
        battle.SetCounter(Counter.CumulativeHpLost, 0);
        battle.SetCounter(Counter.RecoveredHp, 0);
        battle.SetCounter(Counter.PlayerUnblockedDamageEvents, h.UnblockedDamageEventsThisTurn);
        battle.SetCounter(Counter.LongTermResourceValue, 0);
        battle.SetCounter(Counter.AngerCopiesGenerated, 0);
        battle.SetCounter(Counter.DeathSaveRelicHpRestored, 0);
        battle.SetCounter(Counter.DeathSavePotionHpRestored, 0);
        battle.SetCounter(Counter.DeathSaveUseCount, 0);
        battle.SetCounter(Counter.AutomaticPotionUseCount, 0);
        battle.SetCounter(Counter.VitalSparkNormalizedAmount, int.MinValue);
        battle.SetCounter(Counter.SkillPlayedThisSideTurn, 0);
        battle.SetCounter(Counter.FinishedCardPlays, h.FinishedCardPlays);
        battle.SetCounter(Counter.CardsDrawn, h.CardsDrawn);
        battle.SetCounter(Counter.LightningChannels, h.LightningChannels);
        ClrObject osty = live.Pet("Osty");
        battle.SetCounter(Counter.OstyAttacksThisTurn, osty.IsNull ? 0 : h.CreatureAttacksThisTurn(osty.Address));
        battle.SetCounter(Counter.DoomAppliedThisTurn, h.DoomAppliedThisTurn ? 1 : 0);
        battle.SetCounter(Counter.EtherealPlays, h.EtherealPlays);
        battle.SetCounter(Counter.CardsGeneratedThisCombat, h.CardsGenerated);
        battle.SetCounter(Counter.OrbChannels, 0);
        battle.SetCounter(Counter.OutstandingStolenGold, 0);
        battle.SetCounter(Counter.OutstandingStolenCards, 0);
        battle.SetCounter(Counter.EntropicFreePotionMask, 0);
        battle.SetCounter(Counter.GrowthRewardsHandOfGreedTheHunt, 0);
        battle.SetCounter(Counter.GrowthRewardsFeedRoyalties, 0);
        battle.SetCounter(Counter.GrowthRewardsAlchemizeGeneticAlgorithm, 0);
        battle.SetCounter(Counter.GrowthRewardsTheScytheGoopy, 0);
        battle.SetCounter(Counter.GrowthRewardsForbiddenGrimoireMadScience, 0);
        battle.SetCounter(Counter.GeneratedPotionUseCount, 0);
        battle.SetCounter(Counter.GeneratedPotionStrategicCost, 0);
    }

    // RunRngType: UpFront, Shuffle, UnknownMapPoint, CombatCardGeneration, CombatPotionGeneration,
    // CombatCardSelection, CombatEnergyCosts, CombatTargets, MonsterAi, Niche, CombatOrbs, TreasureRoomRelics.
    static void ImportRng(ref Battle battle, LiveState live)
    {
        var run = RngMap(Obj(Obj(live.Run, "Rng"), "_rngs"));
        var player = RngMap(Obj(Obj(live.Player, "PlayerRng"), "_rngs"));
        Set(ref battle, RngStream.Shuffle, run, 1);
        Set(ref battle, RngStream.CombatCardGeneration, run, 3);
        Set(ref battle, RngStream.CombatPotionGeneration, run, 4);
        Set(ref battle, RngStream.CombatCardSelection, run, 5);
        Set(ref battle, RngStream.CombatEnergyCosts, run, 6);
        Set(ref battle, RngStream.CombatTargets, run, 7);
        Set(ref battle, RngStream.MonsterAi, run, 8);
        Set(ref battle, RngStream.Niche, run, 9);
        Set(ref battle, RngStream.CombatOrbGeneration, run, 10);
        Set(ref battle, RngStream.NonCombatGeneration, player, 3); // PlayerRngType.NonCombatGeneration

        static Dictionary<int, ClrObject> RngMap(ClrObject dictionary) =>
            HashEntries(dictionary).ToDictionary(e => e.ReadField<int>("key"), e => e.ReadObjectField("value"));
        static void Set(ref Battle battle, RngStream stream, Dictionary<int, ClrObject> map, int key)
        {
            Require(map.TryGetValue(key, out ClrObject rng), $"亂數流 {stream} 不存在");
            ClrObject random = Obj(rng, "_random");
            battle.RngStream(stream) = RngState.FromSerializable(Get<int>(rng, "_counter"),
                Get<ulong>(random, "_s0"), Get<ulong>(random, "_s1"), Get<ulong>(random, "_s2"), Get<ulong>(random, "_s3"));
        }
    }

    static void ImportEnemies(ref Battle battle, LiveState live, Rules rules, LiveHistory history)
    {
        battle.EnemyCount = checked((byte)live.Enemies.Count);
        int roster = 0;
        for (int index = 0; index < live.Enemies.Count; index++)
        {
            ClrObject creature = live.Enemies[index];
            ClrObject monster = Obj(creature, "Monster");
            string monsterId = Id(monster);
            ushort mid = Registry.TryMonster(monsterId, out ushort found) ? found : throw new LiveUnsupportedException($"未支援的敵人 {monsterId}");
            MonsterRules machine = rules.MonsterRule((byte)index);
            ref Enemy enemy = ref battle.Enemies[index];
            enemy.Creature = Vitals(creature, history);
            enemy.MonsterId = mid;
            enemy.Rules = (byte)index;
            enemy.Slot = SlotFor(rules, Str(creature, "SlotName"));
            enemy.CombatId = Nullable<uint>(creature, "CombatId") ?? throw new LiveUnsupportedException("敵人沒有 CombatId");
            ClrObject next = Obj(monster, "NextMove");
            string current = Str(next, "StateId") ?? throw new LiveUnsupportedException($"{monsterId} 沒有當前行動");
            // Upstream BattleImporter: a dead enemy whose next move revives it stays on the roster as Reviving.
            bool reviving = Get<int>(creature, "_currentHp") <= 0;
            Require(!reviving || current is "REVIVE_MOVE" or "RESPAWN_MOVE" or "DEAD_MOVE" or "REATTACH_MOVE", $"{monsterId} 已死亡但仍在名單中，暫不支援");
            enemy.DeathPhase = reviving ? DeathPhase.Reviving : DeathPhase.None;
            if (reviving) enemy.Flags = EnemyFlags.DeathProcessed | EnemyFlags.InCombat;
            Require(current != "UNSET_MOVE", $"{monsterId} 的行動尚未決定");
            if (current is "STUNNED" or "REVIVE_MOVE")
            {
                ClrObject follow = Obj(next, "FollowUpState");
                string? followUp = follow.IsNull ? Str(next, "FollowUpStateId") : StateId(follow);
                Require(followUp != null && followUp is not ("STUNNED" or "REVIVE_MOVE"), $"{monsterId} 的 {current} 沒有可用的後續行動");
                Require(current == "STUNNED" || machine.ReviveNode >= 0, $"{monsterId} 的復活行動沒有節點");
                enemy.Move = checked((byte)(current == "STUNNED" ? machine.StunnedNode : machine.ReviveNode));
                enemy.ForcedMove = checked((byte)Node(machine, followUp!, monsterId));
                if (current == "STUNNED" && LiveState.Powers(creature).Any(p => Id(p) == "IMBALANCED_POWER" && Get<int>(p, "_amount") > 0))
                {
                    Require(monsterId != "SLUMBERING_BEETLE", "沉睡甲蟲的失衡暈眩暫不支援");
                    enemy.Flags |= EnemyFlags.SkipNextMove;
                }
            }
            else
            {
                enemy.Move = checked((byte)Node(machine, current, monsterId));
                enemy.ForcedMove = byte.MaxValue;
            }
            ClrObject stateMachine = Obj(monster, "_moveStateMachine");
            Require(!stateMachine.IsNull, $"{monsterId} 沒有行動狀態機");
            foreach (ClrObject logged in List(Obj(stateMachine, "StateLog"), 4096))
            {
                int node = Node(machine, StateId(logged), monsterId);
                Require(node < 64, $"行動節點 {node}");
                Kernel.LogMonsterMove(ref enemy, node);
            }
            enemy.PoweredAttackHitsThisTurn = history.PoweredAttackHitsThisTurn(creature.Address);
            enemy.SteamPhase = monsterId == "WATERFALL_GIANT"
                ? current switch { "ABOUT_TO_BLOW_MOVE" => SteamPhase.AboutToBlow, "EXPLODE_MOVE" => SteamPhase.Explode, _ => SteamPhase.None }
                : SteamPhase.None;
            MonsterDef def = Registry.Monster(mid);
            Require(def.AiRegisterNames.Count <= Limits.EnemyAiRegisters, $"{monsterId} AI 暫存器過多");
            for (int r = 0; r < def.AiRegisterNames.Count; r++)
                // A register backed by a computed getter was resolved through the REA table when Rules were built.
                if (!LiveRules.TryMonsterInt(monster, def.AiRegisterNames[r], out enemy.Ai[r]) && r < machine.InitialAiRegisters.Length)
                    enemy.Ai[r] = machine.InitialAiRegisters[r];
                else Require(LiveRules.TryMonsterInt(monster, def.AiRegisterNames[r], out enemy.Ai[r]),
                    $"{monsterId} 的 AI 狀態 {def.AiRegisterNames[r]} 無法讀取");
            battle.Roster[roster++] = (byte)index;
            enemy.Flags |= EnemyFlags.InCombat;
        }
        battle.RosterCount = (byte)roster;
    }

    static string StateId(ClrObject state) =>
        (Has(state, "StateId") ? Str(state, "StateId") : Has(state, "_id") ? Str(state, "_id") : Has(state, "Id") ? Str(state, "Id") : null)
        ?? throw new LiveUnsupportedException($"無法讀取 {TypeName(state)} 的狀態 id");

    static int Node(MonsterRules machine, string id, string monster)
    {
        for (int i = 0; i < machine.Nodes.Length; i++)
            if (machine.Nodes[i].NativeId == id) return i;
        throw new LiveUnsupportedException($"{monster} 的行動 {id} 不在目錄的狀態機中");
    }

    static byte SlotFor(Rules rules, string? slot)
    {
        if (slot == null) return (byte)Kernel.NoSlot;
        for (int i = 0; i < rules.EncounterSlots.Count; i++)
            if (rules.EncounterSlots[i] == slot) return (byte)i;
        return (byte)Kernel.NoSlot;
    }

    // BattleProjections.ProjectPower: powers with no private state, plus the ones read here.
    static readonly HashSet<string> NoPrivateState = ["GIGANTIFICATION_POWER", "VIGOR_POWER", "RUPTURE_POWER",
        "CALAMITY_POWER", "AFTERIMAGE_POWER", "STRANGLE_POWER", "STORM_POWER", "SUBROUTINE_POWER", "OBLIVION_POWER",
        "SERPENT_FORM_POWER", "ROLLING_BOULDER_POWER"];
    static readonly HashSet<string> ProjectedUnsupported = ["CHAINS_OF_BINDING_POWER", // needs per-card Bound afflictions
        "NIGHTMARE_POWER", "SOULBOUND_POWER", "CACOPHONY_POWER"];

    // BattleProjections.ProjectPower cases whose private state is a DynamicVar or a PowerModel._internalData field.
    static void ProjectPower(string powerId, ClrObject power, ref Power target)
    {
        switch (powerId)
        {
            case "THIEVERY_POWER": target.Aux0 = Var(power, "Gold"); break; // the gold ledger of this theft row
            case "ENCORE_POWER": target.Aux0 = Data(power, "numberOfCardsPlayedSinceApplied"); break;
            case "POSSESS_STRENGTH_POWER": target.Aux0 = Data(power, "stolenStrength"); break;
            case "POSSESS_SPEED_POWER": target.Aux0 = Data(power, "stolenDexterity"); break;
            case "AUTOMATION_POWER": target.Aux0 = Data(power, "cardsLeft"); break;
            case "ORBIT_POWER": target.Aux0 = Data(power, "energySpent") % 4; break;
            case "TENDER_POWER": target.Aux0 = Get<int>(power, "_cardsPlayedThisTurn"); break;
            case "TORIC_TOUGHNESS_POWER": Kernel.SetInstanceBlockValue(ref target, Var(power, "Block")); break;
            case "PALE_BLUE_DOT_POWER": target.Aux0 = Data(power, "alreadyActivatedThisTurn"); break;
            case "LULLABY_POWER": target.Aux0 = Get<bool>(power, "_proccdThisTurn") ? 1 : 0; break;
            case "NEMESIS_POWER": target.Aux0 = Get<bool>(power, "_shouldApplyIntangible") ? 1 : 0; break;
            case "SLOTH_POWER": target.Aux0 = Get<int>(power, "_cardsPlayedThisTurn"); break;
            case "SKITTISH_POWER": target.Aux0 = Data(power, "hasGainedBlockThisTurn"); break;
            case "SURROUNDED_POWER": target.Aux0 = Get<int>(power, "Facing"); break;
            case "CURL_UP_POWER": target.Aux0 = 0; target.Aux1 = 0; break;
            case "MYTHIC_FORM_POWER": target.Aux0 = Var(power, "Upgraded"); break;
            case "ENNUI_POWER": target.Aux0 = Var(power, "Cards"); break;
            case "CRIMSON_MANTLE_POWER": case "INFERNO_POWER": target.Aux0 = Var(power, "SelfDamage"); break;
            case "SLOW_POWER": target.Aux0 = Var(power, "SlowAmount"); break;
            case "WITHERING_PRESENCE_POWER": target.Aux0 = Var(power, "CardsLeft"); break;
            case "THE_BOMB_POWER": target.Aux0 = Var(power, "Damage"); break;
            case "MONOLOGUE_POWER": target.Aux0 = Var(power, "Strength"); target.Aux1 = Var(power, "StrengthApplied"); break;
            case "PANACHE_POWER": target.Aux0 = Var(power, "CardsLeft"); target.Aux1 = Data(power, "alreadyApplied"); break;
            case "YAH_POWER": target.Aux0 = Data(power, "triggeredThisTurn"); break;
            case "JUGGLING_POWER": target.Aux0 = Data(power, "attacksPlayedThisTurn"); break;
            case "DARK_EMBRACE_POWER": target.Aux0 = Data(power, "etherealCount"); break;
            case "VOID_FORM_POWER": target.Aux0 = Data(power, "cardsPlayedThisTurn"); break;
            case "HARDENED_SHELL_POWER": target.Aux0 = Data(power, "damageReceivedThisTurn"); break;
            case "HELLRAISER_POWER": target.Aux0 = Data(power, "infiniteAutoPlaysThisTurn"); break;
            case "FERAL_POWER": target.Aux0 = Data(power, "zeroCostAttacksPlayed"); break;
        }
    }

    /// <summary>DynamicVars[name].IntValue, i.e. (int)BaseValue.</summary>
    static int Var(ClrObject model, string name)
    {
        ClrObject vars = Obj(model, "_dynamicVars");
        Require(!vars.IsNull, $"{Id(model)} 沒有動態變數");
        foreach (ClrValueType entry in HashEntries(Obj(vars, "_vars")))
            if (entry.ReadObjectField("key").AsString() == name)
                return (int)Get<decimal>(entry.ReadObjectField("value"), "_baseValue");
        throw new LiveUnsupportedException($"{Id(model)} 沒有變數 {name}");
    }

    /// <summary>A field of PowerModel._internalData (BattleProjections' PowerData&lt;T&gt;).</summary>
    static int Data(ClrObject power, string field)
    {
        ClrObject data = Obj(power, "_internalData");
        Require(!data.IsNull, $"{Id(power)} 的內部資料缺失");
        return IntOrBool(data, field);
    }

    static void ImportPowers(ref Battle battle, LiveState live, Dictionary<ulong, byte> ownerOf)
    {
        var ordered = new List<(byte Owner, ClrObject Power)>();
        foreach (ClrObject power in LiveState.Powers(live.Creature))
            if (Get<int>(power, "_amount") != 0) ordered.Add((Limits.PlayerOwner, power));
        foreach (string pet in new[] { "Osty", "Wuppy" })
        {
            ClrObject creature = live.Pet(pet);
            if (creature.IsNull) continue;
            foreach (ClrObject power in LiveState.Powers(creature))
                if (Get<int>(power, "_amount") != 0) ordered.Add((ownerOf[creature.Address], power));
        }
        for (int i = 0; i < live.Enemies.Count; i++)
            foreach (ClrObject power in LiveState.Powers(live.Enemies[i]))
                if (Get<int>(power, "_amount") != 0) ordered.Add(((byte)(Limits.FirstEnemyOwner + i), power));
        ordered = ordered.OrderBy(p => p.Owner).ToList(); // stable: native order within an owner
        Require(ordered.Count <= Limits.Powers, $"能力數 {ordered.Count}");
        battle.PowerCount = (byte)ordered.Count;
        for (int index = 0; index < ordered.Count; index++)
        {
            (byte owner, ClrObject power) = ordered[index];
            string powerId = Id(power);
            Require(Registry.TryPower(powerId, out ushort id), $"未支援的能力 {powerId}");
            Require(!ProjectedUnsupported.Contains(powerId), $"能力 {powerId} 的內部狀態尚未移植");
            PowerDef def = Registry.Power(id);
            PowerFlags flags = PowerFlags.None;
            if (def.UsesNativeDurationSkip && Get<bool>(power, "_skipNextDurationTick")) flags |= PowerFlags.SkipNextDurationTick;
            if (def.MarksJustAppliedByEnemy && powerId == "RITUAL_POWER" && Get<bool>(power, "_wasJustAppliedByEnemy"))
                flags |= PowerFlags.WasJustAppliedByEnemy;
            battle.Powers[index] = new Power
            {
                Id = id,
                Owner = owner,
                Applier = OwnerOf(Obj(power, "_applier"), ownerOf),
                Target = OwnerOf(Obj(power, "_target"), ownerOf),
                Flags = flags,
                Amount = Get<int>(power, "_amount"),
                AmountOnTurnStart = def.UsesAmountOnTurnStart ? Get<int>(power, "_amountOnTurnStart") : 0,
            };
            ProjectPower(powerId, power, ref battle.Powers[index]);
        }
    }

    [ThreadStatic] static bool partnered;

    static byte OwnerOf(ClrObject creature, Dictionary<ulong, byte> ownerOf)
    {
        if (creature.IsNull) return Limits.NoOwner;
        if (ownerOf.TryGetValue(creature.Address, out byte owner)) return owner;
        if (partnered) return Limits.NoOwner; // applied by a partner, who is not simulated
        // shortcut: upstream keeps dead enemies as PermanentlyDead tombstones via a cross-turn tracker; the
        // live port sees only _enemies, so a dead applier/target (it never acts again) maps to no owner.
        Require(Get<int>(creature, "_currentHp") <= 0, "能力關聯到未知的生物");
        return Limits.NoOwner;
    }

    static readonly HashSet<string> ReturnsToHandCards = ["BOLAS", "THRUMMING_HATCHET", "BOUNCE"];

    static void ImportCards(ref Battle battle, LiveState live, Rules rules, LiveHistory history, List<string> notes,
        Dictionary<ulong, int> dampened)
    {
        Pile[] targets = [Pile.Hand, Pile.Draw, Pile.Discard, Pile.Exhaust, Pile.Play];
        int p = 0;
        var rowOf = new Dictionary<ulong, int>();
        foreach (ClrObject pile in live.Piles())
        {
            Pile target = targets[p++];
            foreach (ClrObject card in LiveState.Cards(pile))
            {
                Card value = ImportCard(card, rules, notes);
                if (dampened.TryGetValue(card.Address, out int original))
                {
                    Require(original < byte.MaxValue, $"{Id(card)} 壓制前升級等級 {original}");
                    value.DampenOriginalUpgrade = checked((byte)(original + 1));
                }
                string id = Id(card);
                // BattleImporter.ImportCards/Card: card-private state for a fresh root capture.
                if (id == "SOVEREIGN_BLADE") value.Private = checked((short)rules.RootSwordSageAmount);
                if (id == "THE_SHOT")
                {
                    decimal exhausted = Get<decimal>(card, "_cardsExhaustedByFlorescence");
                    Require(exhausted == decimal.Truncate(exhausted) && exhausted is >= 0 and <= short.MaxValue, $"TheShot 花開計數 {exhausted}");
                    value.Private = (short)exhausted;
                }
                if (id == "MAD_SCIENCE")
                {
                    int type = Get<int>(card, "TinkerTimeType"), rider = Get<int>(card, "TinkerTimeRider");
                    Require(type is 1 or 2 or 3, $"MAD_SCIENCE 的 TinkerTimeType {type}");
                    value.Private = Sts2Solver.Content.Cards.Event.MadScience.Encode(type, rider);
                }
                if (id == "FETCH" && history.FinishedThisTurnContains(card.Address)) value.Private |= FetchPlayedBit;
                if (ReturnsToHandCards.Contains(id) && history.FinishedThisTurnContains(card.Address)) value.ReturnToHandNextTurn = true;
                rowOf[card.Address] = battle.CardCount;
                Piles.Append(ref battle, target, value);
            }
        }
        // BattleImporter.ImportHistoryCourseCard at a Play root: this turn's last finished non-dupe attack.
        if (rules.HasHistoryCourse && history.HistoryCourseAttack() is ulong attack)
        {
            // Piles are appended in pile order, so the recorded row is final (upstream records CardCount the same way).
            Require(rowOf.TryGetValue(attack, out int row), "歷史課記錄的攻擊牌不在牌堆中");
            battle.Cards[row].HistoryCourseRole = 1;
            battle.HistoryCourseCard = Kernel.HistoryCourseValue(battle.Cards[row]);
            battle.Flags |= BattleFlags.HistoryCourseCardPresent;
        }
    }

    static Card ImportCard(ClrObject card, Rules rules, List<string> notes)
    {
        string nativeId = Id(card);
        Require(Registry.TryCard(nativeId, out ushort id), $"未支援的卡牌 {nativeId}");
        int upgrade = Get<int>(card, "_currentUpgradeLevel");
        CardStats stats = rules.Card(id, upgrade);
        CardDef def = Registry.Card(id);
        ClrObject cost = Obj(card, "_energyCost");
        bool costsX = Get<bool>(cost, "CostsX");
        ClrObject keywords = Obj(card, "_keywords");
        var result = new Card
        {
            Id = id,
            Upgrade = checked((byte)upgrade),
            Keywords = keywords.IsNull ? stats.Keywords : MapKeywords(IntSet(keywords)),
            KeywordsAddedFromOutside = MapKeywords(IntSet(Obj(card, "_keywordsAddedFromOutside"))),
            KeywordsRemovedFromOutside = MapKeywords(IntSet(Obj(card, "_keywordsRemovedFromOutside"))),
            BaseCost = checked((sbyte)Get<int>(cost, "_base")),
            BaseStarCost = checked((sbyte)(Get<bool>(card, "_starCostSet") ? Get<int>(card, "_baseStarCost") : stats.StarCost)),
            ReplayCount = checked((byte)Get<int>(card, "_baseReplayCount")),
            IsClone = !Obj(card, "_cloneOf").IsNull,
            IsDupe = Get<bool>(card, "_isDupe"),
            CapturedEnergy = costsX ? checked((short)Get<int>(cost, "_capturedXValue")) : (short)0,
        };
        if (def.UsesIdentityLedger) result.Private = Limits.UninitializedLedger;
        result.Flags |= CardFlags.PowerAfflictionChecked;
        if (Get<bool>(card, "_exhaustOnNextPlay")) result.Flags |= CardFlags.ExhaustOnNextPlay;
        if (Get<bool>(card, "_hasSingleTurnSly")) result.Flags |= CardFlags.SlyThisTurn;
        if (Get<bool>(card, "_hasSingleTurnRetain")) result.Flags |= CardFlags.RetainThisTurn;
        if (!Obj(card, "_deckVersion").IsNull) result.Flags |= CardFlags.DeckCard;
        if (Get<bool>(card, "HasBeenRemovedFromState")) result.Flags |= CardFlags.RemovedFromState;
        if (costsX) result.Flags |= CardFlags.CostsX;
        if (stats.StarCostsX) result.Flags |= CardFlags.StarCostsX;
        ImportCardEffects(ref result, card, notes);
        foreach (ClrObject modifier in List(Obj(cost, "_localModifiers"), 64))
            CardCostModifiers.Append(ref result, CostModifier.Create(Get<int>(modifier, "Amount"),
                (CostType)(byte)Get<int>(modifier, "Type"), (CostExpiration)(byte)Get<int>(modifier, "Expiration"),
                Get<bool>(modifier, "IsReduceOnly")));
        var starCosts = List(Obj(card, "_temporaryStarCosts"))
            .Select(entry => (Cost: Get<int>(entry, "Cost"), Turn: Get<bool>(entry, "ClearsWhenTurnEnds"),
                Play: Get<bool>(entry, "ClearsWhenCardIsPlayed"))).ToArray();
        if (starCosts.Length > 0)
        {
            bool allSame = starCosts.All(entry => entry == starCosts[0]);
            Require(allSame || (starCosts.Length <= 3 && starCosts.All(entry => entry.Cost == starCosts[0].Cost)),
                $"{nativeId} 的臨時星能費用超出模型容量");
            foreach (var entry in allSame ? starCosts.Take(1) : starCosts)
                result.SetTemporaryStarCost(entry.Cost, entry.Turn, entry.Play);
        }

        ClrObject vars = Obj(card, "_dynamicVars");
        if (!vars.IsNull)
        {
            var byName = HashEntries(Obj(vars, "_vars")).ToDictionary(e => e.ReadObjectField("key").AsString()!, e => e.ReadObjectField("value"));
            for (int index = 0; index < def.VarNames.Count; index++)
            {
                Require(byName.TryGetValue(def.VarNames[index], out ClrObject variable), $"{nativeId} 沒有變數 {def.VarNames[index]}");
                decimal delta = Get<decimal>(variable, "_baseValue") - stats.Vars[index];
                if (delta == 0) continue;
                Require(index <= 1 && delta == decimal.Truncate(delta) && delta >= short.MinValue && delta <= short.MaxValue,
                    $"{nativeId} 變數 {def.VarNames[index]} 偏移 {delta}");
                if (index == 0) result.Var0 = (short)delta; else result.Var1 = (short)delta;
            }
        }
        def.ProjectPrivate(ref result);
        return result;
    }

    static void ImportCardEffects(ref Card result, ClrObject card, List<string> notes)
    {
        ClrObject enchantment = Obj(card, "Enchantment");
        if (!enchantment.IsNull)
        {
            string id = Id(enchantment);
            int amount = Get<int>(enchantment, "_amount");
            int status = Get<int>(enchantment, "_status");
            int extra = id == "MOMENTUM" ? Get<int>(enchantment, "_extraDamage") : 0;
            if (Registry.TryEnchantment(id, out ushort eid) && eid < byte.MaxValue &&
                amount is >= short.MinValue and <= short.MaxValue && status is 0 or 1 &&
                extra is >= short.MinValue and <= short.MaxValue)
            {
                result.EnchantmentId = (byte)(eid + 1);
                result.EnchantmentAmount = (short)amount;
                result.EnchantmentStatus = (byte)status;
                result.EnchantmentPrivate = (short)extra;
            }
            else
                notes.Add($"近似預測略過附魔 {id}");
        }

        ClrObject affliction = Obj(card, "Affliction");
        if (!affliction.IsNull)
        {
            string id = Id(affliction);
            int amount = Get<int>(affliction, "_amount");
            if (Registry.TryAffliction(id, out ushort aid) && aid < byte.MaxValue &&
                amount is >= short.MinValue and <= short.MaxValue)
            {
                result.AfflictionId = (byte)(aid + 1);
                result.AfflictionAmount = (short)amount;
            }
            else
                notes.Add($"近似預測略過苦難 {id}");
        }
    }

    static Keywords MapKeywords(IEnumerable<int> keywords)
    {
        Keywords result = Keywords.None;
        foreach (int keyword in keywords)
            result |= keyword switch
            {
                1 => Keywords.Exhaust, 2 => Keywords.Ethereal, 3 => Keywords.Innate, 4 => Keywords.Unplayable,
                5 => Keywords.Retain, 6 => Keywords.Sly, 7 => Keywords.Eternal,
                _ => throw new LiveUnsupportedException($"關鍵字 {keyword}"),
            };
        return result;
    }

    // BattleProjections.ProjectRelic: relics whose counter is a plain field.
    static readonly Dictionary<string, (string? Counter, string? Aux)> RelicFields = new()
    {
        ["CONDUCTORS_BATON"] = ("_triggeredThisTurn", null),
        ["VAMBRACE"] = ("_blockGainedThisCombat", null),
        ["BURNING_STICKS"] = ("_wasUsedThisCombat", null),
        ["DEMON_TONGUE"] = ("_triggeredThisTurn", null),
        ["KUNAI"] = ("_attacksPlayedThisTurn", null),
        ["KUSARIGAMA"] = ("_attacksPlayedThisTurn", null),
        ["LETTER_OPENER"] = ("_skillsPlayedThisTurn", null),
        ["ORNAMENTAL_FAN"] = ("_attacksPlayedThisTurn", null),
        ["SHURIKEN"] = ("_attacksPlayedThisTurn", null),
        ["PERMAFROST"] = ("_activatedThisCombat", null),
        ["THROWING_AXE"] = ("_usedThisCombat", null),
        ["METRONOME"] = ("_orbsChanneled", null),
        ["BRILLIANT_SCARF"] = ("_cardsPlayedThisTurn", null),
        ["REGALITE"] = ("_usedThisTurn", null),
        ["BEATING_REMNANT"] = ("_damageReceivedThisTurn", null),
        ["RAINBOW_RING"] = ("_activationCountThisTurn", "_powersPlayedThisTurn"),
        ["JOSS_PAPER"] = ("CardsExhausted", "_etherealCount"),
        ["NUNCHAKU"] = ("AttacksPlayed", null),
        ["PEN_NIB"] = ("AttacksPlayed", null),
        ["TUNING_FORK"] = ("SkillsPlayed", null),
        ["HAPPY_FLOWER"] = ("TurnsSeen", null),
        ["FAKE_HAPPY_FLOWER"] = ("TurnsSeen", null),
        ["PENDULUM"] = ("_turnsSeen", null),
        ["POLLINOUS_CORE"] = ("TurnsSeen", null),
        ["IRON_CLUB"] = ("CardsPlayed", null),
        ["PUMPKIN_CANDLE"] = ("KindleCount", null),
        ["BYRDPIP"] = ("SwoopsPlayed", null),
        ["CENTENNIAL_PUZZLE"] = ("UsedThisCombat", null),
        ["LAVA_LAMP"] = ("TookDamageThisCombat", null),
        ["LIZARD_TAIL"] = ("WasUsed", null),
        ["RED_SKULL"] = ("_strengthApplied", null),
        ["BELT_BUCKLE"] = ("_dexterityApplied", null),
        ["VENERABLE_TEA_SET"] = ("GainEnergyInNextCombat", null),
        ["FAKE_VENERABLE_TEA_SET"] = ("GainEnergyInNextCombat", null),
        ["BONE_TEA"] = ("CombatsLeft", null),
        ["GALACTIC_DUST"] = ("StarsSpent", null),
        ["MINI_REGENT"] = ("_usedThisTurn", null),
        ["PAELS_TEARS"] = ("_hadLeftoverEnergy", null),
        ["ART_OF_WAR"] = ("_anyAttacksPlayedThisTurn", "_anyAttacksPlayedLastTurn"),
        ["POCKETWATCH"] = ("_cardsPlayedThisTurn", "_cardsPlayedLastTurn"),
        ["PAELS_EYE"] = ("_usedThisCombat", "_wasOwnerPartOfLastPlayerTurn"),
        ["RUINED_HELMET"] = ("_usedThisCombat", null),
        ["UNSETTLING_LAMP"] = ("_isFinishedTriggering", null),
        ["ORICHALCUM"] = (null, null),
        ["FAKE_ORICHALCUM"] = (null, null),
    };
    static void ImportRelics(ref Battle battle, LiveState live, Rules rules, LiveHistory history)
    {
        var relics = live.Relics().Where(relic => Registry.TryRelic(Id(relic), out _)).ToArray();
        Require(relics.Length <= Limits.RelicRoster, $"遺物數 {relics.Length}");
        var rows = new Relic[relics.Length];
        for (int index = 0; index < relics.Length; index++)
        {
            ClrObject relic = relics[index];
            string relicId = Id(relic);
            Require(Registry.TryRelic(relicId, out ushort id), $"未支援的遺物 {relicId}");
            rows[index] = new Relic { Id = id, Flags = Get<bool>(relic, "_isMelted") ? RelicFlags.Melted : RelicFlags.None };
            switch (relicId)
            {
                case "BOOMING_CONCH" or "TORMENTOUS_COLLAR":
                    // RoomType == Elite; elite encounter ids end in _ELITE.
                    ClrObject encounter = Obj(live.Combat, "_encounter");
                    rows[index].Aux = !encounter.IsNull && Id(encounter).EndsWith("_ELITE", StringComparison.Ordinal) ? 1 : 0;
                    break;
                case "PHILOSOPHERS_STONE": rows[index].Aux = Var(relic, "StrengthPower"); break;
                case "MUSIC_BOX": if (Get<bool>(relic, "_wasUsedThisTurn")) rows[index].Counter = 1; break;
                case "VELVET_CHOKER": rows[index].Counter = history.CardPlayStartsThisTurn; break;
                case "EMOTION_CHIP":
                    (bool last, bool current) = history.EmotionChipDamage();
                    (rows[index].Counter, rows[index].Aux) = (last ? 1 : 0, current ? 1 : 0);
                    break;
                case "PAELS_LEGION":
                    ClrObject play = Obj(relic, "_affectedCardPlay");
                    Require(play.IsNull || history.WasCardPlayFinished(play.Address), "PaelsLegion 記錄的出牌尚未完成");
                    rows[index].Counter = Get<int>(relic, "_cooldown");
                    if (Get<bool>(relic, "_triggeredBlockLastTurn")) rows[index].Flags |= RelicFlags.UsedThisCombat;
                    break;
            }
            if (RelicFields.TryGetValue(relicId, out var fields))
            {
                if (fields.Counter != null) rows[index].Counter = IntOrBool(relic, fields.Counter);
                if (fields.Aux != null) rows[index].Aux = IntOrBool(relic, fields.Aux);
                if (relicId == "RUINED_HELMET")
                {
                    if (rows[index].Counter != 0) rows[index].Flags |= RelicFlags.UsedThisCombat;
                    rows[index].Counter = 0;
                }
            }
        }
        rules.Relics.Import(rows, ref battle);
    }

    static int IntOrBool(ClrObject obj, string name)
    {
        for (ClrType? t = obj.Type; t != null; t = t.BaseType)
            foreach (ClrInstanceField f in t.Fields)
                if (f.Name == name || f.Name == Backing(name) || f.Name == "_" + char.ToLowerInvariant(name[0]) + name[1..])
                    return f.ElementType switch
                    {
                        ClrElementType.Boolean => obj.ReadField<bool>(f.Name) ? 1 : 0,
                        ClrElementType.Int32 => obj.ReadField<int>(f.Name),
                        ClrElementType.Struct when f.Type?.Name == "System.Decimal" => (int)obj.ReadField<decimal>(f.Name),
                        _ => throw new LiveUnsupportedException($"{TypeName(obj)}.{name} 型別不支援"),
                    };
        throw new LiveUnsupportedException($"{TypeName(obj)}.{name} 欄位不存在");
    }

    static void ImportPotions(ref Battle battle, LiveState live)
    {
        var slots = List(Obj(live.Player, "_potionSlots"), Limits.PotionSlots, allowNull: true);
        battle.PotionSlotCount = (byte)slots.Count;
        for (int slot = 0; slot < slots.Count; slot++)
        {
            if (slots[slot].IsNull) continue;
            string potionId = Id(slots[slot]);
            Require(Registry.TryPotion(potionId, out ushort id), $"未支援的藥水 {potionId}");
            battle.Potions[slot] = (ushort)(id + 1);
        }
    }

    static void ImportOrbs(ref Battle battle, LiveState live)
    {
        ClrObject queue = Obj(live.PlayerState, "OrbQueue");
        int capacity = Get<int>(queue, "Capacity");
        Require(capacity >= 0 && capacity <= Limits.Orbs, $"球槽 {capacity}");
        List<ClrObject> orbs = List(Obj(queue, "_orbs"), Limits.Orbs + 1);
        Require(orbs.Count <= capacity, $"球數 {orbs.Count} 超過容量 {capacity}");
        battle.OrbCapacity = (byte)capacity;
        battle.OrbCount = (byte)orbs.Count;
        for (int index = 0; index < orbs.Count; index++)
        {
            string orbId = Id(orbs[index]);
            Require(Registry.TryOrb(orbId, out ushort id), $"未支援的充能球 {orbId}");
            // NativeAccess.OrbPrivateValue: Dark keeps its growing evoke value, Glass its decaying passive value.
            string? field = orbId switch { "DARK_ORB" => "_evokeVal", "GLASS_ORB" => "_passiveVal", _ => null };
            decimal stored = field == null ? 0m : Get<decimal>(orbs[index], field);
            Require(stored == decimal.Truncate(stored), $"{orbId} 私有值 {stored}");
            battle.Orbs[index] = new Orb { Id = checked((byte)id), Aux = (int)stored };
        }
    }
}
