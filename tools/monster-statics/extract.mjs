// Extracts constant and ascension-scaled integer getters of monster models from a REA
// `inspect-managed-members` evidence file (CIL anchors), for monsters the rules catalog lacks.
// Usage: node extract.mjs <out.json> <rea-evidence.json>...  (REA limits metadata size, so batches are merged)
// Recognized getter bodies:
//   ldc.i4 K; ret                                   -> { value: K }
//   ldc.i4 L; ldc.i4 H; ldc.i4 B; call GetValueIfAscension; ret -> { ascension: L, high: H, low: B }
import fs from "node:fs";
const [, , output, ...inputs] = process.argv;
const ASCENSION_HELPER = "0x060053e9"; // AscensionHelper.GetValueIfAscension(level, high, low) in v0.113.1 (d09d48e5)
const ldc = a => {
  if (a.opcode === "ldc.i4" || a.opcode === "ldc.i4.s") return Number(a.operand);
  const m = /^ldc\.i4\.(m1|\d)$/.exec(a.opcode);
  return m ? (m[1] === "m1" ? -1 : Number(m[1])) : null;
};
const snake = s => s.replace(/([a-z0-9])([A-Z])/g, "$1_$2").toUpperCase();
const result = {};
for (const input of inputs) {
const nr = JSON.parse(fs.readFileSync(input, "utf8")).records[0].normalized_result;
for (const type of nr.types) {
  const getters = {};
  for (const m of nr.methods.filter(m => m.declaring_type_token === type.token && m.name.startsWith("get_"))) {
    const a = (m.body?.anchors ?? []);
    const n = a.map(ldc);
    if (a.length === 2 && n[0] !== null && a[1].opcode === "ret") getters[m.name.slice(4)] = { value: n[0] };
    else if (a.length === 5 && n[0] !== null && n[1] !== null && n[2] !== null && a[3].opcode === "call"
      && a[3].operand === ASCENSION_HELPER && a[4].opcode === "ret")
      getters[m.name.slice(4)] = { ascension: n[0], high: n[1], low: n[2] };
  }
  // const int fields (e.g. DevotedSculptor._ritualGain) are compiled away; REA reads them from the Constant table.
  for (const f of nr.fields.filter(f => f.declaring_type_token === type.token && f.constant?.state === "decoded" && f.constant.element_type === "i4"))
    getters[f.name] ??= { value: f.constant.value };
  result[snake(type.name)] = getters;
}
}
// Getters of the form GetValueIfAscension(L, H, B) + (IsDread() ? k : 0) do not match the CIL patterns
// above; transcribed from the 0.113.1 decompilation. "dread" is added in Dread mode (LiveRules).
const DREAD = [
  ["AEONGLASS", "WitherAmount", 9, 2, 1, 1], ["CEREMONIAL_BEAST", "MinInitialHp", 8, 262, 252, 30],
  ["LAGAVULIN_MATRIARCH", "MinInitialHp", 8, 233, 222, 22], ["LAGAVULIN_MATRIARCH", "Slash2Damage", 9, 14, 12, 2],
  ["LAGAVULIN_MATRIARCH", "Slash2Block", 8, 14, 12, 2], ["ROCKET", "RocketClawDamage", 9, 35, 31, 5],
  ["TEST_SUBJECT", "EnrageAmount", 9, 3, 2, 1], ["TEST_SUBJECT", "BurningGrowlStrengthGain", 9, 3, 2, 3],
  ["THE_INSATIABLE", "MinInitialHp", 8, 341, 321, 20], ["TORCH_HEAD_AMALGAM", "StrongTackleDamage", 9, 32, 26, 5],
  ["VANTOM", "SlipperyAmt", 8, 9, 8, 2],
];
for (const [monster, name, ascension, high, low, dread] of DREAD)
  (result[monster] ??= {})[name] = { ascension, high, low, dread };
// Getters of the form `if (!IsDread()) return a; return b;` (constant outside Dread).
const DREAD_CONST = [
  ["CEREMONIAL_BEAST", "PlowStrength", 2, 3], ["CEREMONIAL_BEAST", "RitualAmount", 0, 2],
  ["CRUSHER", "PlatingAmount", 0, 14], ["LAGAVULIN_MATRIARCH", "DebuffAmount", -2, -3],
  ["ROCKET", "PlatingAmount", 0, 14], ["SOUL_FYSH", "BeckonMoveAmount", 2, 3],
  ["TEST_SUBJECT", "BaseMultiClawCount", 3, 4], ["THE_INSATIABLE", "SandpitAmount", 4, 3],
  ["VANTOM", "PrepareStrength", 2, 3], ["WATERFALL_GIANT", "SteamBoostAmount", 3, 4],
];
for (const [monster, name, value, inDread] of DREAD_CONST)
  (result[monster] ??= {})[name] = { value, dread: inDread - value };
fs.writeFileSync(output, JSON.stringify(result, null, 1) + "\n");
console.log(Object.entries(result).map(([k, v]) => `${k}: ${Object.keys(v).length}`).join("\n"));
