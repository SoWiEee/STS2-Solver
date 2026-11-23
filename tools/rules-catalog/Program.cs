// Builds reader/data/rules-catalog.br from the frozen RULE tables in rank-Yu/sts2-solver's Golden data
// (vendor/sts2-solver, MIT; see THIRD_PARTY_NOTICES.md), then re-reads the file and checks that every
// Standard-mode source table is reproduced field by field. Usage:
//   dotnet run --project tools/rules-catalog -c Release -- <golden data dir> <output .br> <report .md>
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Sts2Solver.Simulation.Combat;

string dataDir = args.Length > 0 ? args[0] : "vendor/sts2-solver/tests/Golden/data";
string outPath = args.Length > 1 ? args[1] : "reader/data/rules-catalog.br";
string reportPath = args.Length > 2 ? args[2] : "docs/rules-catalog.md";

var json = new JsonSerializerOptions { IncludeFields = true };
string Sig(object? value) => JsonSerializer.Serialize(value, json);
T Field<T>(Rules rules, string name) =>
    (T)(typeof(Rules).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(Rules), name)).GetValue(rules)!;

// Fixture names per rules hash, for provenance.
var rootNames = new Dictionary<string, List<string>>();
foreach (string meta in Directory.GetFiles(Path.Combine(dataDir, "d1"), "meta.json", SearchOption.AllDirectories))
{
    var root = JsonDocument.Parse(File.ReadAllText(meta)).RootElement;
    string hash = root.GetProperty("rulesSha256").GetString()!;
    (rootNames.TryGetValue(hash, out var list) ? list : rootNames[hash] = []).Add(root.GetProperty("root").GetString()!);
}

var sources = new List<(string Label, Rules Rules)>();
var skippedModes = new Dictionary<string, int>();
foreach (string path in Directory.GetFiles(Path.Combine(dataDir, "objects", "rules"), "*.br").Order())
{
    Rules rules = RulesCodec.Decode(Brotli(path));
    string hash = Path.GetFileNameWithoutExtension(path);
    string label = rootNames.TryGetValue(hash, out var names) ? string.Join("+", names) : "d2:" + hash[..12];
    if (rules.Mode != CombatMode.Standard)
    {
        skippedModes[rules.Mode.ToString()] = skippedModes.GetValueOrDefault(rules.Mode.ToString()) + 1;
        continue;
    }
    sources.Add((label, rules));
}

// ---- Merge content tables ----
var cards = new Dictionary<(ushort Id, int Upgrade), (CardStats Stats, string Sig)>();
var powers = new Dictionary<ushort, (decimal[] Vars, bool Risk, string Sig)>();
var potions = new Dictionary<ushort, (PotionStats Stats, string Sig)>();
var relics = new Dictionary<ushort, (decimal[] Vars, string Sig)>();
var pools = new Dictionary<string, (ushort[] Cards, string Sig, string Source)>(StringComparer.Ordinal);
var potionPools = new Dictionary<string, int>();
var outPotionPools = new Dictionary<string, int>();
var conflicts = new List<string>();
var monsters = new Dictionary<string, Dictionary<string, (MonsterRules Rules, List<string> Sources)>>();

foreach (var (label, rules) in sources)
{
    var cardTable = Field<CardStats?[][]>(rules, "_cards");
    for (ushort id = 0; id < cardTable.Length; id++)
        for (int up = 0; up < cardTable[id].Length; up++)
            if (cardTable[id][up] is { } stats)
                Merge(cards, (id, up), stats, Sig(stats), $"card {Registry.Card(id).NativeId}+{up}", label);

    var powerVars = Field<decimal[][]>(rules, "_powerVars");
    var risk = Field<bool[]>(rules, "_powerDeathHistoryRisk");
    for (ushort id = 0; id < powerVars.Length; id++)
        if (powerVars[id].Length > 0 || risk[id])
        {
            string sig = Sig(powerVars[id]) + risk[id];
            if (powers.TryGetValue(id, out var have))
            {
                if (have.Vars.Length == 0 && powerVars[id].Length > 0) powers[id] = (powerVars[id], risk[id], sig);
                else if (have.Sig != sig && powerVars[id].Length > 0) conflicts.Add($"power {Registry.Power(id).NativeId} @ {label}");
            }
            else powers[id] = (powerVars[id], risk[id], sig);
        }

    var potionTable = Field<PotionStats?[]>(rules, "_potions");
    for (ushort id = 0; id < potionTable.Length; id++)
        if (potionTable[id] is { } stats)
            Merge(potions, id, stats, Sig(stats), $"potion {Registry.Potion(id).NativeId}", label);

    var relicVars = Field<decimal[][]>(rules, "_relicVars");
    for (ushort id = 0; id < relicVars.Length; id++)
        if (relicVars[id].Length > 0)
            Merge(relics, id, relicVars[id], Sig(relicVars[id]), $"relic {Registry.Relic(id).NativeId}", label);

    foreach (var (rawKey, pool) in Field<Dictionary<string, CardPool>>(rules, "_cardPools"))
    {
        string key = PoolKey(rawKey, pool, cardTable);
        string sig = Sig(pool.Cards);
        if (!pools.TryGetValue(key, out var have)) pools[key] = (pool.Cards, sig, label);
        else if (have.Sig != sig) conflicts.Add($"card pool {key} @ {label} (vs {have.Source})");
    }
    Count(potionPools, Sig(rules.PotionPool));
    Count(outPotionPools, Sig(rules.OutOfCombatPotionPool));

    foreach (MonsterRules? monster in Field<MonsterRules?[]>(rules, "_monsters"))
    {
        if (monster == null) continue;
        string name = Registry.Monster(monster.MonsterId).NativeId;
        var variants = monsters.TryGetValue(name, out var v) ? v : monsters[name] = [];
        string sig = Sig(monster);
        if (!variants.TryGetValue(sig, out var entry)) variants[sig] = entry = (monster, []);
        if (!entry.Sources.Contains(label)) entry.Sources.Add(label);
    }
}

void Merge<TKey, TValue>(Dictionary<TKey, (TValue, string)> table, TKey key, TValue value, string sig, string what, string label)
    where TKey : notnull
{
    if (!table.TryGetValue(key, out var have)) table[key] = (value, sig);
    else if (have.Item2 != sig) conflicts.Add($"{what} @ {label}: {sig} (kept {have.Item2})");
}
static void Count(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

// character.* pools differ per character, so the catalog keys them as "<key>@<TransformPool>"
// (the most common pool of their base cards); other pools keep their key.
static string PoolKey(string key, CardPool pool, CardStats?[][] cardTable)
{
    if (!key.StartsWith("character.", StringComparison.Ordinal) || pool.Cards.Length == 0) return key;
    var owner = pool.Cards.Select(id => cardTable[id].FirstOrDefault(s => s != null)?.Pool)
        .Where(p => p != null).GroupBy(p => p).MaxBy(g => g.Count())?.Key;
    return owner == null ? key : $"{key}@{owner}";
}

// ---- Build catalog entries ----
var draft = new RulesDraft { Mode = CombatMode.Standard };
foreach (var ((id, up), (stats, _)) in cards) draft.SetCard(id, up, stats);
foreach (var (id, (vars, risk, _)) in powers) { if (vars.Length > 0) draft.SetPowerVars(id, vars); draft.SetPowerDeathHistoryRisk(id, risk); }
foreach (var (id, (stats, _)) in potions) draft.SetPotion(id, stats);
foreach (var (id, (vars, _)) in relics) draft.SetRelicVars(id, vars);
foreach (var (key, (poolCards, _, _)) in pools) draft.SetCardPool(key, poolCards);
// Potion pools depend on unlocks; keep the most common observed list.
draft.SetPotionPool(JsonSerializer.Deserialize<ushort[]>(potionPools.MaxBy(p => p.Value).Key)!);
draft.SetOutOfCombatPotionPool(JsonSerializer.Deserialize<ushort[]>(outPotionPools.MaxBy(p => p.Value).Key)!);

var entries = new List<RulesCatalogEntry>
{
    new(RulesCatalogKind.Content, "standard", sources.Select(s => s.Label).ToArray(), RulesCodec.Encode(draft.Build())),
};
foreach (var (name, variants) in monsters.OrderBy(m => m.Key, StringComparer.Ordinal))
    foreach (var (monster, monsterSources) in variants.Values.OrderBy(v => v.Rules.InitialHpMin).ThenBy(v => Sig(v.Rules), StringComparer.Ordinal))
    {
        var one = new RulesDraft { Mode = CombatMode.Standard };
        one.SetMonster(0, monster);
        entries.Add(new(RulesCatalogKind.Monster, name, monsterSources.Order().ToArray(), RulesCodec.Encode(one.Build())));
    }
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
RulesCatalogFile.Write(outPath, entries);

// ---- Verify: re-read the file and reproduce every source table ----
var read = RulesCatalogFile.Read(outPath);
Rules content = RulesCodec.Decode(read.Single(e => e.Kind == RulesCatalogKind.Content).Rules);
var catalogMonsters = read.Where(e => e.Kind == RulesCatalogKind.Monster)
    .Select(e => Sig(RulesCodec.Decode(e.Rules).MonsterRule(0))).ToHashSet();
var cCards = Field<CardStats?[][]>(content, "_cards");
var cPowers = Field<decimal[][]>(content, "_powerVars");
var cRisk = Field<bool[]>(content, "_powerDeathHistoryRisk");
var cPotions = Field<PotionStats?[]>(content, "_potions");
var cRelics = Field<decimal[][]>(content, "_relicVars");
var cPools = Field<Dictionary<string, CardPool>>(content, "_cardPools");
var mismatches = new List<string>();
var rootDependent = new List<string>();
int checkedValues = 0;
foreach (var (label, rules) in sources)
{
    var t = Field<CardStats?[][]>(rules, "_cards");
    for (int id = 0; id < t.Length; id++)
        for (int up = 0; up < t[id].Length; up++)
            if (t[id][up] is { } s)
            {
                checkedValues++;
                if (up < cCards[id].Length && Sig(cCards[id][up]) == Sig(s)) continue;
                // Root-dependent: FAN_OF_KNIVES_POWER makes Shiv target all enemies at rules compile time.
                if (Registry.Card((ushort)id).NativeId == "SHIV" && up < cCards[id].Length
                    && WithoutTarget(Sig(cCards[id][up])) == WithoutTarget(Sig(s)))
                { rootDependent.Add($"`SHIV`+{up} 的目標 @ {label}（場上有 `FAN_OF_KNIVES_POWER`，匯入時需改為全體敵人）"); continue; }
                mismatches.Add($"card {id}+{up} @ {label}");
            }
    var pv = Field<decimal[][]>(rules, "_powerVars");
    var pr = Field<bool[]>(rules, "_powerDeathHistoryRisk");
    for (int id = 0; id < pv.Length; id++)
    {
        if (pv[id].Length > 0) { checkedValues++; if (Sig(cPowers[id]) != Sig(pv[id])) mismatches.Add($"power {id} @ {label}"); }
        if (pr[id] != cRisk[id] && (pv[id].Length > 0 || pr[id])) mismatches.Add($"power risk {id} @ {label}");
    }
    var po = Field<PotionStats?[]>(rules, "_potions");
    for (int id = 0; id < po.Length; id++)
        if (po[id] is { } s) { checkedValues++; if (Sig(cPotions[id]) != Sig(s)) mismatches.Add($"potion {id} @ {label}"); }
    var rv = Field<decimal[][]>(rules, "_relicVars");
    for (int id = 0; id < rv.Length; id++)
        if (rv[id].Length > 0) { checkedValues++; if (Sig(cRelics[id]) != Sig(rv[id])) mismatches.Add($"relic {id} @ {label}"); }
    foreach (var (rawKey, pool) in Field<Dictionary<string, CardPool>>(rules, "_cardPools"))
    { string key = PoolKey(rawKey, pool, t); checkedValues++; if (!cPools.TryGetValue(key, out var cp) || Sig(cp.Cards) != Sig(pool.Cards)) mismatches.Add($"pool {key} @ {label}"); }
    foreach (MonsterRules? m in Field<MonsterRules?[]>(rules, "_monsters"))
        if (m != null) { checkedValues++; if (!catalogMonsters.Contains(Sig(m))) mismatches.Add($"monster {Registry.Monster(m.MonsterId).NativeId} @ {label}"); }
}

// ---- Report ----
int cardIds = cards.Keys.Select(k => k.Id).Distinct().Count();
var missingMonsters = Enumerable.Range(0, Registry.MonsterCount).Select(i => Registry.Monster((ushort)i).NativeId)
    .Where(n => !monsters.ContainsKey(n)).Order(StringComparer.Ordinal).ToList();
var report = new StringBuilder();
report.AppendLine("# 規則目錄涵蓋率");
report.AppendLine();
report.AppendLine("由 `tools/rules-catalog` 從 `vendor/sts2-solver/tests/Golden/data` 的凍結 RULE 表產生（rank-Yu/sts2-solver，MIT）。只合併標準模式；重新產生：`dotnet run --project tools/rules-catalog -c Release`。");
report.AppendLine();
report.AppendLine("| 項目 | 數量 |");
report.AppendLine("| --- | ---: |");
report.AppendLine($"| 來源規則表（標準模式） | {sources.Count} |");
report.AppendLine($"| 略過的其他模式 | {string.Join("、", skippedModes.Select(m => $"{m.Key} {m.Value}"))} |");
report.AppendLine($"| 卡牌 | {cardIds} / {Registry.CardCount} 種（{cards.Count} 個 id×升級）|");
int powersWithVars = Enumerable.Range(0, Registry.PowerCount).Count(i => Registry.Power((ushort)i).VarNames.Count > 0);
int relicsWithVars = Enumerable.Range(0, Registry.RelicCount).Count(i => Registry.Relic((ushort)i).VarNames.Count > 0);
report.AppendLine($"| 能力參數 | {powers.Count(p => p.Value.Vars.Length > 0)} / {powersWithVars}（只有定義了參數的能力需要；共 {Registry.PowerCount} 種能力） |");
report.AppendLine($"| 遺物參數 | {relics.Count} / {relicsWithVars}（只有定義了參數的遺物需要；共 {Registry.RelicCount} 種遺物） |");
report.AppendLine($"| 藥水 | {potions.Count} / {Registry.PotionCount} |");
report.AppendLine($"| 卡池 | {pools.Count} |");
report.AppendLine($"| 藥水池變體 | 戰鬥內 {potionPools.Count}、戰鬥外 {outPotionPools.Count}（目錄取最常見者） |");
report.AppendLine($"| 敵人 | {monsters.Count} / {Registry.MonsterCount} 種，{monsters.Sum(m => m.Value.Count)} 個數值變體 |");
report.AppendLine($"| 驗證 | {checkedValues} 個來源值逐欄位比對：{mismatches.Count} 個不符，{rootDependent.Count} 個已知的根相依差異 |");
report.AppendLine();
report.AppendLine("## 尚未涵蓋的敵人");
report.AppendLine();
report.AppendLine(string.Join("、", missingMonsters.Select(m => $"`{m}`")));
report.AppendLine();
report.AppendLine("## 敵人數值變體");
report.AppendLine();
report.AppendLine("同一敵人的變體通常對應不同進階等級（生命與傷害較高者為高進階），等 C4 用 REA 的 getter 公式確認對應關係。");
report.AppendLine();
report.AppendLine("| 敵人 | 變體 | 初始生命 |");
report.AppendLine("| --- | ---: | --- |");
foreach (var (name, variants) in monsters.OrderBy(m => m.Key, StringComparer.Ordinal))
    report.AppendLine($"| `{name}` | {variants.Count} | {string.Join("、", variants.Values.OrderBy(v => v.Rules.InitialHpMin).Select(v => v.Rules.InitialHpMin == v.Rules.InitialHpMax ? $"{v.Rules.InitialHpMin}" : $"{v.Rules.InitialHpMin}–{v.Rules.InitialHpMax}"))} |");
if (rootDependent.Count > 0)
{
    report.AppendLine();
    report.AppendLine("## 已知的根相依差異");
    report.AppendLine();
    report.AppendLine("目錄保存規範值；以下值依戰鬥當下狀態改變，匯入器必須自行套用。");
    report.AppendLine();
    foreach (string c in rootDependent) report.AppendLine($"- {c}");
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
File.WriteAllText(reportPath, report.ToString());
Console.WriteLine($"catalog {new FileInfo(outPath).Length} bytes, entries {entries.Count}; checked {checkedValues}, mismatches {mismatches.Count}, root-dependent {rootDependent.Count}");
foreach (string m in mismatches.Take(20)) Console.WriteLine("MISMATCH " + m);
return mismatches.Count == 0 ? 0 : 1;

static string WithoutTarget(string cardJson)
{
    var node = System.Text.Json.Nodes.JsonNode.Parse(cardJson)!.AsObject();
    node.Remove("Target");
    return node.ToJsonString();
}

static byte[] Brotli(string path)
{
    using var file = File.OpenRead(path);
    using var brotli = new BrotliStream(file, CompressionMode.Decompress);
    using var memory = new MemoryStream();
    brotli.CopyTo(memory);
    return memory.ToArray();
}
