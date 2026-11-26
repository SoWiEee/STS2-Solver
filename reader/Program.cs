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
app.MapGet("/reward-policy.js", () => Results.File(Path.Combine(root, "reward-policy.js"), "text/javascript; charset=utf-8"));
app.MapGet("/event-policy.js", () => Results.File(Path.Combine(root, "event-policy.js"), "text/javascript; charset=utf-8"));
app.MapGet("/events.json", () => Results.File(Path.Combine(root, "data", "events.json"), "application/json; charset=utf-8"));
foreach (string doc in new[] { "game", "policy", "live-import", "rules-catalog" })
    app.MapGet($"/docs/{doc}.md", () => Results.File(Path.Combine(root, "docs", doc + ".md"), "text/plain; charset=utf-8"));
IResult ReadSnapshot(HttpContext request, Func<object> read, object? gate = null)
{
    if (request.Request.Headers["X-STS2-Capture"] != "1") return Results.StatusCode(403);
    lock (gate ?? captureLock)
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
app.MapGet("/rewards", (HttpContext request) => ReadSnapshot(request, GameReader.CaptureRewards));
app.MapGet("/import", (HttpContext request) => ReadSnapshot(request, GameReader.CaptureImport));
app.MapGet("/event", (HttpContext request) => ReadSnapshot(request, GameReader.CaptureEvent));
app.MapGet("/names", (HttpContext request) => ReadSnapshot(request, GameReader.CaptureNames));
// Searches take seconds; their own lock keeps /capture polling responsive meanwhile (each read takes its own snapshot).
var solveLock = new object();
app.MapGet("/solve", (HttpContext request) => ReadSnapshot(request, () => GameReader.CaptureSolve(request.Request.Query["quick"] == "1", double.TryParse(request.Request.Query["budget"], out double budget) ? budget : null), solveLock));
app.Run();

static class GameReader
{
    // sts2.dll SHA-256 of each supported build.
    static readonly Dictionary<string, string> SupportedBuilds = new()
    {
        ["62ECBE91093E1FA862DE4329AAC865209DCB297804CAF0F0520D92D8C0B4A9C3"] = "0.113.1",
        ["DDCEB87905B6096DEB7BE975922E24A53992453675DF26DF545B1C46B1826C5D"] = "0.114.0",
    };
    static string Version = "0.114.0";

    // The page polls this every few seconds only to notice changes; reading without suspending keeps
    // combat smooth. The search (/solve) re-reads under a snapshot, so it always sees a consistent state.
    public static object Capture() => WithRuntime((r, _) => ReadCombat(r), suspend: false);

    // Map, reward and event screens are static: read them live without suspending the game, so the
    // page's polling outside combat does not stutter it. Combat keeps the consistent snapshot.
    public static object CaptureMap() => WithRuntime((r, _) => ReadMap(r), suspend: false);
    public static object CaptureRewards() => WithRuntime(ReadRewards, suspend: false);
    public static object CaptureEvent() => WithRuntime(ReadEvent, suspend: false);

    static readonly Lazy<Sts2Solver.LiveImport.RulesCatalog> Catalog = new(() =>
        new Sts2Solver.LiveImport.RulesCatalog(Path.Combine(AppContext.BaseDirectory, "data", "rules-catalog.br")));

    static readonly Sts2Solver.LiveImport.ScoreTemplates Templates = new(
        Path.Combine(AppContext.BaseDirectory, "data", "score-template-normal.br"),
        Path.Combine(AppContext.BaseDirectory, "data", "score-template-act3-boss.br"));

    // rank-Yu beam search over the whole remaining fight from the current root (~10 s).
    static string? solvedKey;
    static object? solvedResult;
    static bool solvedFull;
    // Several clients (page, overlay) poll /solve: the same combat state is searched once and shared.
    // ?quick=1 answers within ~1.5 s so the page can show a first route while the full search runs.
    public static object CaptureSolve(bool quick, double? budgetSeconds = null) => WithRuntime(runtime =>
    {
        var state = System.Text.Json.JsonSerializer.SerializeToNode(ReadCombat(runtime))!.AsObject();
        state.Remove("capturedAt");
        string key = state.ToJsonString();
        if (key == solvedKey && (solvedFull || quick) && budgetSeconds == null) return solvedResult!;
        var budget = TimeSpan.FromSeconds(budgetSeconds ?? (quick ? 1.5 : 30)); // easy fights end early (frontier empty); elites and bosses need the room
        var result = Sts2Solver.LiveImport.LiveImporter.Solve(runtime, Catalog.Value, Templates, budget);
        if (budgetSeconds == null) (solvedKey, solvedResult, solvedFull) = (key, result, !quick); // a custom budget is not shared with the page
        return result;
    });

    // rank-Yu solver import of the current root (Battle + Rules), with the kernel's end-turn prediction.
    // Chinese titles from the game's zht localization, keyed by content id: {cards, monsters, potions, powers, relics}.
    static object? names;
    public static object CaptureNames() => names ??= WithRuntime((_, gameDirectory) =>
    {
        Dictionary<string, string> Titles(string table, string suffix) => RelicText.Load(gameDirectory, table)
            .Where(e => e.Key.EndsWith(suffix, StringComparison.Ordinal) && e.Key.IndexOf('.') == e.Key.Length - suffix.Length)
            .ToDictionary(e => e.Key[..^suffix.Length], e => e.Value);
        return new { cards = Titles("cards", ".title"), monsters = Titles("monsters", ".name"),
            potions = Titles("potions", ".title"), powers = Titles("powers", ".title"), relics = Titles("relics", ".title") };
    });

    public static object CaptureImport() => WithRuntime(runtime =>
        Sts2Solver.LiveImport.LiveImporter.Describe(Sts2Solver.LiveImport.LiveImporter.Import(runtime, Catalog.Value)));

    static object WithRuntime(Func<ClrRuntime, object> read) => WithRuntime((runtime, _) => read(runtime));

    static string? verifiedDll;
    static object WithRuntime(Func<ClrRuntime, string, object> read, bool suspend = true)
    {
        var games = Process.GetProcessesByName("SlayTheSpire2");
        try
        {
            var windows = games.Where(game => game.MainWindowHandle != IntPtr.Zero).ToArray();
            var active = windows.Length == 1 ? windows[0] : games.Length == 1 ? games[0] : null;
            Require(active != null, "請開啟一個 Slay the Spire 2 遊戲視窗。");
            string dir = Path.GetDirectoryName(active!.MainModule!.FileName)!;
            string data = Path.Combine(dir, "data_sts2_windows_x86_64");
            string dllPath = Path.Combine(data, "sts2.dll");
            string dllKey = dllPath + File.GetLastWriteTimeUtc(dllPath).Ticks; // re-check after a game update
            if (verifiedDll != dllKey)
            {
                string hash;
                using (var dll = File.OpenRead(dllPath)) hash = Convert.ToHexString(SHA256.HashData(dll));
                Require(SupportedBuilds.TryGetValue(hash, out var build), "目前的遊戲組件版本未支援。");
                Version = build!;
                Sts2Solver.LiveImport.RulesCatalog.GameVersion = build!;
                verifiedDll = dllKey;
            }

            using var snapshot = suspend
                ? DataTarget.CreateSnapshotAndAttach(active.Id)
                : DataTarget.AttachToProcess(active.Id, suspend: false);
            var runtime = snapshot.ClrVersions.Single().CreateRuntime(Path.Combine(data, "mscordaccore.dll"));
            return read(runtime, dir);
        }
        finally { foreach (var game in games) game.Dispose(); }
    }

    /// <summary>
    /// The player on this machine. Over Steam a player's NetId is their SteamID64
    /// (SteamClient/SteamHost.NetId), and Steam records the signed-in account under
    /// HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser (32-bit account id).
    /// </summary>
    static int LocalPlayerIndex(ClrRuntime runtime, ClrObject[] players)
    {
        if (players.Length == 1) return 0;
        object? account = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
        Require(account is int id && id != 0, "讀不到目前登入的 Steam 帳號。");
        ulong me = 76561197960265728UL + (uint)(int)account!;
        int index = Array.FindIndex(players, p => Field<ulong>(p, Backing("NetId")) == me);
        Require(index >= 0, "多人遊戲中找不到本機玩家（目前只支援 Steam 連線）。");
        return index;
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
            foreach (var (obj, flag) in new[] { (manager, "_playerActionsDisabled"), (turn, Backing("IsStarting")),
                (turn, Backing("IsEnemyTurnStarted")), (turn, Backing("EndingPlayerTurnPhaseOne")), (turn, Backing("EndingPlayerTurnPhaseTwo")) })
                Require(!Field<bool>(obj, flag), $"戰鬥正在結算（{flag.Trim('<', '>').Replace(">k__BackingField", "")}）。請等到可出牌時擷取。");
            var combat = Object(turn, Backing("State"));
            Require(Field<int>(combat, Backing("CurrentSide")) == 1, "請在玩家回合擷取。");
            var pairs = List(Object(combat, "_allies"), 32)
                .Select(c => (creature: c, player: Object(c, Backing("Player"))))
                .Where(pair => !pair.player.IsNull).ToList();
            // Multiplayer: the local player comes first (the page and the search treat players[0] as "you").
            int local = LocalPlayerIndex(runtime, pairs.Select(p => p.player).ToArray());
            if (local > 0) { var me = pairs[local]; pairs.RemoveAt(local); pairs.Insert(0, me); }
            var players = pairs.Select(pair =>
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
                        // Relics the rank-Yu registry defines with no hooks at all: no combat effect.
                        inertRelics = List(Object(pair.player, "_relics"), 256).Where(IsInertRelic).Select(ShortName).ToArray(),
                        potions = List(Object(pair.player, "_potionSlots"), 32, allowEmpty: true).Select(ShortName).ToArray(),
                        drawPile = state.IsNull ? null : ReadPile(Object(state, Backing("DrawPile")), 256),
                        discardPile = state.IsNull ? null : ReadPile(Object(state, Backing("DiscardPile")), 256),
                        hand = hand.IsNull ? null : List(Object(hand, "_cards"), 10).Select(ReadCard).ToArray()
                    };
                }).ToArray();
            Require(players.Length is 1 or 2, "目前只支援單人或雙人戰鬥。");
            var enemies = List(Object(combat, "_enemies"), 32)
                .Where(c => Field<int>(c, "_currentHp") > 0 || IsReviving(c)).Select(c =>
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
            Require(enemies.Length is >= 1 and <= Sts2Solver.Simulation.Combat.Limits.Enemies, "存活敵人數超出讀取範圍。");
            var run = Object(combat, Backing("RunState"));
            return new { schemaVersion = 1, version = Version, capturedAt = DateTimeOffset.Now,
                round = Field<int>(combat, Backing("RoundNumber")), ascension = Field<int>(run, Backing("AscensionLevel")), players, enemies };
    }

    static bool IsReviving(ClrObject creature)
    {
        var move = Object(Object(creature, Backing("Monster")), Backing("NextMove"));
        return !move.IsNull && move.ReadStringField(Backing("StateId")) is "REVIVE_MOVE" or "RESPAWN_MOVE";
    }

    /// <summary>
    /// The local player's current event (EventSynchronizer._events), its live DynamicVars (already
    /// ascension-scaled) and options; effects are looked up client-side in data/events.json by TextKey.
    /// </summary>
    static object ReadEvent(ClrRuntime runtime, string gameDirectory)
    {
        var runManager = FindManager(runtime, "MegaCrit.Sts2.Core.Runs.RunManager");
        var run = Object(runManager, Backing("State"));
        Require(!run.IsNull, "請先開始遊戲。");
        var players = List(Object(run, "_players"), 4).ToArray();
        int me = LocalPlayerIndex(runtime, players);
        var player = players[me];
        var creature = Object(player, Backing("Creature"));
        Require(List(Object(run, "_currentRooms"), 8).LastOrDefault() is { IsNull: false } room && ShortName(room) == "EventRoom",
            "請在遊戲進入事件。");
        var events = List(Object(Object(runManager, Backing("EventSynchronizer")), "_events"), 4).ToList();
        Require(me < events.Count, "找不到本機玩家的事件。");
        var model = events[me]; // EventSynchronizer.GetEventForPlayer: one event per player slot
        string id = Object(model, Backing("Id")).ReadStringField(Backing("Entry")) ?? "";
        var vars = new Dictionary<string, decimal>();
        var words = new Dictionary<string, string>(); // StringVar values such as a relic's title
        foreach (var entry in DynamicVarEntries(model))
        {
            var value = entry.ReadObjectField("value");
            string name = entry.ReadObjectField("key").AsString() ?? "";
            if (ShortName(value) == "StringVar") words[name] = value.ReadStringField(Backing("StringValue")) ?? "";
            else if (value.TryReadField<decimal>("_baseValue", out var amount)) vars[name] = amount;
        }
        var text = RelicText.Load(gameDirectory, "events");
        foreach (var (key, value) in RelicText.Load(gameDirectory, "ancients")) text.TryAdd(key, value);
        string? Fill(string? template) => template == null ? null : words.Aggregate(template, (t, w) => t.Replace("{" + w.Key + "}", w.Value));
        var optionList = Object(model, "_currentOptions");
        Require(!optionList.IsNull, "事件目前沒有可選的選項。");
        var options = List(optionList, 16, allowEmpty: true).Select(option =>
        {
            string key = option.ReadStringField(Backing("TextKey")) ?? "";
            var relic = Object(option, Backing("Relic"));
            return new
            {
                textKey = key,
                title = RelicText.Describe(Fill(text.GetValueOrDefault(key + ".title")), vars),
                description = RelicText.Describe(Fill(text.GetValueOrDefault(key + ".description")), vars),
                locked = Field<bool>(option, Backing("IsLocked")),
                proceed = Field<bool>(option, Backing("IsProceed")),
                relic = relic.IsNull ? null : ShortName(relic),
                mayKill = !Object(option, Backing("WillKillPlayer")).IsNull,
            };
        }).ToArray();
        Require(options.Length > 0, "事件目前沒有可選的選項。");
        return new
        {
            eventId = id,
            title = text.GetValueOrDefault(id + ".title"),
            vars,
            options,
            deck = List(Object(Object(player, Backing("Deck")), "_cards"), 256).Select(card => ReadRewardCard(card)).ToArray(),
            relics = List(Object(player, "_relics"), 256).Select(ShortName).ToArray(),
            potions = List(Object(player, "_potionSlots"), 32, allowEmpty: true).Select(ShortName).ToArray(),
            potionSlots = Field<int>(Object(player, "_potionSlots"), "_size"),
            gold = Field<int>(player, "_gold"),
            character = ShortName(Object(player, Backing("Character"))),
            act = Field<int>(run, "_currentActIndex") + 1,
            ascension = Field<int>(run, Backing("AscensionLevel")),
            hp = Field<int>(creature, "_currentHp"),
            maxHp = Field<int>(creature, "_maxHp"),
        };
    }

    static object ReadRewards(ClrRuntime runtime, string gameDirectory)
    {
        var runManager = FindManager(runtime, "MegaCrit.Sts2.Core.Runs.RunManager");
        var run = Object(runManager, Backing("State"));
        Require(!run.IsNull, "請先開始遊戲。");
        var players = List(Object(run, "_players"), 4).ToArray();
        var player = players[LocalPlayerIndex(runtime, players)];
        var creature = Object(player, Backing("Creature"));
        var stack = FindManager(runtime, "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack");
        var overlays = List(Object(stack, "_overlays"), 32).ToArray();
        var top = overlays.LastOrDefault();
        var options = new List<RewardOption>();
        var relicText = new Lazy<Dictionary<string, string>>(() => RelicText.Load(gameDirectory));
        var cardText = new Lazy<Dictionary<string, string>>(() => RelicText.Load(gameDirectory, "cards"));
        bool shop = false;
        int pick = 1;
        bool remove = false;
        if (ShortName(top) == "NCardRewardSelectionScreen")
        {
            var active = runtime.Heap.EnumerateObjects().FirstOrDefault(obj =>
                obj.Type?.Name == "MegaCrit.Sts2.Core.Rewards.CardReward" &&
                obj.TryReadObjectField("_currentlyShownScreen", out var screen) && screen.Address == top.Address);
            Require(!active.IsNull, "找不到目前開啟的戰鬥卡牌獎勵。");
            foreach (var choice in List(Object(active, "_cards"), 32))
            {
                var card = Object(choice, "_modifiedCard");
                options.Add(ReadRewardCard(card.IsNull ? Object(choice, "originalCard") : card, cardText.Value));
            }
            if (Field<bool>(active, Backing("CanSkip"))) options.Add(new RewardOption("略過", "skip"));
        }
        else if (ShortName(top) == "NSimpleCardSelectScreen")
        {
            // Event "choose N of M cards to add" grid (EventModel.SelectCardsToAddToDeckFromGrid).
            foreach (var card in List(Object(top, "_cards"), 64))
                options.Add(ReadRewardCard(card, cardText.Value));
            pick = top.ReadValueTypeField("_prefs").ReadField<int>(Backing("MaxSelect"));
        }
        else if (ShortName(top) == "NDeckCardSelectScreen")
        {
            // CardSelectCmd.FromDeckForRemoval: the prompt is card_selection/TO_REMOVE; other deck picks are not advised.
            var prefs = top.ReadValueTypeField("_prefs");
            var prompt = prefs.ReadObjectField(Backing("Prompt"));
            Require(!prompt.IsNull && (prompt.ReadStringField("<locEntryKey>P") ?? "").Contains("REMOVE"), "這個選牌畫面不是移除卡牌，工具尚未支援。");
            foreach (var card in List(Object(top, "_cards"), 256))
                options.Add(ReadRewardCard(card, cardText.Value));
            pick = prefs.ReadField<int>(Backing("MaxSelect"));
            remove = true;
        }
        else if (ShortName(top) == "NRewardsScreen")
        {
            var set = Object(top, "_rewardsSet");
            Require(!set.IsNull, "獎勵尚未生成。");
            foreach (var reward in List(Object(set, Backing("Rewards")), 32))
            {
                if (ShortName(reward) != "RelicReward" || Field<bool>(reward, "_wasTaken")) continue;
                var relic = Object(reward, "_relic");
                if (relic.IsNull) continue;
                options.Add(ReadRelicReward(relic, relicText.Value));
            }
        }
        else if (top.IsNull)
        {
            var synchronizer = Object(runManager, Backing("TreasureRoomRelicSynchronizer"));
            if (!synchronizer.IsNull)
            {
                var relics = Object(synchronizer, "_currentRelics");
                if (!relics.IsNull && runtime.Heap.EnumerateObjects().Any(obj =>
                    obj.Type?.Name == "MegaCrit.Sts2.Core.Nodes.Rooms.NTreasureRoom" &&
                    Field<bool>(obj, "_isRelicCollectionOpen") &&
                    Object(obj, "_runState").Address == run.Address))
                {
                    options.AddRange(List(relics, 16).Select(relic => ReadRelicReward(relic, relicText.Value)));
                    if (options.Count > 0) options.Add(new RewardOption("略過", "skip"));
                }
            }
        }
        if (options.Count == 0)
        {
            foreach (var view in runtime.Heap.EnumerateObjects())
            {
                if (ShortName(view) != "NMerchantInventory" || !Field<bool>(view, Backing("IsOpen"))) continue;
                var inventory = Object(view, Backing("Inventory"));
                if (inventory.IsNull || Object(inventory, Backing("Player")).Address != player.Address) continue;
                shop = true;
                foreach (string field in new[] { "_characterCardEntries", "_colorlessCardEntries", "_relicEntries", "_potionEntries" })
                    foreach (var entry in List(Object(inventory, field), 32))
                    {
                        var option = ReadMerchantEntry(entry, relicText, cardText);
                        if (option != null) options.Add(option);
                    }
                var removal = Object(inventory, Backing("CardRemovalEntry"));
                if (!removal.IsNull && !Field<bool>(removal, Backing("Used")))
                    options.Add(new RewardOption("移除卡牌", "remove", Price: Field<int>(removal, "_cost")));
                options.Add(new RewardOption("保留金幣", "skip", Price: 0));
                break;
            }
        }
        if (options.Count == 0 && List(Object(run, "_currentRooms"), 8).LastOrDefault() is { IsNull: false } room
            && ShortName(room) == "RestSiteRoom")
            return ReadRestSite(player, creature);
        Require(options.Count > 0, "請在遊戲開啟卡牌選擇、戰利品、寶箱遺物、商店商品或營火畫面。");
        return new
        {
            deck = List(Object(Object(player, Backing("Deck")), "_cards"), 256).Select(card => ReadRewardCard(card, cardText.Value)).ToArray(),
            options,
            pick,
            remove,
            shop,
            gold = Field<int>(player, "_gold"),
            character = ShortName(Object(player, Backing("Character"))),
            act = Field<int>(run, "_currentActIndex") + 1,
            hp = Field<int>(creature, "_currentHp"),
            maxHp = Field<int>(creature, "_maxHp")
        };
    }

    // Rest site: rest heals 30% of max HP (HealRestSiteOption.GetBaseHealAmount; relic hooks not applied).
    // Smith candidates are ranked by what the upgrade changes in the rules catalog.
    // shortcut: rest-vs-smith is an HP-threshold heuristic, not a search over future fights.
    static object ReadRestSite(ClrObject player, ClrObject creature)
    {
        int hp = Field<int>(creature, "_currentHp"), maxHp = Field<int>(creature, "_maxHp");
        int heal = Math.Min((int)(maxHp * 0.3m), maxHp - hp);
        var upgrades = new List<(double Gain, object Row)>();
        foreach (var card in List(Object(Object(player, Backing("Deck")), "_cards"), 256))
        {
            string? id = Object(card, Backing("Id")).ReadStringField(Backing("Entry"));
            int level = Field<int>(card, "_currentUpgradeLevel");
            if (id == null || Catalog.Value.CardStats(id, level) is not { } now || level >= now.MaxUpgradeLevel
                || Catalog.Value.CardStats(id, level + 1) is not { } next) continue;
            var reasons = new List<string>();
            double gain = 0;
            if (!now.CostsX && next.EnergyCost < now.EnergyCost) { gain += 8 * (now.EnergyCost - next.EnergyCost); reasons.Add($"費用 {now.EnergyCost}→{next.EnergyCost}"); }
            for (int i = 0; i < Math.Min(now.Vars.Length, next.Vars.Length); i++)
                if (next.Vars[i] > now.Vars[i]) { gain += (double)(next.Vars[i] - now.Vars[i]); reasons.Add($"數值 {now.Vars[i]:0.#}→{next.Vars[i]:0.#}"); }
            var lost = now.Keywords & ~next.Keywords & (Sts2Solver.Simulation.Combat.Keywords.Exhaust | Sts2Solver.Simulation.Combat.Keywords.Ethereal);
            if (lost != 0) { gain += 4; reasons.Add($"移除 {lost}"); }
            var added = next.Keywords & ~now.Keywords & (Sts2Solver.Simulation.Combat.Keywords.Innate | Sts2Solver.Simulation.Combat.Keywords.Retain);
            if (added != 0) { gain += 2; reasons.Add($"獲得 {added}"); }
            if (gain > 0) upgrades.Add((gain, new { name = ShortName(card), id, gain = Math.Round(gain, 1), reasons }));
        }
        bool rest = hp + heal <= maxHp && hp < maxHp * 0.6m;
        return new
        {
            restSite = true, hp, maxHp, heal,
            recommendation = rest ? "rest" : "smith",
            upgrades = upgrades.OrderByDescending(u => u.Gain).Take(6).Select(u => u.Row).ToArray(),
        };
    }

    sealed record RewardOption(string Name, string Kind, int? Cost = null, decimal? Damage = null,
        decimal? Block = null, decimal? Draw = null, decimal? CombatStartHeal = null, string? Title = null,
        string? Description = null, decimal? OpeningEnergy = null, decimal? OpeningStrength = null,
        decimal? OpeningDexterity = null, decimal? OpeningVigor = null, string? Pool = null,
        int? StarCost = null, bool StarCostsX = false, decimal? DrawPutBack = null, bool GeneratesStars = false,
        decimal? NextTurnEnergy = null, int? Price = null, decimal? BlockPerTurn = null, bool ReturnsToHand = false,
        string? Unmodeled = null, decimal? EnergyPerTurn = null, decimal? DrawPerTurn = null, int JunkCards = 0);

    static RewardOption? ReadMerchantEntry(ClrObject entry, Lazy<Dictionary<string, string>> relicText,
        Lazy<Dictionary<string, string>> cardText)
    {
        if (ShortName(entry) == "MerchantCardEntry")
        {
            var choice = Object(entry, Backing("CreationResult"));
            if (choice.IsNull) return null;
            var card = Object(choice, "_modifiedCard");
            return ReadRewardCard(card.IsNull ? Object(choice, "originalCard") : card, cardText.Value)
                with { Price = Field<int>(entry, "_cost") };
        }
        var model = Object(entry, Backing("Model"));
        if (model.IsNull) return null;
        var option = ShortName(entry) == "MerchantRelicEntry"
            ? ReadRelicReward(model, relicText.Value)
            : new RewardOption(ShortName(model), "potion");
        return option with { Price = Field<int>(entry, "_cost") };
    }

    static RewardOption ReadRelicReward(ClrObject relic, Dictionary<string, string> text)
    {
        string name = ShortName(relic);
        string id = Object(relic, Backing("Id")).ReadStringField(Backing("Entry")) ?? name;
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var entry in DynamicVarEntries(relic))
        {
            var key = entry.ReadObjectField("key").AsString();
            if (key != null && entry.ReadObjectField("value").TryReadField<decimal>("_baseValue", out var amount))
                values[key] = amount;
        }
        decimal Value(string key, decimal canonical)
        {
            values.TryAdd(key, canonical);
            return values[key];
        }
        var option = name switch
        {
            "Anchor" => new RewardOption(name, "relic", Block: Value("Block", 10)),
            "FakeAnchor" => new RewardOption(name, "relic", Block: Value("Block", 4)),
            "HornCleat" => new RewardOption(name, "relic", Block: Value("Block", 14)),
            "BagOfPreparation" => new RewardOption(name, "relic", Draw: Value("Cards", 2)),
            "BloodVial" => new RewardOption(name, "relic", CombatStartHeal: Value("Heal", 2)),
            "Lantern" => new RewardOption(name, "relic", OpeningEnergy: Value("Energy", 1)),
            "VeryHotCocoa" => new RewardOption(name, "relic", OpeningEnergy: Value("Energy", 4)),
            "Vajra" => new RewardOption(name, "relic", OpeningStrength: Value("StrengthPower", 1)),
            "SwordOfJade" => new RewardOption(name, "relic", OpeningStrength: Value("StrengthPower", 3)),
            "OddlySmoothStone" => new RewardOption(name, "relic", OpeningDexterity: Value("DexterityPower", 1)),
            "Akabeko" => new RewardOption(name, "relic", OpeningVigor: Value("VigorPower", 8)),
            // shortcut: any other relic with a Damage var (e.g. combat-start damage) scores that damage once per fight.
            _ when values.TryGetValue("Damage", out decimal relicDamage) => new RewardOption(name, "relic", Damage: relicDamage),
            _ => new RewardOption(name, "relic")
        };
        return option with { Title = text.GetValueOrDefault(id + ".title"),
            Description = RelicText.Describe(text.GetValueOrDefault(id + ".description"), values) };
    }

    static RewardOption ReadRewardCard(ClrObject card, Dictionary<string, string>? text = null)
    {
        var cost = Object(card, "_energyCost");
        var hints = ReadCardHints(card);
        string name = ShortName(card);
        string id = Object(card, Backing("Id")).ReadStringField(Backing("Entry")) ?? name;
        var stats = Catalog.Value.RewardCardStats(id, Field<int>(card, "_currentUpgradeLevel"));
        var pool = Object(card, "_pool");
        int? energyCost = cost.IsNull
            ? stats?.CostsX == false ? stats.EnergyCost : null
            : Field<bool>(cost, Backing("CostsX")) ? null : Field<int>(cost, "_base");
        // Cards whose numbers mean something other than plain damage or draw.
        // shortcut: per-card fixes for cards seen in play, extend when a reward misreads another card.
        if (id == "KI_CHANT" && hints.ContainsKey("draw")) hints["draw"] = 1; // look at N, keep 1
        if (id == "FIEND_FIRE" && hints.TryGetValue("damage", out decimal perCard)) hints["damage"] = perCard * 4; // per exhausted card, ~4 in hand
        var values = DynamicVarEntries(card).ToDictionary(e => e.ReadObjectField("key").AsString()!,
            e => e.ReadObjectField("value").TryReadField<decimal>("_baseValue", out var v) ? v : 0m);
        // Stretch adds its amount to the next block-gaining card once (StretchPower.ModifyBlockAdditive);
        // Hidden Roots grants that Stretch every turn, so it scores as block per turn.
        decimal? blockPerTurn = values.TryGetValue("HiddenRootsPower", out decimal roots) ? roots : null;
        if (blockPerTurn == null && values.TryGetValue("StretchPower", out decimal stretch))
            hints["block"] = hints.GetValueOrDefault("block") + stretch;
        string? description = text == null ? null : RelicText.Describe(text.GetValueOrDefault(id + ".description"), values);
        // A "Cards" var is a count of anything (upgrade 2 cards, ...); it is draw only when the text puts cards in hand.
        if (description != null && hints.ContainsKey("draw") && !description.Contains('抽') && !description.Contains("手牌")) hints.Remove("draw");
        // shortcut: Doom kills at the end of the enemy turn once it reaches the enemy's HP, so it counts as
        // 3/4 of the same damage (delayed, all-or-nothing); a real model would track Doom against enemy HP.
        // CalculatedDamageVar (Squeeze, ...): CalculationBase plus extras that depend on game state; score the base.
        if (!hints.ContainsKey("damage") && values.TryGetValue("CalculationBase", out decimal calculated)
            && DynamicVarEntries(card).Any(e => ShortName(e.ReadObjectField("value")) == "CalculatedDamageVar"))
            hints["damage"] = calculated;
        decimal doom = values.GetValueOrDefault("DoomPower")
            + 2 * values.GetValueOrDefault("CountdownPower") // Doom on a random enemy each turn, ~2 turns per fight counted
            + (id == "NO_ESCAPE" ? values.GetValueOrDefault("CalculationBase") : 0)
            + (id == "BLIGHT_STRIKE" ? hints.GetValueOrDefault("damage") : 0); // Doom equal to the damage dealt
        if (doom > 0) hints["damage"] = hints.GetValueOrDefault("damage") + doom * 0.75m;
        // A power whose text starts "at the start of your turn" repeats its draw/energy every turn.
        bool everyTurn = stats?.Type == Sts2Solver.Simulation.Combat.CardType.Power && description?.Contains("回合開始時") == true;
        decimal? drawPerTurn = everyTurn && hints.Remove("draw", out decimal turnDraw) ? turnDraw : null;
        decimal? energyPerTurn = everyTurn && hints.TryGetValue("energy", out decimal turnEnergy) ? turnEnergy : null;
        return new RewardOption(name, "card",
            energyCost,
            hints.TryGetValue("damage", out decimal damage) ? damage : null,
            hints.TryGetValue("block", out decimal block) ? block : null,
            hints.TryGetValue("draw", out decimal draw) ? draw : null,
            Title: text?.GetValueOrDefault(id + ".title"), Description: description,
            Pool: pool.IsNull ? null : ShortName(pool),
            StarCost: Field<bool>(card, "_starCostSet") ? Field<int>(card, "_baseStarCost") : stats?.StarCost,
            StarCostsX: stats?.StarCostsX == true,
            DrawPutBack: id == "GLIMMER" ? 1 : null,
            GeneratesStars: id is "SOLAR_STRIKE" or "SHINING_STRIKE" or "GATHER_LIGHT",
            NextTurnEnergy: NextTurnEnergyCards.Contains(id) ? hints.GetValueOrDefault("energy", 1) : null,
            BlockPerTurn: blockPerTurn,
            // GetResultLocationForCardPlay sends these back to hand instead of the discard pile (Trick Shot only while arrows remain).
            ReturnsToHand: id is "MOURNING_BLOW" or "PARTICLE_WALL" or "TRICK_SHOT",
            Unmodeled: id == "RECALL" ? "效果依賴箭矢與抽牌堆順序，無法換算分數，請參照牌面。" : null,
            EnergyPerTurn: energyPerTurn, DrawPerTurn: drawPerTurn, JunkCards: JunkCount(description));
    }

    // Cards whose OnPlay applies EnergyNextTurnPower with their Energy var (decompiled 0.113.1).
    static readonly HashSet<string> NextTurnEnergyCards = ["CHARGE_BATTERY", "CONVERGENCE", "DELAY", "HEGEMONY", "INVOKE",
        "KI_REST", "KI_WHIP", "OUTMANEUVER", "REFINE_BLADE", "RELAX", "SCAVENGE", "SIDESTEP", "THE_STAR", "WAH_KI"];

    static object ReadMap(ClrRuntime runtime)
    {
        var manager = FindManager(runtime, "MegaCrit.Sts2.Core.Runs.RunManager");
        var run = Object(manager, Backing("State"));
        Require(!run.IsNull && !Field<bool>(manager, Backing("HasRunEnded")), "請先開始一局遊戲。");
        Require(!Field<bool>(manager, Backing("IsChangingRooms")), "正在切換房間。");
        Require(!Field<bool>(run, Backing("IsCustomGame")), "地圖規劃不適用於自訂模式。");
        var players = List(Object(run, "_players"), 4).ToArray();
        var me = players[LocalPlayerIndex(runtime, players)];
        var creature = Object(me, Backing("Creature"));
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
            gold = Field<int>(me, "_gold"),
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
        var enchantment = Object(card, Backing("Enchantment"));
        var affliction = Object(card, Backing("Affliction"));
        bool otherModifiers =
            (!cost.IsNull && !Object(cost, "_localModifiers").IsNull && Field<int>(Object(cost, "_localModifiers"), "_size") > 0) ||
            Field<int>(card, "_baseReplayCount") != 0 || Field<int>(card, "_extraReplayThisTurn") != 0;
        int? baseCost = cost.IsNull || Field<bool>(cost, Backing("CostsX")) ? null : Field<int>(cost, "_base");
        return new
        {
            name = ShortName(card),
            upgrades = Field<int>(card, "_currentUpgradeLevel"),
            baseCost = baseCost >= 0 ? baseCost : null,
            unplayable = baseCost == -1,
            targetType = Field<int>(card, Backing("TargetType")),
            modified = !enchantment.IsNull || !affliction.IsNull || otherModifiers,
            enchantment = enchantment.IsNull ? null : ShortName(enchantment),
            affliction = affliction.IsNull ? null : ShortName(affliction),
            otherModifiers,
            vars = ReadCardHints(card)
        };
    }

    // shortcut: junk is recognised by the localized titles of the status/curse cards a card text adds
    // ("將 1 張悲傷加入你的手牌"); a card id table would be exact but needs every generated-card pool.
    static int JunkCount(string? description) => description == null ? 0 : System.Text.RegularExpressions.Regex
        .Matches(description, @"將\s*(\d+)\s*張(?:悲傷|灼傷|暈眩|受傷|黏液|虛空|傷口|疑慮|傷痛|詛咒)")
        .Sum(m => int.Parse(m.Groups[1].Value));

    static Dictionary<string, decimal> ReadCardHints(ClrObject card)
    {
        var hints = new Dictionary<string, decimal>();
        foreach (var entry in DynamicVarEntries(card))
        {
            var value = entry.ReadObjectField("value");
            string? type = value.Type?.Name?.Split('.').Last();
            string? key = type switch { "DamageVar" or "OstyDamageVar" => "damage", "BlockVar" => "block",
                "EnergyVar" when entry.ReadObjectField("key").AsString() == "Energy" => "energy",
                "CardsVar" when entry.ReadObjectField("key").AsString() == "Cards" => "draw", _ => null };
            if (key != null && value.TryReadField<decimal>("_baseValue", out var amount) &&
                amount >= 0 && amount <= 999)
                hints[key] = amount;
        }
        return hints;
    }

    static IEnumerable<ClrValueType> DynamicVarEntries(ClrObject model)
    {
        if (!model.TryReadObjectField("_dynamicVars", out var vars) || vars.IsNull ||
            !vars.TryReadObjectField("_vars", out var dictionary) || dictionary.IsNull ||
            !dictionary.TryReadObjectField("_entries", out var entries) || entries.IsNull ||
            !dictionary.TryReadField<int>("_count", out int count))
            yield break;
        var array = entries.AsArray();
        if (count < 0 || count > 128 || count > array.Length) yield break;
        for (int i = 0; i < count; i++)
        {
            var entry = array.GetStructValue(i);
            if (!entry.ReadObjectField("key").IsNull) yield return entry;
        }
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

    static bool IsInertRelic(ClrObject relic)
    {
        string? id = Object(relic, Backing("Id")).ReadStringField(Backing("Entry"));
        if (id == null || !Sts2Solver.Simulation.Combat.Registry.TryRelic(id, out ushort rid)) return false;
        var def = Sts2Solver.Simulation.Combat.Registry.Relic(rid);
        return def.Hooks == 0 && def.HooksExt == 0;
    }

    static string ShortName(ClrObject obj) => obj.Type?.Name?.Split('.').Last() ?? "Unknown";
    static string Backing(string name) => $"<{name}>k__BackingField";
    static ClrObject Object(ClrObject obj, string field) => obj.ReadObjectField(field);
    static T Field<T>(ClrObject obj, string field) where T : unmanaged => obj.ReadField<T>(field);
    static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
    static IEnumerable<ClrObject> List(ClrObject list, int limit, bool allowEmpty = false)
    {
        int size = Field<int>(list, "_size");
        var array = Object(list, "_items").AsArray();
        Require(size >= 0 && size <= limit && size <= array.Length, "遊戲資料長度超出本工具的支援範圍。");
        for (int i = 0; i < size; i++)
        {
            var value = array.GetObjectValue(i);
            if (value.IsNull && allowEmpty) continue;
            Require(!value.IsNull, "擷取到不完整的遊戲資料。");
            yield return value;
        }
    }
}
