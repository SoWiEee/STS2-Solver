// Validates tools/card-stats/parse.mjs: applying the parse of the 0.113.1 decompilation to the frozen
// catalog must reproduce the catalog. Usage: dotnet run -- <rules-catalog.br> <cards-113.json>
using Sts2Solver.LiveImport;
using Sts2Solver.Simulation.Combat;

var catalog = new RulesCatalog(args[0]);
if (args.Length > 2) // dotnet run -- <catalog> <version> CARD_ID...: print adjusted stats for that build
{
    RulesCatalog.GameVersion = args[1];
    foreach (string id in args[2..])
        for (int level = 0; level <= 1; level++)
            if (catalog.CardStats(id, level) is { } s)
                Console.WriteLine($"{id}+{level} cost {s.EnergyCost} {s.Type} {s.Rarity} kw {s.Keywords} vars [{string.Join(",", s.Vars)}]");
    return;
}
var parsed = CardOverrides.Load(args[1]);
int checkedLevels = 0, matched = 0;
var problems = new Dictionary<string, List<string>>();
void Note(string field, string what) => (problems.TryGetValue(field, out var l) ? l : problems[field] = []).Add(what);

foreach (var (id, card) in parsed)
{
    if (!Registry.TryCard(id, out ushort cardId)) continue;
    var names = Registry.Card(cardId).VarNames;
    for (int level = 0; level <= 1; level++)
    {
        var expected = catalog.CardStats(id, level);
        if (expected == null) continue;
        checkedLevels++;
        var actual = CardOverrides.Apply(expected, card, level, names);
        if (actual == null) { Note("vars-missing", $"{id}+{level} [{string.Join(",", names)}] parsed {{{string.Join(",", card.Vars.Keys)}}}"); continue; }
        bool ok = true;
        void Cmp<T>(string field, T a, T b) { if (!EqualityComparer<T>.Default.Equals(a, b)) { ok = false; Note(field, $"{id}+{level}: catalog {a} parse {b}"); } }
        Cmp("cost", expected.EnergyCost, actual.EnergyCost);
        Cmp("type", expected.Type, actual.Type);
        Cmp("rarity", expected.Rarity, actual.Rarity);
        Cmp("target", expected.Target, actual.Target);
        Cmp("keywords", expected.Keywords, actual.Keywords);
        Cmp("maxUpgrade", expected.MaxUpgradeLevel, actual.MaxUpgradeLevel);
        Cmp("vars", string.Join(",", expected.Vars), string.Join(",", actual.Vars));
        if (ok) matched++;
    }
}
Console.WriteLine($"{matched}/{checkedLevels} card levels reproduced exactly");
foreach (var (field, list) in problems.OrderByDescending(p => p.Value.Count))
{
    Console.WriteLine($"{field}: {list.Count}");
    foreach (var line in list.Take(6)) Console.WriteLine("  " + line);
}
