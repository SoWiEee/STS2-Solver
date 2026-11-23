using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;

const string address = "http://127.0.0.1:8765";
string root = AppContext.BaseDirectory;
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(address);
var app = builder.Build();
var captureLock = new object();
app.Use(async (request, next) =>
{
    if (request.Request.Host.Value != "127.0.0.1:8765")
    {
        request.Response.StatusCode = 400;
        return;
    }
    request.Response.Headers.CacheControl = "no-store";
    await next();
});
app.MapGet("/", () => Results.File(Path.Combine(root, "index.html"), "text/html; charset=utf-8"));
app.MapGet("/multi-turn.js", () => Results.File(Path.Combine(root, "multi-turn.js"), "text/javascript; charset=utf-8"));
app.MapGet("/live-policy.js", () => Results.File(Path.Combine(root, "live-policy.js"), "text/javascript; charset=utf-8"));
app.MapGet("/map-policy.js", () => Results.File(Path.Combine(root, "map-policy.js"), "text/javascript; charset=utf-8"));
foreach (string doc in new[] { "game", "policy" })
    app.MapGet($"/docs/{doc}.md", () => Results.File(Path.Combine(root, "docs", doc + ".md"), "text/plain; charset=utf-8"));
IResult ReadSnapshot(HttpContext request, Func<object> read)
{
    if (request.Request.Headers["X-STS2-Capture"] != "1") return Results.StatusCode(403);
    lock (captureLock)
    {
        try { return Results.Json(read()); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            app.Logger.LogWarning(error, "Capture failed");
            return Results.Json(new { error = error.Message }, statusCode: 409);
        }
    }
}
app.MapGet("/capture", (HttpContext request) => ReadSnapshot(request, GameReader.Capture));
app.MapGet("/map", (HttpContext request) => ReadSnapshot(request, GameReader.CaptureMap));
app.Run();

static class GameReader
{
    const string Version = "0.113.1";
    const string DllHash = "62ECBE91093E1FA862DE4329AAC865209DCB297804CAF0F0520D92D8C0B4A9C3";

    public static object Capture() => WithRuntime(ReadCombat);
    public static object CaptureMap() => WithRuntime(ReadMap);

    static object WithRuntime(Func<ClrRuntime, object> read)
    {
        var games = Process.GetProcessesByName("SlayTheSpire2");
        try
        {
            var windows = games.Where(game => game.MainWindowHandle != IntPtr.Zero).ToArray();
            var active = windows.Length == 1 ? windows[0] : games.Length == 1 ? games[0] : null;
            Require(active != null, "請開啟一個 Slay the Spire 2 遊戲視窗。");
            string dir = Path.GetDirectoryName(active!.MainModule!.FileName)!;
            string data = Path.Combine(dir, "data_sts2_windows_x86_64");
            using (var dll = File.OpenRead(Path.Combine(data, "sts2.dll")))
            using (var sha = SHA256.Create())
                Require(Convert.ToHexString(sha.ComputeHash(dll)) == DllHash, "目前的遊戲組件版本未支援。");

            using var snapshot = DataTarget.CreateSnapshotAndAttach(active.Id);
            var runtime = snapshot.ClrVersions.Single().CreateRuntime(Path.Combine(data, "mscordaccore.dll"));
            return read(runtime);
        }
        finally { foreach (var game in games) game.Dispose(); }
    }

    static ClrObject FindManager(ClrRuntime runtime, string type)
    {
        var managers = runtime.Heap.EnumerateObjects()
            .Where(obj => obj.Type?.Name == type).Take(2).ToArray();
        Require(managers.Length == 1, "尚未取得目前的遊戲狀態。");
        return managers[0];
    }

    static object ReadCombat(ClrRuntime runtime)
    {
            var manager = FindManager(runtime, "MegaCrit.Sts2.Core.Combat.CombatManager");
            var turn = Object(manager, "_turnState");
            Require(!turn.IsNull && Field<bool>(turn, Backing("IsInProgress")), "請進入一場戰鬥。");
            Require(!Field<bool>(manager, "_playerActionsDisabled") &&
                !Field<bool>(turn, Backing("IsStarting")) &&
                !Field<bool>(turn, Backing("IsEnemyTurnStarted")) &&
                !Field<bool>(turn, Backing("EndingPlayerTurnPhaseOne")) &&
                !Field<bool>(turn, Backing("EndingPlayerTurnPhaseTwo")),
                "戰鬥正在結算。請等到可出牌時擷取。");
            var combat = Object(turn, Backing("State"));
            Require(Field<int>(combat, Backing("CurrentSide")) == 1, "請在玩家回合擷取。");
            var players = List(Object(combat, "_allies"), 32)
                .Select(c => (creature: c, player: Object(c, Backing("Player"))))
                .Where(pair => !pair.player.IsNull).Select(pair =>
                {
                    var state = Object(pair.player, Backing("PlayerCombatState"));
                    ClrObject hand = state.IsNull ? default : Object(state, Backing("Hand"));
                    return new
                    {
                        hp = Field<int>(pair.creature, "_currentHp"),
                        maxHp = Field<int>(pair.creature, "_maxHp"),
                        block = Field<int>(pair.creature, "_block"),
                        energy = state.IsNull ? (int?)null : Field<int>(state, "_energy"),
                        maxEnergy = Field<int>(pair.player, Backing("MaxEnergy")),
                        powers = ReadPowers(pair.creature),
                        relics = List(Object(pair.player, "_relics"), 256).Select(ShortName).ToArray(),
                        drawPile = state.IsNull ? null : ReadPile(Object(state, Backing("DrawPile")), 256),
                        hand = hand.IsNull ? null : List(Object(hand, "_cards"), 10).Select(ReadCard).ToArray()
                    };
                }).ToArray();
            Require(players.Length is 1 or 2, "目前只支援單人或雙人戰鬥。");
            var enemies = List(Object(combat, "_enemies"), 32)
                .Where(c => Field<int>(c, "_currentHp") > 0).Select(c =>
                {
                    var monster = Object(c, Backing("Monster"));
                    var move = Object(monster, Backing("NextMove"));
                    return new
                    {
                        name = ShortName(monster),
                        hp = Field<int>(c, "_currentHp"),
                        maxHp = Field<int>(c, "_maxHp"),
                        block = Field<int>(c, "_block"),
                        move = move.IsNull ? null : move.ReadStringField(Backing("StateId")),
                        hasAttack = HasAttackIntent(move),
                        moves = ReadFollowUps(move),
                        powers = ReadPowers(c)
                    };
                }).ToArray();
            Require(enemies.Length is >= 1 and <= 3, "目前支援同時 1 到 3 名存活敵人。");
            var run = Object(combat, Backing("RunState"));
            return new { schemaVersion = 1, version = Version, capturedAt = DateTimeOffset.Now,
                round = Field<int>(combat, Backing("RoundNumber")), ascension = Field<int>(run, Backing("AscensionLevel")), players, enemies };
    }

    static object ReadMap(ClrRuntime runtime)
    {
        var manager = FindManager(runtime, "MegaCrit.Sts2.Core.Runs.RunManager");
        var run = Object(manager, Backing("State"));
        Require(!run.IsNull && !Field<bool>(manager, Backing("HasRunEnded")), "請先開始一局遊戲。");
        Require(!Field<bool>(manager, Backing("IsChangingRooms")), "正在切換房間。");
        Require(!Field<bool>(run, Backing("IsCustomGame")), "地圖規劃適用於單人標準與進階模式。");
        var player = List(Object(run, "_players"), 4).ToArray();
        Require(player.Length == 1, "地圖規劃目前限單人。");
        var creature = Object(player[0], Backing("Creature"));
        var map = Object(run, Backing("Map"));
        Require(!map.IsNull, "目前章節的地圖尚未生成。");

        var visited = Object(run, "_visitedMapCoords");
        int count = Field<int>(visited, "_size");
        var visitedItems = Object(visited, "_items").AsArray();
        Require(count >= 0 && count <= visitedItems.Length, "目前房間座標讀取失敗。");
        var current = count == 0 ? ((string id, int row, int col)?)null :
            GetPointCoord(visitedItems.GetStructValue(count - 1));
        var points = new Dictionary<string, MapNode>();
        var roots = MapPointSet(Object(map, "startMapPoints")).ToArray();
        var pending = new Queue<ClrObject>(roots);
        while (pending.TryDequeue(out var point))
        {
            var (id, row, col) = GetPointCoord(point.ReadValueTypeField("coord"));
            if (points.ContainsKey(id)) continue;
            Require(points.Count < 256, "地圖超出目前支援的房間數。");
            var children = MapPointSet(Object(point, Backing("Children"))).ToArray();
            var type = Field<int>(point, Backing("PointType"));
            var (key, label) = type switch
            {
                1 => ("unknown", "未知"), 2 => ("shop", "商店"), 3 => ("treasure", "寶箱"),
                4 => ("rest", "營火"), 5 => ("monster", "戰鬥"), 6 => ("elite", "精英"),
                7 => ("boss", "Boss"), 8 => ("ancient", "先古之民"), _ => ("unknown", "未知")
            };
            points.Add(id, new MapNode(id, label, key, row, col,
                children.Select(c => GetPointCoord(c.ReadValueTypeField("coord")).id).OrderBy(c => c).ToArray()));
            foreach (var child in children) pending.Enqueue(child);
        }
        string[] starts;
        if (current == null || (roots.Length > 0 &&
            current.Value.row < roots.Min(r => GetPointCoord(r.ReadValueTypeField("coord")).row)))
            starts = roots.Select(r => GetPointCoord(r.ReadValueTypeField("coord")).id).ToArray();
        else if (points.TryGetValue(current.Value.id, out var here)) starts = here.Children;
        else throw new InvalidOperationException("找不到目前房間在地圖上的位置。");
        return new
        {
            hp = Field<int>(creature, "_currentHp"),
            maxHp = Field<int>(creature, "_maxHp"),
            gold = Field<int>(player[0], "_gold"),
            act = Field<int>(run, "_currentActIndex") + 1,
            ascension = Field<int>(run, Backing("AscensionLevel")),
            nodes = points.Values, startIds = starts, current = current?.id
        };
    }

    sealed record MapNode(string Id, string Name, string Type, int Row, int Column, string[] Children);

    static (string id, int row, int col) GetPointCoord(ClrValueType coord)
    {
        int row = coord.ReadField<int>("row"), col = coord.ReadField<int>("col");
        return ($"{row}:{col}", row, col);
    }

    static IEnumerable<ClrObject> MapPointSet(ClrObject set)
    {
        int count = Field<int>(set, "_count");
        if (count == 0) yield break;
        var array = Object(set, "_entries").AsArray();
        Require(count > 0 && count <= 32 && count <= array.Length, "地圖房間連線超出支援範圍。");
        for (int i = 0; i < count; i++)
        {
            var entry = array.GetStructValue(i);
            if (entry.ReadField<int>("Next") < -1) continue;
            var point = entry.ReadObjectField("Value");
            Require(!point.IsNull, "擷取到不完整的地圖連線。");
            yield return point;
        }
    }

    static object[] ReadPile(ClrObject pile, int limit) =>
        List(Object(pile, "_cards"), limit).Select(ReadCard).ToArray();

    static object[] ReadPowers(ClrObject creature) =>
        List(Object(creature, "_powers"), 256)
            .Select(power => (object)new { name = ShortName(power), amount = Field<int>(power, "_amount") }).ToArray();

    static string?[] ReadFollowUps(ClrObject move)
    {
        var moves = new List<string?>();
        while (!move.IsNull && moves.Count < 4)
        {
            if (!move.TryReadStringField(Backing("StateId"), 256, out var id)) break;
            moves.Add(id);
            if (!move.TryReadObjectField(Backing("FollowUpState"), out var next)) break;
            move = next;
        }
        return moves.ToArray();
    }

    static object ReadCard(ClrObject card)
    {
        var cost = Object(card, "_energyCost");
        int? baseCost = cost.IsNull || Field<bool>(cost, Backing("CostsX")) ? null : Field<int>(cost, "_base");
        return new
        {
            name = ShortName(card),
            upgrades = Field<int>(card, "_currentUpgradeLevel"),
            baseCost = baseCost >= 0 ? baseCost : null,
            unplayable = baseCost == -1,
            targetType = Field<int>(card, Backing("TargetType")),
            modified = !Object(card, Backing("Enchantment")).IsNull || !Object(card, Backing("Affliction")).IsNull ||
                (!cost.IsNull && !Object(cost, "_localModifiers").IsNull && Field<int>(Object(cost, "_localModifiers"), "_size") > 0) ||
                Field<int>(card, "_baseReplayCount") != 0 || Field<int>(card, "_extraReplayThisTurn") != 0,
            vars = ReadCardHints(card)
        };
    }

    static Dictionary<string, decimal> ReadCardHints(ClrObject card)
    {
        var hints = new Dictionary<string, decimal>();
        if (!card.TryReadObjectField("_dynamicVars", out var vars) || vars.IsNull ||
            !vars.TryReadObjectField("_vars", out var dictionary) || dictionary.IsNull ||
            !dictionary.TryReadObjectField("_entries", out var entries) || entries.IsNull ||
            !dictionary.TryReadField<int>("_count", out int count))
            return hints;
        var array = entries.AsArray();
        if (count < 0 || count > 128 || count > array.Length) return hints;
        for (int i = 0; i < count; i++)
        {
            var entry = array.GetStructValue(i);
            var value = entry.ReadObjectField("value");
            string? type = value.Type?.Name?.Split('.').Last();
            string? key = type switch { "DamageVar" => "damage", "BlockVar" => "block", _ => null };
            if (key != null && value.TryReadField<decimal>("_baseValue", out var amount) &&
                amount >= 0 && amount <= 999)
                hints[key] = amount;
        }
        return hints;
    }

    static bool? HasAttackIntent(ClrObject move)
    {
        if (move.IsNull) return null;
        var intents = Object(move, Backing("Intents"));
        if (intents.IsNull) return null;
        if (intents.Type?.IsArray == true)
        {
            var array = intents.AsArray();
            return Enumerable.Range(0, array.Length).Any(i => IsAttackIntent(array.GetObjectValue(i)));
        }
        if (intents.Type?.Name?.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) == true)
            return List(intents, 32).Any(IsAttackIntent);
        return null;
    }

    static bool IsAttackIntent(ClrObject intent)
    {
        Require(!intent.IsNull, "擷取到不完整的敵方意圖。");
        for (var type = intent.Type; type != null; type = type.BaseType)
            if (type.Name == "MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent")
                return true;
        return false;
    }

    static string ShortName(ClrObject obj) => obj.Type?.Name?.Split('.').Last() ?? "Unknown";
    static string Backing(string name) => $"<{name}>k__BackingField";
    static ClrObject Object(ClrObject obj, string field) => obj.ReadObjectField(field);
    static T Field<T>(ClrObject obj, string field) where T : unmanaged => obj.ReadField<T>(field);
    static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
    static IEnumerable<ClrObject> List(ClrObject list, int limit)
    {
        int size = Field<int>(list, "_size");
        var array = Object(list, "_items").AsArray();
        Require(size >= 0 && size <= limit && size <= array.Length, "遊戲資料長度超出本工具的支援範圍。");
        for (int i = 0; i < size; i++)
        {
            var value = array.GetObjectValue(i);
            Require(!value.IsNull, "擷取到不完整的遊戲資料。");
            yield return value;
        }
    }
}
