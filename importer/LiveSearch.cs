// Runs rank-Yu/sts2-solver's BeamSolver (MIT, commit f0167be) on a live import. The per-content score
// tables (CardScore/PowerScore/potion costs) come from an upstream frozen request (config.br in its
// Golden data); the root-frozen fields follow upstream src/Import/Combat/ScoreCompiler.cs and are
// recomputed from the live root. The pruning bound is left off: it only speeds the search up.
using System.IO.Compression;
using System.Reflection;
using Microsoft.Diagnostics.Runtime;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Simulation.Combat;
using static Sts2Solver.LiveImport.Clr;

namespace Sts2Solver.LiveImport;

public sealed class ScoreTemplates(string normalPath, string act3BossPath)
{
    readonly Lazy<object> _normal = new(() => Load(normalPath));
    readonly Lazy<object> _act3 = new(() => Load(act3BossPath));
    internal SearchCase Normal => (SearchCase)_normal.Value;
    internal SearchCase Act3Boss => (SearchCase)_act3.Value;

    static object Load(string path)
    {
        using var file = File.OpenRead(path);
        using var brotli = new BrotliStream(file, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        brotli.CopyTo(memory);
        return SearchCodec.DecodeRequest(memory.ToArray());
    }
}

public sealed record LiveSearchStep(int Turn, string Kind, string Card, string? Target, int Upgrade);

public sealed record LiveSearchResult(bool Victory, double Score, int ExpandedNodes, int Turns, double Seconds,
    string StopReason, IReadOnlyList<LiveSearchStep> Route, int FinalHp, int MaxHp, IReadOnlyList<string> Notes);

internal static class LiveSearch
{
    public static LiveSearchResult Solve(ClrRuntime runtime, LiveImportResult import, ScoreTemplates templates,
        TimeSpan budget, CancellationToken cancel)
    {
        LiveState live = LiveState.Capture(runtime);
        var notes = new List<string>(import.Notes);
        Rules rules = import.Rules;
        Battle root = import.Battle;

        ClrObject encounter = Obj(live.Combat, "_encounter");
        string encounterId = encounter.IsNull ? "" : Id(encounter);
        int act = Get<int>(live.Run, "_currentActIndex");
        bool act3Boss = act == 2 && encounterId is "TEST_SUBJECT_BOSS" or "AEONGLASS_BOSS" or "QUEEN_BOSS";
        // ActEndingBossPolicy.ResolveHpRelief with the default ProgressionFirst strategies. The
        // double-boss final act (second boss differs) is not distinguished here.
        BossHpRelief relief = !encounterId.EndsWith("_BOSS", StringComparison.Ordinal) ? BossHpRelief.None
            : act < 2 ? BossHpRelief.ActClearHeal : BossHpRelief.RunEnding;
        if (relief == BossHpRelief.RunEnding) notes.Add("終局 Boss：只比較能否存活，不計剩餘生命");

        SearchCase template = act3Boss ? templates.Act3Boss : templates.Normal;
        ScoreRules source = template.Options.Scorer!.RulesForCodec;
        int heal = 0;
        foreach (ClrObject relic in live.Relics())
            if (!Get<bool>(relic, "_isMelted") && Id(relic) is "BURNING_BLOOD" or "BLACK_BLOOD")
                heal += LiveVar(relic, "Heal");
        Require(!live.Relics().Any(r => Id(r) == "MEAT_ON_THE_BONE"), "帶骨肉遺物的戰後回復暫不支援");

        ScoreRules Build(int persistent, int suppression, int weak, int retained)
        {
            ScoreRules copy = (ScoreRules)typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(source, null)!;
            Set(copy, nameof(ScoreRules.GrowthBudgets), default(GrowthValues));
            Set(copy, nameof(ScoreRules.RequiredGrowthRewards), default(GrowthValues));
            Set(copy, nameof(ScoreRules.PostCombatRelicHeal), new PostCombatRelicHealProfile(heal, 0, 0));
            Set(copy, nameof(ScoreRules.BossHpRelief), relief);
            Set(copy, nameof(ScoreRules.InitialPlayerMaxHp), root.Player.MaxHp);
            Set(copy, nameof(ScoreRules.IsActEndingBoss), relief != BossHpRelief.None);
            Set(copy, nameof(ScoreRules.Act3BossStrategy), act3Boss);
            Set(copy, nameof(ScoreRules.IgnoreGrowthRewards), false);
            Set(copy, nameof(ScoreRules.TheftPolicy), (object?)null);
            Set(copy, nameof(ScoreRules.GremlinGoldReward), GremlinGoldReward.NotApplicable);
            Set(copy, nameof(ScoreRules.InitialEnemyCount), (int)root.EnemyCount);
            Set(copy, nameof(ScoreRules.InitialPersistentBuffValue), persistent);
            Set(copy, nameof(ScoreRules.InitialEnemyStrengthSuppression), suppression);
            Set(copy, nameof(ScoreRules.InitialEnemyWeakTurns), weak);
            Set(copy, nameof(ScoreRules.InitialRetainedAttackValue), retained);
            ushort[] potions = new ushort[Limits.PotionSlots];
            for (int slot = 0; slot < root.PotionSlotCount; slot++) potions[slot] = root.Potions[slot];
            Set(copy, nameof(ScoreRules.RootPotions), potions);
            Set(copy, nameof(ScoreRules.RootPotionSlotCount), (int)root.PotionSlotCount);
            return copy.WithRelicCounterTargets([]).WithRules(rules);
        }
        ScoreFeatures baseline = new Scorer(Build(0, 0, 0, 0)).Features(root, 0);
        var scorer = new Scorer(Build(baseline.PersistentBuffValue, baseline.EnemyStrengthSuppression,
            baseline.EnemyWeakTurns, baseline.RetainedAttackValue));

        BeamOptions options = template.Options with
        {
            Scorer = scorer,
            Bound = null,
            BeamWidth = 300,
            MaxExpandedNodes = 2_000_000,
            MaxTurns = 64,
            SoftTimeBudget = budget,
            SearchThreads = Math.Max(1, Environment.ProcessorCount - 1),
            Ordering = new RouteOrdering(root.Player.CurrentHp, relief, new PostCombatRelicHealProfile(heal, 0, 0)),
            InitialPlayerTurn = root.PlayerTurn,
            Potions = PotionPolicy.Disabled,
            SearchMemoryBudgetBytes = 3L << 30,
            StatePoolBudgetBytes = 1L << 30,
            Act3BossStrategy = act3Boss,
        };
        BeamResult result = new BeamSolver(rules, options).Solve([root], cancel);
        notes.Add("目前搜尋不使用藥水");

        // Replay the route from the root to name each step and get the predicted final HP.
        var steps = new List<LiveSearchStep>();
        Battle state = root;
        int turn = 1;
        foreach (PlayerAction action in result.BestRoute)
        {
            switch (action.Kind)
            {
                case PlayerActionKind.PlayCard:
                    Card card = state.Cards[action.HandIndex]; // hand occupies the first rows
                    string? target = action.Target == Limits.NoOwner ? null
                        : action.Target >= Limits.FirstEnemyOwner && action.Target < Limits.FirstEnemyOwner + state.EnemyCount
                            ? $"{Registry.Monster(state.Enemies[action.Target - Limits.FirstEnemyOwner].MonsterId).NativeId}#{action.Target - Limits.FirstEnemyOwner + 1}"
                            : null;
                    steps.Add(new LiveSearchStep(turn, "play", Registry.Card(card.Id).NativeId, target, card.Upgrade));
                    break;
                case PlayerActionKind.EndTurn:
                    steps.Add(new LiveSearchStep(turn, "end", "", null, 0));
                    turn++;
                    break;
                default:
                    steps.Add(new LiveSearchStep(turn, action.Kind.ToString(), "", null, 0));
                    break;
            }
            Kernel.Step(ref state, action, rules, out _);
        }
        return new LiveSearchResult(result.BestIsVictory, result.BestScore, (int)result.ExpandedNodesTotal,
            result.TurnsSearched, result.Elapsed.TotalSeconds, result.StopReason, steps,
            state.Player.CurrentHp, state.Player.MaxHp, notes);
    }

    static void Set(ScoreRules target, string property, object? value) =>
        typeof(ScoreRules).GetProperty(property)!.SetValue(target, value);

    /// <summary>A model's live DynamicVars[name].BaseValue (ScoreCompiler.RequiredRelicInt).</summary>
    static int LiveVar(ClrObject model, string name)
    {
        ClrObject vars = Obj(model, "_dynamicVars");
        Require(!vars.IsNull, $"{Id(model)} 沒有動態變數");
        foreach (ClrValueType entry in HashEntries(Obj(vars, "_vars")))
            if (entry.ReadObjectField("key").AsString() == name)
                return (int)Get<decimal>(entry.ReadObjectField("value"), "_baseValue");
        throw new LiveUnsupportedException($"{Id(model)} 沒有變數 {name}");
    }
}
