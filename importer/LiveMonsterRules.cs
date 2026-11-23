// Compiles MonsterRules for an enemy the catalog lacks, from its live MonsterMoveStateMachine.
// Follows rank-Yu/sts2-solver src/Import/Combat/RulesCompiler.cs CompileMonster/CompileMove
// (MIT, commit f0167be). Upstream evaluates each intent's DamageCalc and each branch's weight
// lambda in-process; out of process only lambdas that close over a constant (the game's
// `() => damage` / `() => weight` overloads) can be read, so anything else is rejected.
using Microsoft.Diagnostics.Runtime;
using Sts2Solver.Simulation.Combat;
using static Sts2Solver.LiveImport.Clr;

namespace Sts2Solver.LiveImport;

internal static class LiveMonsterRules
{
    public static MonsterRules Compile(ClrObject creature, ClrObject monster, string monsterId, MonsterDef def, List<string> notes,
        RulesCatalog catalog, int ascension)
    {
        Require(Registry.TryMonster(monsterId, out ushort mid), $"未支援的敵人 {monsterId}");
        ClrObject machine = Obj(monster, "_moveStateMachine");
        Require(!machine.IsNull, $"{monsterId} 沒有行動狀態機");
        var states = HashEntries(Obj(machine, "States")).Select(e => e.ReadObjectField("value")).ToList();
        ClrObject initial = Obj(machine, "_initialState");
        Require(!initial.IsNull, $"{monsterId} 沒有初始狀態");
        if (!states.Any(s => s.Address == initial.Address)) states.Add(initial); // unregistered initial branch
        var ids = states.Select(StateId).ToList();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ids.Count; i++) index[ids[i]] = i;
        int nodeCount = ids.Count + 1 + (def.IllusionRevive ? 1 : 0);
        Require(nodeCount <= 64, $"{monsterId} 狀態數 {ids.Count}");

        var nodes = new MonsterNode[nodeCount];
        for (int i = 0; i < ids.Count; i++)
        {
            ClrObject state = states[i];
            string kind = TypeName(state);
            nodes[i] = kind switch
            {
                "MoveState" => CompileMove(state, ids[i], index, monsterId, catalog, ascension),
                "RandomBranchState" => new MonsterNode
                {
                    NativeId = ids[i],
                    Kind = MonsterNodeKind.RandomBranch,
                    Options = StructList(Obj(state, "States")).Select(option =>
                    {
                        string target = option.ReadObjectField("stateId").AsString()!;
                        Require(index.TryGetValue(target, out int targetIndex), $"{monsterId}.{target} 不在狀態表");
                        return new RandomBranchOption(targetIndex, ConstantFloat(option.ReadObjectField("weightLambda"), $"{monsterId} 隨機權重 {ids[i]}.{target}"),
                            (MoveRepeatType)(byte)option.ReadField<int>("repeatType"), option.ReadField<int>("maxTimes"), option.ReadField<int>("cooldown"));
                    }).ToArray(),
                },
                _ => throw new LiveUnsupportedException($"{monsterId} 的狀態 {ids[i]}（{kind}）需要在遊戲內求值，暫不支援"),
            };
        }
        int stunned = ids.Count;
        nodes[stunned] = new MonsterNode { NativeId = Kernel.StunnedNodeId, Kind = MonsterNodeKind.Move, MustPerformOnceBeforeTransitioning = true };
        int revive = -1;
        if (def.IllusionRevive)
        {
            revive = stunned + 1;
            nodes[revive] = new MonsterNode { NativeId = Kernel.ReviveNodeId, Kind = MonsterNodeKind.Move, MustPerformOnceBeforeTransitioning = true };
        }
        int pending = states.FindIndex(s => TypeName(s) == "MoveState");
        Require(pending >= 0, $"{monsterId} 沒有可用行動狀態");

        int[] staticInts = new int[def.StaticIntNames.Count];
        for (int i = 0; i < staticInts.Length; i++)
            Require(LiveRules.TryMonsterInt(monster, def.StaticIntNames[i], out staticInts[i])
                || catalog.TryStatic(monsterId, def.StaticIntNames[i], ascension, out staticInts[i]),
                $"{monsterId}.{def.StaticIntNames[i]} 無法從記憶體或 REA 數值表讀取");
        int[] registers = new int[def.AiRegisterNames.Count];
        for (int i = 0; i < registers.Length; i++)
            Require(LiveRules.TryMonsterInt(monster, def.AiRegisterNames[i], out registers[i])
                || catalog.TryStatic(monsterId, def.AiRegisterNames[i], ascension, out registers[i]),
                $"{monsterId} 的 AI 狀態 {def.AiRegisterNames[i]} 無法讀取");
        int[] rootPowers = new int[def.RootPowerAmountNames.Count];
        var powers = LiveState.Powers(creature);
        for (int i = 0; i < rootPowers.Length; i++)
            foreach (ClrObject power in powers)
                if (Get<int>(power, "_amount") != 0 && Id(power) == def.RootPowerAmountNames[i])
                { rootPowers[i] = Get<int>(power, "_amount"); break; }
        // MinInitialHp/MaxInitialHp are computed getters: take them from the REA table, else the
        // root creature's base max HP (they only matter when this type spawns mid-fight).
        int baseHp = Nullable<int>(creature, "MonsterMaxHpBeforeModification") ?? Get<int>(creature, "_maxHp");
        int hpMin = catalog.TryStatic(monsterId, "MinInitialHp", ascension, out int min) ? min : baseHp;
        int hpMax = catalog.TryStatic(monsterId, "MaxInitialHp", ascension, out int max) ? max : baseHp;
        notes.Add($"{monsterId} 不在規則目錄，行動模式改從遊戲記憶體讀取");
        return new MonsterRules
        {
            MonsterId = mid,
            Nodes = nodes,
            InitialNode = index[StateId(initial)],
            PendingInitialMoveNode = pending,
            InitialMoveLogged = TypeName(initial) == "MoveState",
            StunnedNode = stunned,
            ReviveNode = revive,
            InitialHpMin = hpMin,
            InitialHpMax = hpMax,
            StaticInts = staticInts,
            RootPowerAmounts = rootPowers,
            InitialAiRegisters = registers,
        };
    }

    static MonsterNode CompileMove(ClrObject move, string id, Dictionary<string, int> index, string monsterId,
        RulesCatalog catalog, int ascension)
    {
        ClrObject followState = Obj(move, "FollowUpState");
        string? followUp = followState.IsNull ? Str(move, "FollowUpStateId") : StateId(followState);
        return new MonsterNode
        {
            NativeId = id,
            Kind = MonsterNodeKind.Move,
            FollowUp = followUp != null && index.TryGetValue(followUp, out int target) ? target : -1,
            MustPerformOnceBeforeTransitioning = Get<bool>(move, "MustPerformOnceBeforeTransitioning"),
            Attacks = LiveAttacks(move, $"{monsterId}.{id}", monsterId, catalog, ascension),
        };
    }

    /// <summary>
    /// The move's attack intents as (damage, repeats), the values upstream's CaptureAttacks gets by
    /// invoking DamageCalc and Repeats. A delegate is resolved when it closes over a constant, or
    /// when it is bound to one of the monster's integer getters listed in the REA statics table.
    /// </summary>
    internal static MonsterAttack[] LiveAttacks(ClrObject move, string what, string monsterId, RulesCatalog catalog, int ascension)
    {
        var attacks = new List<MonsterAttack>();
        ClrObject intents = Obj(move, "Intents");
        IEnumerable<ClrObject> items = intents.Type!.IsArray
            ? Enumerable.Range(0, intents.AsArray().Length).Select(i => intents.AsArray().GetObjectValue(i))
            : Has(intents, "_size") ? List(intents)
            : Has(intents, "_item") ? [Obj(intents, "_item")]
            : Enumerable.Range(0, Obj(intents, "_items").AsArray().Length).Select(i => Obj(intents, "_items").AsArray().GetObjectValue(i));
        foreach (ClrObject intent in items)
        {
            if (!Is(intent, "MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent")) continue;
            int damage = Resolve(Obj(intent, "DamageCalc"), $"{what} 的攻擊傷害", monsterId, catalog, ascension);
            int repeats = TypeName(intent) switch
            {
                "SingleAttackIntent" => 1,
                "MultiAttackIntent" => Obj(intent, "_repeatCalc").IsNull ? Get<int>(intent, "_repeat")
                    : Resolve(Obj(intent, "_repeatCalc"), $"{what} 的攻擊次數", monsterId, catalog, ascension),
                _ => throw new LiveUnsupportedException($"{what} 的攻擊意圖 {TypeName(intent)} 暫不支援"),
            };
            attacks.Add(new MonsterAttack(Math.Max(0, damage), Math.Max(1, repeats)));
        }
        return [.. attacks];
    }

    static int Resolve(ClrObject lambda, string what, string monsterId, RulesCatalog catalog, int ascension)
    {
        Require(!lambda.IsNull, $"{what} 沒有計算式");
        ClrObject target = lambda.ReadObjectField("_target");
        if (target.Type?.Name?.Contains("DisplayClass") == true) return (int)ConstantNumber(lambda, what);
        ClrRuntime runtime = lambda.Type!.Heap.Runtime;
        ClrMethod? method = runtime.GetMethodByInstructionPointer(lambda.ReadField<ulong>("_methodPtr"))
            ?? runtime.GetMethodByInstructionPointer(lambda.ReadField<ulong>("_methodPtrAux"));
        string? name = method?.Name;
        Require(name is not null && name.StartsWith("get_", StringComparison.Ordinal)
            && catalog.TryStatic(monsterId, name[4..], ascension, out _), $"{what} 由遊戲內的計算式（{name ?? "未知"}）決定，暫不支援");
        catalog.TryStatic(monsterId, name![4..], ascension, out int value);
        return value;
    }

    static string StateId(ClrObject state) =>
        (Has(state, "StateId") ? Str(state, "StateId") : Has(state, "BranchId") ? Str(state, "BranchId") : null)
        ?? throw new LiveUnsupportedException($"無法讀取 {TypeName(state)} 的狀態 id");

    /// <summary>The value captured by a `() => constant` closure: its single numeric field.</summary>
    static decimal ConstantNumber(ClrObject lambda, string what)
    {
        Require(!lambda.IsNull, $"{what} 沒有計算式");
        ClrObject target = lambda.ReadObjectField("_target");
        var fields = target.Type?.Fields.Where(f => f.ElementType is ClrElementType.Int32 or ClrElementType.Float or ClrElementType.Double
            || f.Type?.Name == "System.Decimal").ToList();
        Require(target.Type?.Name?.Contains("DisplayClass") == true && fields is { Count: 1 },
            $"{what} 由遊戲內的計算式決定，暫不支援");
        ClrInstanceField field = fields![0];
        return field.ElementType switch
        {
            ClrElementType.Int32 => target.ReadField<int>(field.Name!),
            ClrElementType.Float => (decimal)target.ReadField<float>(field.Name!),
            ClrElementType.Double => (decimal)target.ReadField<double>(field.Name!),
            _ => target.ReadField<decimal>(field.Name!),
        };
    }

    static float ConstantFloat(ClrObject lambda, string what) => (float)ConstantNumber(lambda, what);
}
