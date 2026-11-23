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

public sealed record LiveSearchStep(int Turn, string Kind, string Card, string? Target, int Upgrade)
{
    /// <summary>Cards selected by the card's choice (e.g. what Brand exhausts), or null.</summary>
    public string[]? Choices { get; init; }
    public string? Potion { get; init; }
    public int? PotionSlot { get; init; }
}

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
        int woundedHeal = 0, woundedPercent = 0;
        foreach (ClrObject relic in live.Relics())
            if (!Get<bool>(relic, "_isMelted") && Id(relic) == "MEAT_ON_THE_BONE")
            {
                woundedHeal += LiveVar(relic, "Heal");
                woundedPercent = Math.Max(woundedPercent, LiveVar(relic, "HpThreshold"));
            }
        var healProfile = new PostCombatRelicHealProfile(heal, woundedHeal, woundedPercent);

        ScoreRules Build(int persistent, int suppression, int weak, int retained)
        {
            ScoreRules copy = (ScoreRules)typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(source, null)!;
            Set(copy, nameof(ScoreRules.GrowthBudgets), default(GrowthValues));
            Set(copy, nameof(ScoreRules.RequiredGrowthRewards), default(GrowthValues));
            Set(copy, nameof(ScoreRules.PostCombatRelicHeal), healProfile);
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
            // The final boss ends the run: a potion kept has no later fight to serve, so its strategic HP cost is 0.
            if (act3Boss) Set(copy, nameof(ScoreRules.PotionStrategicCost), new int[source.PotionStrategicCost.Length]);
            return copy.WithRelicCounterTargets([]).WithRules(rules);
        }
        ScoreFeatures baseline = new Scorer(Build(0, 0, 0, 0)).Features(root, 0);
        var scorer = new Scorer(Build(baseline.PersistentBuffValue, baseline.EnemyStrengthSuppression,
            baseline.EnemyWeakTurns, baseline.RetainedAttackValue));

        bool hasSearchablePotion = false;
        bool hasPotion = false;
        for (int slot = 0; slot < root.PotionSlotCount; slot++)
            if (root.Potions[slot] != 0)
            {
                hasPotion = true;
                if (Registry.Potion((ushort)(root.Potions[slot] - 1)).Searchable)
                    hasSearchablePotion = true;
            }
        if (!hasSearchablePotion)
            hasSearchablePotion = rules.PotionPool.Any(id => Registry.Potion(id).Searchable)
                || rules.OutOfCombatPotionPool.Any(id => Registry.Potion(id).Searchable);

        BeamOptions options = template.Options with
        {
            Scorer = scorer,
            Bound = null,
            BeamWidth = 300,
            MaxExpandedNodes = 2_000_000,
            MaxTurns = 64,
            SoftTimeBudget = budget,
            SearchThreads = Math.Max(1, Environment.ProcessorCount - 1),
            Ordering = new RouteOrdering(root.Player.CurrentHp, relief, healProfile),
            InitialPlayerTurn = root.PlayerTurn,
            Potions = hasSearchablePotion
                ? new PotionPolicy(new PotionStrategySnapshot(PotionUseMode.Smart, []), false)
                : PotionPolicy.Disabled,
            SearchMemoryBudgetBytes = 3L << 30,
            StatePoolBudgetBytes = 1L << 30,
            Act3BossStrategy = act3Boss,
        };
        BeamResult result = new BeamSolver(rules, options).Solve([root], cancel);
        if (!hasSearchablePotion && hasPotion)
            notes.Add("目前持有的藥水尚無可搜尋效果；按無藥水路線求解");

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
                    if (Registry.Card(card.Id).GeneratedCardPools.Count > 0)
                        notes.Add("路線有隨機生成卡牌。打出生成牌的來源後，請等新快照更新再執行後續步驟。");
                    string? target = action.Target == Limits.NoOwner ? null
                        : action.Target >= Limits.FirstEnemyOwner && action.Target < Limits.FirstEnemyOwner + state.EnemyCount
                            ? $"{Registry.Monster(state.Enemies[action.Target - Limits.FirstEnemyOwner].MonsterId).NativeId}#{action.Target - Limits.FirstEnemyOwner + 1}"
                            : null;
                    steps.Add(new LiveSearchStep(turn, "play", Registry.Card(card.Id).NativeId, target, card.Upgrade)
                        { Choices = DescribeChoice(state, action, rules) });
                    break;
                case PlayerActionKind.EndTurn:
                    steps.Add(new LiveSearchStep(turn, "end", "", null, 0));
                    turn++;
                    break;
                case PlayerActionKind.UsePotion:
                    ushort stored = state.Potions[action.PotionSlot];
                    string potion = Registry.Potion((ushort)(stored - 1)).NativeId;
                    string? potionTarget = action.Target >= Limits.FirstEnemyOwner
                        && action.Target < Limits.FirstEnemyOwner + state.EnemyCount
                        ? $"{Registry.Monster(state.Enemies[action.Target - Limits.FirstEnemyOwner].MonsterId).NativeId}#{action.Target - Limits.FirstEnemyOwner + 1}"
                        : null;
                    steps.Add(new LiveSearchStep(turn, "potion", "", potionTarget, 0)
                        { Potion = potion, PotionSlot = action.PotionSlot });
                    if (action.ChoiceCount > 0)
                        notes.Add("藥水產生了選項；使用後請更新快照再執行後續步驟。");
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

    /// <summary>
    /// Names the cards picked by the action's first choice: replays the action without its
    /// selections to get the kernel's ChoiceRequest, whose options are card rows; the selection is a
    /// bit set over them. Generated or index-encoded options are reported as unnamed.
    /// </summary>
    static string[]? DescribeChoice(Battle state, PlayerAction action, Rules rules)
    {
        if (action.ChoiceCount == 0) return null;
        Battle probe = state;
        PlayerAction bare = PlayerAction.PlayCard(action.HandIndex, action.Target);
        Kernel.Step(ref probe, bare, rules, out ChoiceRequest request);
        if (request.OptionCount == 0 || request.OptionsAreGenerated || request.IndexSelection) return ["（未命名的選項）"];
        ulong selection = action.Choices[0];
        var picked = new List<string>();
        for (int option = 0; option < request.OptionCount && option < 64; option++)
            if ((selection & (1UL << option)) != 0)
            {
                Card card = probe.Cards[request.Options[option]];
                picked.Add(Registry.Card(card.Id).NativeId + (card.Upgrade > 0 ? "+" : ""));
            }
        return [.. picked];
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
