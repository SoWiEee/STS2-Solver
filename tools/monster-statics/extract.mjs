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
  result[snake(type.name)] = getters;
}
}
fs.writeFileSync(output, JSON.stringify(result, null, 1) + "\n");
console.log(Object.entries(result).map(([k, v]) => `${k}: ${Object.keys(v).length}`).join("\n"));
