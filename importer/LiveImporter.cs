using Microsoft.Diagnostics.Runtime;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.LiveImport;

public sealed record LiveImportResult(Battle Battle, Rules Rules, IReadOnlyList<string> Notes);

public static class LiveImporter
{
    /// <summary>Imports the current play-phase root, or throws <see cref="LiveUnsupportedException"/>.</summary>
    public static LiveImportResult Import(ClrRuntime runtime, RulesCatalog catalog)
    {
        var notes = new List<string>();
        LiveState live = LiveState.Capture(runtime);
        Rules rules;
        Battle battle;
        try
        {
            rules = LiveRules.Build(live, catalog, notes);
            battle = LiveBattle.Import(live, rules);
            BattleCodec.Validate(battle);
        }
        catch (UnsupportedException error) { throw new LiveUnsupportedException(error.Message); }
        catch (CapacityException error) { throw new LiveUnsupportedException(error.Message); }
        return new LiveImportResult(battle, rules, notes);
    }

    /// <summary>Human-readable dump plus the kernel's prediction for ending the turn now.</summary>
    public static object Describe(LiveImportResult result)
    {
        Battle battle = result.Battle;
        var legal = new PlayerAction[512];
        int legalCount = Kernel.LegalActions(in battle, result.Rules, legal);
        Battle after = battle;
        string endTurn;
        try
        {
            StepResult step = Kernel.Step(ref after, PlayerAction.EndTurn(), result.Rules, out _);
            endTurn = step.ToString();
        }
        catch (UnsupportedException error) { endTurn = "unsupported: " + error.Message; }
        return new
        {
            notes = result.Notes,
            legalActions = legal.Take(legalCount).Select(a => a.ToString()).ToArray(),
            battle = BattleDump.Lines(in battle),
            afterEndTurn = new { result = endTurn, battle = BattleDump.Lines(in after) },
        };
    }
}
