// ClrMD port of rank-Yu/sts2-solver src/Import/Combat/NativeHistory.cs (MIT, commit f0167be):
// the same CombatManager.History counters, read from the heap instead of live objects.
using Microsoft.Diagnostics.Runtime;
using Sts2Solver.Simulation.Combat;
using static Sts2Solver.LiveImport.Clr;

namespace Sts2Solver.LiveImport;

internal sealed class LiveHistory
{
    sealed record Entry(string Kind, ClrObject Obj, bool ThisTurn, ulong Actor);

    readonly List<Entry> _entries = [];
    readonly LiveState _live;
    readonly Rules _rules;
    readonly ulong _player;

    public LiveHistory(LiveState live, Rules rules)
    {
        _live = live;
        _rules = rules;
        _player = live.Creature.Address;
        foreach (ClrObject entry in List(Obj(live.History, "_entries"), 100_000))
        {
            bool thisTurn = Get<int>(entry, "RoundNumber") == live.Round && Get<int>(entry, "CurrentSide") == live.Side;
            if (thisTurn)
                foreach (ClrValueType turn in HashEntries(Obj(entry, "_playerTurnNumbers")))
                    if (turn.ReadField<ulong>("key") == live.PlayerNetId && turn.ReadField<int>("value") != live.TurnNumber)
                        thisTurn = false;
            ClrObject actor = Obj(entry, "Actor");
            _entries.Add(new Entry(TypeName(entry), entry, thisTurn, actor.IsNull ? 0 : actor.Address));
        }
    }

    IEnumerable<Entry> Of(string kind) => _entries.Where(e => e.Kind == kind);
    IEnumerable<Entry> Turn(string kind) => Of(kind).Where(e => e.ThisTurn);

    ClrObject Play(Entry e) => Obj(e.Obj, "CardPlay");
    bool ByPlayer(ClrObject play) => Obj(Obj(play, "Player"), "Creature").Address == _player;
    CardStats Stats(ClrObject card)
    {
        Require(Registry.TryCard(Id(card), out ushort id), $"歷史中未支援的卡牌 {Id(card)}");
        return _rules.Card(id, Get<int>(card, "_currentUpgradeLevel"));
    }
    CardType TypeOf(ClrObject play) => Stats(Obj(play, "Card")).Type;
    bool First(ClrObject play) => Get<int>(play, "PlayIndex") == 0;

    IEnumerable<ClrObject> StartedThisTurn => Turn("CardPlayStartedEntry").Select(Play).Where(ByPlayer);
    IEnumerable<ClrObject> FinishedThisTurn => Turn("CardPlayFinishedEntry").Select(Play).Where(ByPlayer);

    public int CardsPlayedThisTurn => StartedThisTurn.Count(First);
    public int ManualCardsPlayedThisTurn => StartedThisTurn.Count(p => First(p) && !Get<bool>(p, "IsAutoPlay"));
    public int CardPlayStartsThisTurn => StartedThisTurn.Count();
    public int AttackPlayStartsThisTurn => StartedThisTurn.Count(p => TypeOf(p) == CardType.Attack);
    public int AttackSkillStartsThisTurn => StartedThisTurn.Count(p => TypeOf(p) is CardType.Attack or CardType.Skill);
    public int ZeroCostAttackStartsThisTurn => StartedThisTurn.Count(p => TypeOf(p) == CardType.Attack
        && Struct(p, "Resources").ReadField<int>(Backing("EnergyValue")) == 0);
    public int SkillCardsPlayedThisTurn => StartedThisTurn.Count(p => TypeOf(p) == CardType.Skill);
    public int AttacksPlayedThisTurn => FinishedThisTurn.Count(p => TypeOf(p) == CardType.Attack);
    public int ShivsPlayedThisTurn => FinishedThisTurn.Count(p => (Stats(Obj(p, "Card")).Tags & CardTag.Shiv) != 0);
    public int BlockCardsPlayedThisTurn => Turn("BlockGainedEntry").Count(e =>
        Obj(e.Obj, "CardPlay") is { IsNull: false } play && ByPlayer(play) && (Get<int>(e.Obj, "Props") & 8) != 0);
    public int CardsExhaustedThisTurn => Turn("CardExhaustedEntry").Count(e => e.Actor == _player);
    public int CardsDiscardedThisTurn => Turn("CardDiscardedEntry").Count(e => e.Actor == _player);
    public int PlayerAttacksThisTurn => Turn("CreatureAttackedEntry").Count(e => e.Actor == _player);
    public int NonHandDrawsThisTurn => Turn("CardDrawnEntry").Count(e => e.Actor == _player && !Get<bool>(e.Obj, "FromHandDraw"));
    public int StatusCardsDrawnThisTurn => Turn("CardDrawnEntry").Count(e => e.Actor == _player
        && Stats(Obj(e.Obj, "Card")).Type == CardType.Status);
    public int EnergySpentThisTurn => Turn("EnergySpentEntry").Where(e => e.Actor == _player).Sum(e => Get<int>(e.Obj, "Amount"));
    public int StarsGainedThisTurn => Turn("StarsModifiedEntry").Where(e => e.Actor == _player)
        .Sum(e => Math.Max(0, Get<int>(e.Obj, "Amount")));

    IEnumerable<ClrObject> Damage(bool thisTurn, ulong receiver) =>
        (thisTurn ? Turn("DamageReceivedEntry") : Of("DamageReceivedEntry"))
        .Select(e => e.Obj)
        .Where(d => Obj(Obj(d, "Result"), "Receiver").Address == receiver);

    public bool HasLostHpThisTurn(ulong creature) => Damage(true, creature).Any(d => Get<int>(Obj(d, "Result"), "UnblockedDamage") > 0);
    public int UnblockedDamageEventsThisTurn => Damage(true, _player).Count(d => Get<int>(Obj(d, "Result"), "UnblockedDamage") > 0);
    public int PoweredAttackHitsThisTurn(ulong receiver) => Damage(true, receiver).Count(d =>
        Obj(d, "Dealer").Address == _player && IsPowered(Get<int>(Obj(d, "Result"), "Props")));
    static bool IsPowered(int props) => (props & 8) != 0 && (props & 4) == 0;

    public bool DoomAppliedThisTurn => Turn("PowerReceivedEntry").Any(e =>
        Obj(e.Obj, "Applier").Address == _player && Id(Obj(e.Obj, "Power")) == "DOOM_POWER");

    public int FinishedCardPlays => Of("CardPlayFinishedEntry").Count();
    public int CardsDrawn => Of("CardDrawnEntry").Count(e => e.Actor == _player);
    public int LightningChannels => Of("OrbChanneledEntry").Count(e => e.Actor == _player
        && Id(Obj(e.Obj, "Orb")) == "LIGHTNING_ORB");
    public int EtherealPlays => Of("CardPlayFinishedEntry").Count(e => ByPlayer(Play(e)) && Get<bool>(e.Obj, "WasEthereal"));
    // FoolImporter: melody plays this turn by native tag (catalog tags stand in for card.Tags).
    bool Melody(ClrObject play, CardTag tag) => (Stats(Obj(play, "Card")).Tags & tag) != 0;
    public int MelodyStarted(CardTag tag) => StartedThisTurn.Count(p => Melody(p, tag));
    public int MelodyFinished(CardTag tag) => FinishedThisTurn.Count(p => Melody(p, tag));
    public int MelodyCount => FinishedThisTurn.Count(p => Melody(p, CardTag.Wah | CardTag.Cho | CardTag.Ki));
    public int SadnessGenerated => Of("CardGeneratedEntry").Count(e => Obj(e.Obj, "Creator").Address == _live.Player.Address
        && Id(Obj(e.Obj, "Card")) == "SADNESS");
    public int CardsGenerated => Of("CardGeneratedEntry").Count(e => Obj(e.Obj, "Creator").Address == _live.Player.Address);

    public bool FinishedThisTurnContains(ulong card) => FinishedThisTurn.Any(p => Obj(p, "Card").Address == card);

    public bool WasCardPlayFinished(ulong play) => Of("CardPlayFinishedEntry").Any(e => Play(e).Address == play);

    /// <summary>NativeHistory.HistoryCourseAttack at a Play root: this turn's last finished non-dupe attack.</summary>
    public ulong? HistoryCourseAttack()
    {
        ClrObject last = FinishedThisTurn.LastOrDefault(p => TypeOf(p) == CardType.Attack && !Get<bool>(Obj(p, "Card"), "_isDupe"));
        return last.IsNull ? null : Obj(last, "Card").Address;
    }

    /// <summary>NativeHistory.EmotionChipDamage: unblocked-or-partial damage to the player last / this player turn.</summary>
    public (bool LastPlayerTurn, bool ThisTurn) EmotionChipDamage()
    {
        bool last = false, current = false;
        foreach (Entry e in Of("DamageReceivedEntry"))
        {
            ClrObject result = Obj(e.Obj, "Result");
            if (Obj(result, "Receiver").Address != _player || Get<bool>(result, "WasFullyBlocked")) continue;
            foreach (ClrValueType turn in HashEntries(Obj(e.Obj, "_playerTurnNumbers")))
            {
                if (turn.ReadField<ulong>("key") != _live.PlayerNetId) continue;
                int n = turn.ReadField<int>("value");
                last |= n == _live.TurnNumber - 1;
                current |= n == _live.TurnNumber;
            }
        }
        return (last, current);
    }
}
