// Localhost executor: GET /state, POST /play?card=ID&up=N&target=K, /potion?slot=N&target=K, /end.
// Target K is the 1-based index into CombatState.Enemies (the reader's "#K"). Execution follows the
// enqueue-and-capture pattern of upstream Sts2Solver ActionDeployer (adapted from CombatSolver
// SolverController at a62f1850, MIT, Copyright (c) 2026 Torch1230 and contributors).
using System.Net;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;
using NativeCreature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;

namespace Sts2Executor;

[ModInitializer("Init")]
public static partial class ExecutorMod
{
    const string Prefix = "http://127.0.0.1:8766/";
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "sts2executor.log");
    static void Log(string line) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + line + "\n"); } catch { } }

    public static void Init()
    {
        Log("Init called");
        try
        {
            // The game keeps a separate save profile while any mod is loaded. This mod changes no rules, so make the
            // game report "not modded" and keep using the original profile (the check runs right after mod loading).
            new HarmonyLib.Harmony("Sts2Executor").Patch(
                HarmonyLib.AccessTools.Method(typeof(ModManager), nameof(ModManager.IsRunningModded)),
                postfix: new HarmonyLib.HarmonyMethod(typeof(ExecutorMod), nameof(NotModded)));
        }
        catch (Exception error) { Log("could not patch IsRunningModded: " + error); }
        new Thread(Serve) { IsBackground = true, Name = "Sts2Executor" }.Start();
    }

    public static void NotModded(ref bool __result) => __result = false;

    static void Serve()
    {
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(Prefix);
            listener.Start();
            Log("listening on " + Prefix);
            while (true)
            {
                HttpListenerContext ctx = listener.GetContext();
                _ = Task.Run(() => Handle(ctx));
            }
        }
        catch (Exception error) { Log("server stopped: " + error); }
    }

    static async Task Handle(HttpListenerContext ctx)
    {
        object result;
        try
        {
            // A header a browser page cannot send cross-origin keeps other sites from driving the game.
            if (ctx.Request.Headers["X-STS2-Exec"] != "1") result = new { ok = false, error = "missing X-STS2-Exec header" };
            else
            {
                var q = ctx.Request.QueryString;
                result = ctx.Request.Url!.AbsolutePath switch
                {
                    "/state" => await OnMain(State),
                    "/play" => await Play(q["card"] ?? "", int.Parse(q["up"] ?? "0"), q["target"] is { } t ? int.Parse(t) : 0),
                    "/potion" => await Potion(int.Parse(q["slot"] ?? "0"), q["target"] is { } p ? int.Parse(p) : 0),
                    "/end" => await EndTurn(),
                    "/ui" => await OnMain(Ui),
                    "/click" => await OnMain(() => Click(int.Parse(q["i"] ?? "-1"))),
                    _ => new { ok = false, error = "unknown path" },
                };
            }
        }
        catch (Exception error) { result = new { ok = false, error = error.GetBaseException().Message }; }
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(result);
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(body);
        ctx.Response.Close();
    }

    static Task<T> OnMain<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Callable.From(() => { try { tcs.SetResult(work()); } catch (Exception e) { tcs.SetException(e); } }).CallDeferred();
        return tcs.Task;
    }

    static (CombatState combat, Player me) Live()
    {
        CombatState combat = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("not in combat");
        Player me = LocalContext.GetMe(combat) ?? throw new InvalidOperationException("no local player");
        return (combat, me);
    }

    static object State()
    {
        (CombatState combat, Player me) = Live();
        if (!CombatManager.Instance.IsInProgress) return new { ok = false, error = "combat finished" };
        var pcs = me.PlayerCombatState!;
        return new
        {
            ok = true,
            turn = pcs.TurnNumber,
            energy = pcs.Energy,
            hand = pcs.Hand.Cards.Select(c => new { id = c.Id.Entry, up = c.CurrentUpgradeLevel }).ToArray(),
            enemies = combat.Enemies.Select(e => new { alive = e.IsAlive, hp = e.CurrentHp }).ToArray(),
        };
    }

    static NativeCreature? Enemy(CombatState combat, int oneBased)
    {
        if (oneBased <= 0) return null;
        if (oneBased > combat.Enemies.Count) throw new InvalidOperationException($"enemy #{oneBased} does not exist");
        NativeCreature enemy = combat.Enemies[oneBased - 1];
        return enemy.IsAlive ? enemy : throw new InvalidOperationException($"enemy #{oneBased} is dead");
    }

    /// <summary>Subscribes, enqueues on the main thread, then waits for the game to run and finish the action.</summary>
    static async Task<object> Execute(Func<GameAction, bool> matches, Action enqueue)
    {
        var captured = new TaskCompletionSource<GameAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        ActionExecutor executor = RunManager.Instance.ActionExecutor;
        void Handler(GameAction a) { if (matches(a)) captured.TrySetResult(a); }
        await OnMain(() => { executor.BeforeActionExecuted += Handler; enqueue(); return 0; });
        try
        {
            GameAction action = await captured.Task.WaitAsync(Wait);
            Task done = action.CompletionTask;
            if (await Task.WhenAny(done, Task.Delay(Wait)) != done) return new { ok = true, pending = true }; // waiting for a choice in the UI
            await done;
            if (action.Exception != null) return new { ok = false, error = action.Exception.GetBaseException().Message };
            return new { ok = true };
        }
        catch (TimeoutException) { return new { ok = false, error = "action did not enter the game queue" }; }
        finally { await OnMain(() => { executor.BeforeActionExecuted -= Handler; return 0; }); }
    }

    static async Task<object> Play(string id, int up, int target)
    {
        CardModel? card = null; NativeCreature? enemy = null; Player? player = null;
        await OnMain(() =>
        {
            (CombatState combat, Player me) = Live();
            player = me;
            enemy = Enemy(combat, target);
            card = me.PlayerCombatState!.Hand.Cards.FirstOrDefault(c => c.Id.Entry == id && c.CurrentUpgradeLevel == up && c.CanPlayTargeting(enemy));
            return 0;
        });
        if (card == null) return new { ok = false, error = $"no playable {id}+{up} in hand for target {target}" };
        return await Execute(a => a is PlayCardAction p && ReferenceEquals(p.Player, player) && ReferenceEquals(p.NetCombatCard.ToCardModelOrNull(), card),
            () => { if (!card.TryManualPlay(enemy)) throw new InvalidOperationException("card could not be played"); });
    }

    static async Task<object> Potion(int slot, int target)
    {
        PotionModel? potion = null; NativeCreature? enemy = null; Player? player = null;
        await OnMain(() =>
        {
            (CombatState combat, Player me) = Live();
            player = me;
            potion = me.GetPotionAtSlotIndex(slot) ?? throw new InvalidOperationException($"potion slot {slot} is empty");
            enemy = Enemy(combat, target);
            if (enemy == null && potion.IsValidTarget(me.Creature)) enemy = me.Creature; // untargeted self potion
            if (!potion.IsValidTarget(enemy)) throw new InvalidOperationException("invalid potion target");
            return 0;
        });
        return await Execute(a => a is UsePotionAction u && ReferenceEquals(u.Player, player) && u.PotionIndex == (uint)slot,
            () => potion!.EnqueueManualUse(enemy));
    }

    static async Task<object> EndTurn()
    {
        Player? player = null; int turn = 0;
        await OnMain(() => { (_, player) = Live(); turn = player.PlayerCombatState!.TurnNumber; return 0; });
        return await Execute(a => a is EndPlayerTurnAction e && e.OwnerId == player!.NetId,
            () =>
            {
                CombatManager.Instance.OnEndedTurnLocally();
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player!, turn));
            });
    }
}
