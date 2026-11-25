using Microsoft.Diagnostics.Runtime;
using static Sts2Solver.LiveImport.Clr;

namespace Sts2Solver.LiveImport;

/// <summary>The native objects of one single-player play-phase root, located in a ClrMD heap.</summary>
internal sealed class LiveState
{
    public required ClrObject Combat { get; init; }      // CombatState
    public required ClrObject Run { get; init; }         // RunState
    public required ClrObject Player { get; init; }      // Player
    public required ClrObject Creature { get; init; }    // player Creature
    public required ClrObject PlayerState { get; init; } // PlayerCombatState
    public required List<ClrObject> Enemies { get; init; }
    public required ClrObject History { get; init; }     // CombatHistory
    public required int Round { get; init; }
    public required int Side { get; init; }
    public required int TurnNumber { get; init; }
    public required ulong PlayerNetId { get; init; }
    public required string Character { get; init; }
    /// <summary>Other players share this fight: the search sees only this player (approximate).</summary>
    public bool Partnered { get; init; }

    public List<ClrObject> Pets => List(Obj(PlayerState, "_pets"), 16);
    /// <summary>Player.Osty / the Wuppy pet: the player's pet whose monster model is that native type (may be dead).</summary>
    public ClrObject Pet(string monsterType) =>
        Pets.FirstOrDefault(p => Obj(p, "Monster").Type?.Name?.Split('.').Last() == monsterType); // ClrMD type names are namespace-qualified

    public static LiveState Capture(ClrRuntime runtime)
    {
        ClrObject manager = Single(runtime, "MegaCrit.Sts2.Core.Combat.CombatManager");
        ClrObject turn = Obj(manager, "_turnState");
        Require(!turn.IsNull && Get<bool>(turn, "IsInProgress"), "請進入一場戰鬥。");
        Require(!Get<bool>(manager, "_playerActionsDisabled") && !Get<bool>(turn, "IsStarting")
            && !Get<bool>(turn, "IsEnemyTurnStarted") && !Get<bool>(turn, "EndingPlayerTurnPhaseOne")
            && !Get<bool>(turn, "EndingPlayerTurnPhaseTwo"), "戰鬥正在結算。請等到可出牌時擷取。");
        ClrObject combat = Obj(turn, "State");
        ClrObject run = Obj(combat, "RunState");
        Require(Is(run, "MegaCrit.Sts2.Core.Runs.RunState"), "目前不是一般的跑局。");
        var players = List(Obj(combat, "_allies")).Where(c => !Obj(c, "Player").IsNull).ToList();
        Require(players.Count is >= 1 and <= 4, $"玩家數 {players.Count}");
        // Multiplayer: the upstream kernel simulates one player, so import the local one alone.
        // Over Steam a NetId is the SteamID64; Steam records the signed-in account id in the registry.
        ClrObject creature = players[0];
        if (players.Count > 1)
        {
            object? account = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
            Require(account is int id && id != 0, "讀不到目前登入的 Steam 帳號。");
            ulong me = 76561197960265728UL + (uint)(int)account!;
            creature = players.FirstOrDefault(c => Get<ulong>(Obj(c, "Player"), "NetId") == me);
            Require(!creature.IsNull, "多人遊戲中找不到本機玩家（目前只支援 Steam 連線）。");
        }
        ClrObject player = Obj(creature, "Player");
        ClrObject state = Obj(player, "PlayerCombatState");
        Require(!state.IsNull, "玩家沒有戰鬥狀態。");
        Require(Get<int>(combat, "CurrentSide") == 1, "請在玩家回合擷取。");
        Require(Get<int>(state, "_phase") == 3, "請等到出牌階段再擷取。"); // PlayerTurnPhase.Play
        Require(Get<int>(creature, "_currentHp") > 0, "玩家已死亡。");
        Require(List(Obj(combat, "_escapedCreatures")).Count == 0, "有敵人已逃離，暫不支援。");
        ClrObject character = Obj(player, "Character");
        return new LiveState
        {
            Combat = combat,
            Run = run,
            Player = player,
            Creature = creature,
            PlayerState = state,
            Enemies = List(Obj(combat, "_enemies"), 32),
            History = Obj(manager, "History"),
            Round = Get<int>(combat, "RoundNumber"),
            Side = Get<int>(combat, "CurrentSide"),
            TurnNumber = Get<int>(state, "TurnNumber"),
            PlayerNetId = Get<ulong>(player, "NetId"),
            Character = Id(character),
            Partnered = players.Count > 1,
        };
    }

    static ClrObject Single(ClrRuntime runtime, string type)
    {
        var found = runtime.Heap.EnumerateObjects().Where(o => o.Type?.Name == type).Take(2).ToArray();
        Require(found.Length == 1, "尚未取得目前的遊戲狀態。");
        return found[0];
    }

    public IEnumerable<ClrObject> Piles()
    {
        yield return Obj(PlayerState, "Hand");
        yield return Obj(PlayerState, "DrawPile");
        yield return Obj(PlayerState, "DiscardPile");
        yield return Obj(PlayerState, "ExhaustPile");
        yield return Obj(PlayerState, "PlayPile");
    }

    public static List<ClrObject> Cards(ClrObject pile) => List(Obj(pile, "_cards"), 512);

    public static List<ClrObject> Powers(ClrObject creature) => List(Obj(creature, "_powers"), 256);

    public List<ClrObject> Relics() => List(Obj(Player, "_relics"), 256);
}
