// node autoplay.js — plays the current combat: solve (reader :8765) then execute through the mod (:8766).
// Each turn: take the route's first turn, run its steps, end turn; re-solve whenever a step fails or the
// state differs. Stops on: combat over, a pending in-game choice, repeated failure, or HP below --min-hp.
const READER = "http://127.0.0.1:8765", MOD = "http://127.0.0.1:8766";
const minHp = Number((process.argv.find(a => a.startsWith("--min-hp=")) || "--min-hp=0").split("=")[1]);
const sleep = ms => new Promise(r => setTimeout(r, ms));
const get = async (base, path, post = false) =>
  (await fetch(base + path, { method: post ? "POST" : "GET", headers: { "X-STS2-Capture": "1", "X-STS2-Exec": "1" } })).json();
const exec = (path) => get(MOD, path, true);

async function waitForPlayerTurn(afterTurn) {
  for (let i = 0; i < 120; i++) {
    await sleep(750);
    const s = await get(MOD, "/state").catch(() => ({ ok: false }));
    if (!s.ok) return s;               // not in combat any more
    if (s.turn > afterTurn && s.hand) { await sleep(1500); return s; } // let draw animations settle
  }
  // The mod's state stays readable after the last enemy dies; the reader tells when combat is really over.
  const cap = await get(READER, "/capture").catch(() => ({}));
  if (cap.error) return { ok: false };
  throw new Error("timed out waiting for the next player turn");
}

async function main() {
  let failures = 0;
  for (;;) {
    const state = await get(MOD, "/state");
    if (!state.ok) { console.log("combat over / not in combat:", state.error || ""); return; }
    let solved = await get(READER, "/solve");
    // The reader refuses while the game is still resolving a turn; retry until it is playable.
    for (let i = 0; solved.error && /結算|請等/.test(solved.error) && i < 20; i++) { await sleep(1500); solved = await get(READER, "/solve"); }
    if (solved.error) throw new Error("solve: " + solved.error);
    if (solved.maxHp && minHp && solved.finalHp < minHp) console.log(`warning: predicted final HP ${solved.finalHp} < ${minHp}`);
    console.log(`turn ${state.turn}: predicted victory=${solved.victory} finalHp=${solved.finalHp}/${solved.maxHp}`);
    let ended = false, failed = false;
    for (const step of solved.route) {
      let r;
      if (step.kind === "end") { r = await exec("/end"); ended = true; }
      else if (step.kind === "play") {
        const t = step.target ? step.target.split("#")[1] : "";
        r = await exec(`/play?card=${step.card}&up=${step.upgrade ?? 0}${t ? "&target=" + t : ""}`);
      } else if (step.kind === "potion") {
        const t = step.potionTarget ? step.potionTarget.split("#")[1] : "";
        r = await exec(`/potion?slot=${step.potionSlot}${t ? "&target=" + t : ""}`);
      } else { console.log("unsupported step", step.kind); failed = true; break; }
      console.log(" ", step.kind, step.card || step.potion || "", step.target || "", JSON.stringify(r));
      if (r.pending) { console.log("a card choice is waiting in the game UI; stopping"); return; }
      if (!r.ok) { failed = true; break; }
      if (ended) break;
      await sleep(900);
    }
    if (failed) { if (++failures >= 3) throw new Error("3 failed attempts in a row"); continue; }
    failures = 0;
    if (!ended) { console.log("route ended without an end-turn step"); return; }
    const next = await waitForPlayerTurn(state.turn);
    if (!next.ok) { console.log("combat finished"); return; }
  }
}
main().catch(e => { console.error("stopped:", e.message); process.exit(1); });
