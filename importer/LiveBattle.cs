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

    public static Battle Import(LiveState live, Rules rules)
    {
        var history = new LiveHistory(live, rules);
        var ownerOf = new Dictionary<ulong, byte> { [live.Creature.Address] = Limits.PlayerOwner };
        for (int i = 0; i < live.Enemies.Count; i++) ownerOf[live.Enemies[i].Address] = (byte)(Limits.FirstEnemyOwner + i);
        Require(List(Obj(live.PlayerState, "_pets")).Count == 0, "召喚物（Osty／寵物）暫不支援");
        Require(List(Obj(live.PlayerState, "_arrows")).Count == 0 && !rules.HasFoolMechanics, "愚者機制暫不支援");
        Require(List(Obj(live.Combat, "_allies")).Count == 1, "場上有其他友方生物，暫不支援");

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

        ImportCounters(ref battle, history);
        ImportRng(ref battle, live);
        ImportEnemies(ref battle, live, rules, history);
        ImportPowers(ref battle, live, ownerOf);
        ImportCards(ref battle, live, rules, history);
        ImportRelics(ref battle, live, rules);
        ImportPotions(ref battle, live);
        ImportOrbs(ref battle, live);
        Require(!rules.HasDecimalCardVars, "小數傷害變數的卡牌暫不支援");
        Require(!rules.HasHistoryCourse, "歷史課遺物暫不支援");
        return battle;
    }

    static Sts2Solver.Simulation.Combat.Creature Vitals(ClrObject creature, LiveHistory history) => new()
    {
        CurrentHp = Get<int>(creature, "_currentHp"),
        MaxHp = Get<int>(creature, "_maxHp"),
        Block = Get<int>(creature, "_block"),
        Flags = CreatureFlags.Present | (history.HasLostHpThisTurn(creature.Address) ? CreatureFlags.LostHpThisTurn : CreatureFlags.None),
    };

    static void ImportCounters(ref Battle battle, LiveHistory h)
    {
        battle.SetCounter(Counter.DampenCasterMask, 0);
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
        battle.SetCounter(Counter.OstyAttacksThisTurn, 0);
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
            Require(Get<int>(creature, "_currentHp") > 0, $"{monsterId} 已死亡但仍在名單中，暫不支援");
            enemy.DeathPhase = DeathPhase.None;

            ClrObject next = Obj(monster, "NextMove");
            string current = Str(next, "StateId") ?? throw new LiveUnsupportedException($"{monsterId} 沒有當前行動");
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
                Require(LiveRules.TryMonsterInt(monster, def.AiRegisterNames[r], out enemy.Ai[r]),
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
    static readonly HashSet<string> ProjectedUnsupported = ["MYTHIC_FORM_POWER", "ENCORE_POWER", "ENNUI_POWER", "YAH_POWER",
        "JUGGLING_POWER", "DARK_EMBRACE_POWER", "CRIMSON_MANTLE_POWER", "INFERNO_POWER", "SLOW_POWER", "VOID_FORM_POWER",
        "HARDENED_SHELL_POWER", "THIEVERY_POWER", "HELLRAISER_POWER", "FERAL_POWER", "CHAINS_OF_BINDING_POWER",
        "WITHERING_PRESENCE_POWER", "POSSESS_STRENGTH_POWER", "POSSESS_SPEED_POWER", "NEMESIS_POWER", "TENDER_POWER",
        "AUTOMATION_POWER", "PANACHE_POWER", "THE_BOMB_POWER", "TORIC_TOUGHNESS_POWER", "MONOLOGUE_POWER", "ORBIT_POWER",
        "PALE_BLUE_DOT_POWER", "NIGHTMARE_POWER", "SOULBOUND_POWER", "CACOPHONY_POWER", "SWIPE_POWER", "DAMPEN_POWER",
        "HEIST_POWER", "SURPRISE_POWER"];

    static void ImportPowers(ref Battle battle, LiveState live, Dictionary<ulong, byte> ownerOf)
    {
        var ordered = new List<(byte Owner, ClrObject Power)>();
        foreach (ClrObject power in LiveState.Powers(live.Creature))
            if (Get<int>(power, "_amount") != 0) ordered.Add((Limits.PlayerOwner, power));
        for (int i = 0; i < live.Enemies.Count; i++)
            foreach (ClrObject power in LiveState.Powers(live.Enemies[i]))
                if (Get<int>(power, "_amount") != 0) ordered.Add(((byte)(Limits.FirstEnemyOwner + i), power));
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
            switch (powerId)
            {
                case "LULLABY_POWER": battle.Powers[index].Aux0 = Get<bool>(power, "_proccdThisTurn") ? 1 : 0; break;
                case "SLOTH_POWER": battle.Powers[index].Aux0 = Get<int>(power, "_cardsPlayedThisTurn"); break;
                case "SKITTISH_POWER": battle.Powers[index].Aux0 = Get<bool>(power, "HasGainedBlockThisTurn") ? 1 : 0; break;
                case "SURROUNDED_POWER": battle.Powers[index].Aux0 = Get<int>(power, "Facing"); break;
                case "CURL_UP_POWER": battle.Powers[index].Aux0 = 0; battle.Powers[index].Aux1 = 0; break;
            }
        }
    }

    static byte OwnerOf(ClrObject creature, Dictionary<ulong, byte> ownerOf)
    {
        if (creature.IsNull) return Limits.NoOwner;
        return ownerOf.TryGetValue(creature.Address, out byte owner) ? owner : throw new LiveUnsupportedException("能力關聯到未知的生物");
    }

    static readonly HashSet<string> ReturnsToHandCards = ["BOLAS", "THRUMMING_HATCHET", "BOUNCE"];

    static void ImportCards(ref Battle battle, LiveState live, Rules rules, LiveHistory history)
    {
        Pile[] targets = [Pile.Hand, Pile.Draw, Pile.Discard, Pile.Exhaust, Pile.Play];
        int p = 0;
        foreach (ClrObject pile in live.Piles())
        {
            Pile target = targets[p++];
            foreach (ClrObject card in LiveState.Cards(pile))
            {
                Card value = ImportCard(card, rules);
                string id = Id(card);
                Require(id is not ("SOVEREIGN_BLADE" or "THE_SHOT" or "MAD_SCIENCE"), $"{id} 的內部狀態尚未移植");
                if (id == "FETCH" && history.FinishedThisTurnContains(card.Address)) value.Private |= FetchPlayedBit;
                if (ReturnsToHandCards.Contains(id) && history.FinishedThisTurnContains(card.Address)) value.ReturnToHandNextTurn = true;
                Piles.Append(ref battle, target, value);
            }
        }
    }

    static Card ImportCard(ClrObject card, Rules rules)
    {
        string nativeId = Id(card);
        Require(Registry.TryCard(nativeId, out ushort id), $"未支援的卡牌 {nativeId}");
        int upgrade = Get<int>(card, "_currentUpgradeLevel");
        CardStats stats = rules.Card(id, upgrade);
        CardDef def = Registry.Card(id);
        ClrObject cost = Obj(card, "_energyCost");
        bool costsX = Get<bool>(cost, "CostsX");
        Require(Obj(card, "Enchantment").IsNull, $"{nativeId} 帶有附魔，暫不支援");
        Require(Obj(card, "Affliction").IsNull, $"{nativeId} 帶有苦難，暫不支援");
        Require(List(Obj(card, "_temporaryStarCosts")).Count == 0, $"{nativeId} 有臨時星能費用，暫不支援");
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
        foreach (ClrObject modifier in List(Obj(cost, "_localModifiers"), 64))
            CardCostModifiers.Append(ref result, CostModifier.Create(Get<int>(modifier, "Amount"),
                (CostType)(byte)Get<int>(modifier, "Type"), (CostExpiration)(byte)Get<int>(modifier, "Expiration"),
                Get<bool>(modifier, "IsReduceOnly")));

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
        ["PENDULUM"] = ("TurnsSeen", null),
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
    static readonly HashSet<string> RelicUnsupported = ["BOOMING_CONCH", "TORMENTOUS_COLLAR", "PHILOSOPHERS_STONE",
        "MUSIC_BOX", "PAELS_LEGION", "EMOTION_CHIP", "VELVET_CHOKER"];

    static void ImportRelics(ref Battle battle, LiveState live, Rules rules)
    {
        var relics = live.Relics();
        Require(relics.Count <= Limits.RelicRoster, $"遺物數 {relics.Count}");
        var rows = new Relic[relics.Count];
        for (int index = 0; index < relics.Count; index++)
        {
            ClrObject relic = relics[index];
            string relicId = Id(relic);
            Require(Registry.TryRelic(relicId, out ushort id), $"未支援的遺物 {relicId}");
            Require(!RelicUnsupported.Contains(relicId), $"遺物 {relicId} 的內部狀態尚未移植");
            rows[index] = new Relic { Id = id, Flags = Get<bool>(relic, "_isMelted") ? RelicFlags.Melted : RelicFlags.None };
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
                if (f.Name == name || f.Name == Backing(name))
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
        Require(List(Obj(queue, "_orbs")).Count == 0, "充能球暫不支援");
        battle.OrbCapacity = (byte)capacity;
        battle.OrbCount = 0;
    }
}
