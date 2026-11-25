// Reads card stats straight from an ILSpy decompilation of sts2.dll:
//   node parse.mjs <decomp dir> <out.json> [ClassName ...]
// Per card: constructor (cost, type, rarity, target), CanonicalVars, CanonicalKeywords/Tags and the
// OnUpgrade deltas. Used to bring the frozen 0.113.1 card stats up to a newer game build.
import fs from "node:fs";
import path from "node:path";

const [dir, out, ...only] = process.argv.slice(2);
const cardsDir = path.join(dir, "MegaCrit.Sts2.Core.Models.Cards");
const snake = s => s.replace(/([a-z0-9])([A-Z])/g, "$1_$2").replace(/([A-Z])([A-Z][a-z])/g, "$1_$2").toUpperCase();
const num = s => Number(String(s).replace(/m$/, ""));

// Named var classes and the DynamicVar key each one registers under.
const VAR_NAMES = {
  DamageVar: "Damage", BlockVar: "Block", CardsVar: "Cards", EnergyVar: "Energy", RepeatVar: "Repeat",
  GoldVar: "Gold", HpLossVar: "HpLoss", HealVar: "Heal", ExtraDamageVar: "ExtraDamage", MaxHpVar: "MaxHp",
  CalculationBaseVar: "CalculationBase", CalculationExtraVar: "CalculationExtra", StarsVar: "Stars",
  ForgeVar: "Forge", SummonVar: "Summon", OstyDamageVar: "OstyDamage", HitCountVar: "HitCount", IntVar: "Int",
};

export function parseCard(source) {
  const ctor = source.match(/:\s*base\((-?\d+),\s*CardType\.(\w+),\s*CardRarity\.(\w+),\s*TargetType\.(\w+)\)/);
  if (!ctor) return null;
  const vars = {};
  const varsBlock = source.match(/CanonicalVars\s*=>([\s\S]*?);\s*\n/);
  if (varsBlock) {
    for (const m of varsBlock[1].matchAll(/new\s+(\w+)(?:<(\w+)>)?\(([^()]*(?:\([^()]*\)[^()]*)*)\)/g)) {
      const [, cls, generic, args] = m;
      const first = args.split(",")[0].trim();
      const named = args.match(/^\s*"(\w+)"\s*,\s*(-?[\d.]+m?)/); // any var class given an explicit key
      if (named) vars[named[1]] = num(named[2]);
      else if (cls === "DynamicVar") { const n = args.match(/"(\w+)"\s*,\s*(-?[\d.]+m?)/); if (n) vars[n[1]] = num(n[2]); }
      else if (cls === "PowerVar" && generic && /^-?[\d.]+m?$/.test(first)) vars[generic] = num(first);
      else if (VAR_NAMES[cls] && /^-?[\d.]+m?$/.test(first)) vars[VAR_NAMES[cls]] = num(first);
    }
  }
  const list = re => [...source.matchAll(re)].map(m => m[1]);
  const kwBlock = source.match(/CanonicalKeywords\s*=>([\s\S]*?);\s*\n/);
  const tagBlock = source.match(/CanonicalTags\s*=>([\s\S]*?);\s*\n/);
  const upgrade = source.match(/void OnUpgrade\(\)\s*\{([\s\S]*?)\n\t\}/);
  const up = { vars: {}, cost: 0, add: [], remove: [] };
  if (upgrade) {
    const body = upgrade[1];
    for (const m of body.matchAll(/DynamicVars(?:\.(\w+)|\["(\w+)"\])\.UpgradeValueBy\((-?[\d.]+)m?\)/g)) {
      const key = m[1] ?? m[2];
      const name = key in vars ? key : (key + "Power") in vars ? key + "Power" : key;
      up.vars[name] = (up.vars[name] ?? 0) + num(m[3]);
    }
    for (const m of body.matchAll(/EnergyCost\.UpgradeBy\((-?\d+)\)/g)) up.cost += num(m[1]);
    up.add = [...body.matchAll(/AddKeyword\(CardKeyword\.(\w+)\)/g)].map(m => m[1]);
    up.remove = [...body.matchAll(/RemoveKeyword\(CardKeyword\.(\w+)\)/g)].map(m => m[1]);
  }
  const maxUp = source.match(/MaxUpgradeLevel\s*=>\s*(\d+)/);
  return {
    cost: num(ctor[1]), type: ctor[2], rarity: ctor[3], target: ctor[4],
    keywords: kwBlock ? [...kwBlock[1].matchAll(/CardKeyword\.(\w+)/g)].map(m => m[1]) : [],
    tags: tagBlock ? [...tagBlock[1].matchAll(/CardTag\.(\w+)/g)].map(m => m[1]) : [],
    vars, upgrade: up, maxUpgrade: maxUp ? Number(maxUp[1]) : 1,
  };
}

if (dir) {
  const files = only.length ? only.map(c => c + ".cs") : fs.readdirSync(cardsDir).filter(f => f.endsWith(".cs"));
  const result = {};
  for (const file of files) {
    const full = path.join(cardsDir, file);
    if (!fs.existsSync(full)) continue;
    const card = parseCard(fs.readFileSync(full, "utf8"));
    if (card) result[snake(path.basename(file, ".cs"))] = card;
  }
  fs.writeFileSync(out, JSON.stringify(result, null, 1) + "\n");
  console.log(`${Object.keys(result).length} cards`);
}
