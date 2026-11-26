// node run.js [--until-act=N] [--min-hp=N] — plays the current run: combat, rewards, map, events, rest sites, shops.
// Reading and choosing use the reader (:8765) and the page's policies; clicking uses the in-game mod (:8766).
// Stops (and says why) on anything it does not understand, so a person can take over.
const path = require("path");
const root = path.join(__dirname, "..", "..");
require(path.join(root, "reward-policy.js")); require(path.join(root, "event-policy.js"));
const planMap = require(path.join(root, "map-policy.js"));
const planEvent = (...a) => globalThis.planEvent(...a);
const eventTable = require(path.join(root, "reader", "data", "events.json"));
const READER = "http://127.0.0.1:8765", MOD = "http://127.0.0.1:8766";
const arg = (k, d) => Number((process.argv.find(a => a.startsWith(`--${k}=`)) || `--${k}=${d}`).split("=")[1]);
const untilAct = arg("until-act", 99), solveBudget = arg("budget", 3), hardBudget = arg("hard-budget", 15);
const sleep = ms => new Promise(r => setTimeout(r, ms));
const H = { "X-STS2-Capture": "1", "X-STS2-Exec": "1" };
const get = async (base, p, post = false) => (await fetch(base + p, { method: post ? "POST" : "GET", headers: H })).json();
const ui = () => get(MOD, "/ui");
const click = i => get(MOD, `/click?i=${i}`, true);
const log = (...a) => console.log(new Date().toLocaleTimeString(), ...a);

// ---- helpers -------------------------------------------------------------
const snake = n => n.replace(/([a-z0-9])([A-Z])/g, "$1_$2").toUpperCase();
async function clickWhere(pred, what) {
  for (let t = 0; t < 12; t++) {
    const u = await ui();
    const hit = (u.items || []).find(pred);
    if (hit) { await click(hit.i); log("click", what); await sleep(900); return true; }
    await sleep(500);
  }
  return false;
}
async function inCombat() { const s = await get(MOD, "/state").catch(() => ({ ok: false })); return !!s.ok && s.hand && (await get(READER, "/capture").catch(() => ({ error: 1 }))).error === undefined; }

// ---- combat (from autoplay.js) ------------------------------------------
async function playCombat() {
  let failures = 0;
  for (;;) {
    const state = await get(MOD, "/state");
    if (!state.ok) return;
    const solve = b => get(READER, `/solve?budget=${b}`);
    let solved = await solve(solveBudget);
    if (solved.error && /請等/.test(solved.error) && await mulligan()) solved = await solve(solveBudget);
    for (let i = 0; solved.error && /結算|請等|snapshot|1450/.test(solved.error) && i < 90; i++) { await sleep(800); if (/請等/.test(solved.error) && i % 3 === 2) await mulligan(); solved = await solve(solveBudget); }   // an enemy turn or long animation can take a minute
    // A short search is enough while the fight is comfortable; search longer when it predicts a loss or a close call.
    if (!solved.error && (!solved.victory || solved.finalHp < 0.35 * solved.maxHp)) { log(`  hard turn: re-solving with ${hardBudget}s`); solved = await solve(hardBudget); }
    if (solved.error) {
      if (/進入一場戰鬥/.test(solved.error)) return;
      // Content the solver does not model: play a crude turn instead of stopping the run.
      if (!/結算|請等|snapshot|1450|reader down|fetch failed/.test(solved.error) && !process.argv.includes("--no-fallback")) {
        log("FALLBACK (solver cannot handle this state):", solved.error);
        await greedyTurn(state);
        await waitNextTurn(state);
        continue;
      }
      if (/請等/.test(solved.error)) { await sleep(2000); continue; }   // still not the player's turn: look again
      throw new Error("solve: " + solved.error);
    }
    if (state.turn === 1 && !solved.victory && solved.finalHp <= 0 && !process.argv.includes("--fight-on")) {
      const e = new Error(`PAUSED: predicted loss at the start of this fight (final HP ${solved.finalHp}/${solved.maxHp}); take over, or rerun with --fight-on`);
      e.pause = true; throw e;
    }
    log(`combat turn ${state.turn}: predicted victory=${solved.victory} finalHp=${solved.finalHp}/${solved.maxHp} (${solved.seconds?.toFixed?.(1)}s)`);
    let ended = false, failed = false;
    for (const step of solved.route) {
      let r;
      if (step.kind === "end") { await playExtras(); r = await get(MOD, "/end", true); ended = true; }
      else if (step.kind === "play") {
        const t = step.target ? step.target.split("#")[1] : "";
        r = await get(MOD, `/play?card=${step.card}&up=${step.upgrade ?? 0}${t ? "&target=" + t : ""}${step.targetCombatId != null ? "&cid=" + step.targetCombatId : ""}`, true);
        if (r.pending) r = await handleChoice(step);
      } else if (step.kind === "potion") {
        const t = step.potionTarget ? step.potionTarget.split("#")[1] : "";
        r = await get(MOD, `/potion?slot=${step.potionSlot}${t ? "&target=" + t : ""}${step.targetCombatId != null ? "&cid=" + step.targetCombatId : ""}`, true);
        if (r.pending) r = await handleChoice(step);
      } else throw new Error("unsupported route step " + step.kind);
      if (r.ok) {   // a played card or potion may open a selection screen (Dredge, colorless potion, ...)
        const u = await ui().catch(() => ({}));
        if (/Select|ChooseA/.test(u.scope || "") && !/Reward/.test(u.scope || "")) r = await handleChoice(step);
      }
      if (!r.ok) { log(" step failed:", step.kind, step.card || step.potion, JSON.stringify(r)); failed = true; break; }
      if (ended) break;
      await sleep(120);
    }
    if (failed) { if (++failures >= 3) throw new Error("3 failed attempts in a row"); continue; }
    failures = 0;
    for (let i = 0; i < 160; i++) {          // wait for the next player turn or the end of combat
      await sleep(400);
      const s = await get(MOD, "/state").catch(() => ({ ok: false }));
      if (!s.ok) return;
      if (s.turn > state.turn) { await sleep(700); break; }
      if (i % 5 === 4) await mulligan();   // an enemy effect can open a choose-a-card screen mid-turn
      const cap = await get(READER, "/capture").catch(() => ({}));
      if (cap.error && /進入一場戰鬥/.test(cap.error)) return;
    }
  }
}

// A relic can open "choose any number of cards to replace" on the hand at combat start: swap the basic Strikes/Defends, then confirm.
async function mulligan() {
  const u = await ui().catch(() => ({}));
  if (u.room !== "Monster" && u.room !== "Elite" && u.room !== "Boss") return false;
  const items = u.items || [];
  // A choose-a-card screen the fight itself opens (boss start: pick one of two debuff cards): take the first.
  if (/ChooseACard|CardSelect/.test(u.scope || "") && items.some(i => i.type === "card")) { await handleChoice({}); return true; }
  if (!items.some(i => i.type === "NConfirmButton") || !items.some(i => i.type === "card")) return false;
  const basics = items.filter(i => i.type === "card" && /^(STRIKE|DEFEND)_/.test(i.id || ""));
  for (const c of basics) { await click(c.i); await sleep(250); }
  log("mulligan: replaced", basics.map(c => c.id).join(",") || "nothing");
  await clickWhere(i => i.type === "NConfirmButton", "confirm");
  await sleep(2500);
  return true;
}

// 0.114 cards the kernel cannot model (importer LiveState.LeftOut) are left out of the solved state; spend what energy is left on them.
async function playExtras() {
  for (let pass = 0; pass < 6; pass++) {
    const st = await get(MOD, "/state").catch(() => ({ ok: false }));
    if (!st.ok) return;
    const enemy = (st.enemies || []).map((e, i) => ({ ...e, n: i + 1 })).filter(e => e.alive).sort((a, b) => a.hp - b.hp)[0];
    const sad = st.hand.some(c => c.id === "SADNESS");
    const pick = st.hand.find(c => c.id === "TWIN_SHOT" && enemy) || (sad && st.hand.find(c => c.id === "OPTIMISM"));
    if (!pick) return;
    const r = await get(MOD, `/play?card=${pick.id}&up=${pick.up}${pick.id === "TWIN_SHOT" ? "&target=" + enemy.n : ""}`, true);
    log(" extra", pick.id, JSON.stringify(r));
    if (!r.ok) return;
    await sleep(300);
  }
}

// Crude fallback turn: try every card in hand (targeting the first living enemy, else untargeted), then end the turn.
async function greedyTurn(state0) {
  for (let pass = 0; pass < 12; pass++) {
    const st = await get(MOD, "/state");
    if (!st.ok) return;
    const enemy = (st.enemies || []).findIndex(e => e.alive) + 1;
    let played = false;
    // Attacks first: against thieves (SWIPE) a fast kill matters most, and a long hand gets stolen from.
    const order = { Attack: 0, Skill: 1, Power: 2 };
    for (const c of [...st.hand].sort((a, b) => (order[a.type] ?? 3) - (order[b.type] ?? 3))) {
      let r = enemy ? await get(MOD, `/play?card=${c.id}&up=${c.up}&target=${enemy}`, true) : { ok: false };
      if (!r.ok) r = await get(MOD, `/play?card=${c.id}&up=${c.up}`, true);
      if (r.ok) {
        const u = await ui().catch(() => ({}));
        if (/Select|ChooseA/.test(u.scope || "") && !/Reward/.test(u.scope || "")) await handleChoice({});
        played = true; await sleep(250); break;
      }
    }
    if (!played) break;
  }
  await get(MOD, "/end", true);
}

async function waitNextTurn(state) {
  for (let i = 0; i < 160; i++) {
    await sleep(400);
    const s = await get(MOD, "/state").catch(() => ({ ok: false }));
    if (!s.ok) return;
    if (s.turn > state.turn) { await sleep(700); return; }
  }
}

// A card played into an in-combat selection (Dredge, Soul choices, ...): pick the route's named cards, then confirm.
async function handleChoice(step) {
  const wanted = (step.choices || []).map(n => n.replace(/\+$/, ""));
  await sleep(600);
  for (const name of wanted) {
    const ok = await clickWhere(i => i.type === "card" && snake(i.id || "") === snake(name).replace(/ /g, "_") || (i.id || "").toUpperCase() === name.toUpperCase(), "choice " + name);
    if (!ok) log("  could not find choice", name);
  }
  const u = await ui();
  if (!wanted.length && (u.items || []).some(i => i.type === "card")) {   // a generated-card pick the route does not name: take the first
    const cards = u.items.filter(i => i.type === "card");
    const first = cards.find(i => i.id !== "DISINTEGRATION") || cards[0];   // Disintegration costs HP every turn: avoid it when there is another option
    await click(first.i); log("choice (first card)", first.id); await sleep(600);
  }
  await clickWhere(i => i.type === "NConfirmButton", "confirm");
  await sleep(800);
  return { ok: true };
}

// ---- other screens -------------------------------------------------------
async function rewards() {
  // Claim gold/relics first, then potions (each at most once, since a full belt leaves the reward in place),
  // the card reward last (its screen replaces the list).
  let potionTries = ((await ui()).items || []).filter(i => i.kind === "PotionReward").length;
  for (let n = 0; n < 14; n++) {
    const u = await ui();
    if (!/Reward/.test(u.scope || "")) return;
    const items = u.items || [];
    const pick = items.find(i => i.type === "reward" && i.enabled && !/Card/.test(i.kind || "") && i.kind !== "PotionReward")
      || (potionTries > 0 && items.find(i => i.type === "reward" && i.enabled && i.kind === "PotionReward"))
      || items.find(i => i.type === "reward" && i.enabled && /Card/.test(i.kind || ""));
    if (!pick) break;
    if (pick.kind === "PotionReward") potionTries--;
    await click(pick.i); log("reward", pick.kind); await sleep(900);
    if (/Card/.test(pick.kind || "")) await cardReward();
  }
  await clickWhere(i => i.type === "NProceedButton", "proceed");
}

async function cardReward() {
  await sleep(700);
  const r = await get(READER, "/rewards");
  if (r.error) { log("rewards read failed:", r.error); return; }
  const ranked = globalThis.planRewards(r);
  const picks = r.remove
    ? [...ranked.filter(c => c.pool === "CurseCardPool"), ...ranked.filter(c => c.pool !== "CurseCardPool" && c.evaluated).sort((a, b) => a.score - b.score)]
    : ranked.filter(c => c.evaluated && c.score > 0 && String(c.kind).toLowerCase() === "card");
  const take = picks.slice(0, r.pick || 1);
  if (!take.length) {
    log("card reward: skip");
    if (!(await clickWhere(i => /Skip|Alternative/.test(i.type), "skip"))) log("  no skip button found");
    return;
  }
  for (const c of take) {
    const id = snake(c.name);
    await clickWhere(i => i.type === "card" && (i.id || "").toUpperCase() === id, "card " + c.name);
  }
  if (take.length > 1 || r.remove || (r.pick || 1) > 1) await clickWhere(i => i.type === "NConfirmButton", "confirm");
}

async function map() {
  const m = await get(READER, "/map");
  if (m.error) throw new Error("map: " + m.error);
  const plan = planMap(m);
  const next = plan.paths?.[0]?.nodes?.[0];
  if (!next) throw new Error("map: no route");
  const [row, col] = next.split(":").map(Number);
  let ok = await clickWhere(i => i.type === "map" && i.row === row && i.col === col && i.enabled, `map ${next}`);
  // At an act's start only the opening (ancient) node is travelable; the planned node unlocks after it.
  if (!ok) ok = await clickWhere(i => i.type === "map" && i.row === 0 && i.enabled, "map act start node");
  if (!ok) throw new Error("map: node not clickable " + next);
  // A second click while the room loads enters it twice (treasure room: "relic picking session already occurring" hangs the game).
  for (let i = 0; i < 25; i++) { await sleep(1000); const u = await ui().catch(() => ({})); if (u.scope && u.scope !== "NMapScreen") break; }
}

async function event() {
  const e = await get(READER, "/event");
  if (e.error) {  // after the last page only a "continue" button is left and the reader has nothing to rank
    return await clickWhere(i => i.type === "event" && !i.locked, "event continue");
  }
  const plan = planEvent({ ...e, name: x => x }, eventTable);
  const best = plan.ranked?.[plan.best ?? 0] ?? plan.ranked?.[0];
  if (!best) return false;
  const ok = await clickWhere(i => i.type === "event" && i.key === best.textKey && !i.locked, "event " + best.textKey);
  if (!ok) await clickWhere(i => i.type === "event" && !i.locked, "event first option");
  return true;
}

async function rest() {
  const r = await get(READER, "/rewards");
  if (r.error || !r.restSite) return false;
  const id = r.recommendation === "rest" ? "HEAL" : "SMITH";
  await clickWhere(i => i.type === "rest" && i.enabled && (i.id || "").toUpperCase().includes(id), "rest option " + id);
  if (id === "SMITH") {
    await sleep(900);
    await pickUpgrade(r.upgrades?.[0]);
  }
  await sleep(800);
  await clickWhere(i => i.type === "NProceedButton", "proceed");
  return true;
}

// On the deck's upgrade screen: the reader's best upgrade if it has one, else the first non-basic card that can still be upgraded.
async function pickUpgrade(up) {
  const u = await ui();
  const cards = (u.items || []).filter(i => i.type === "card");
  const want = up && snake(up.name);
  const pick = cards.find(i => want && (i.id || "").toUpperCase() === want && !i.up)
    || cards.find(i => !i.up && !/^(STRIKE|DEFEND)_/.test(i.id || "")) || cards.find(i => !i.up) || cards[0];
  if (!pick) return;
  await click(pick.i); log("upgrade", pick.id); await sleep(500);
  await clickWhere(i => i.type === "NConfirmButton", "confirm");
}

// "Choose N cards to enchant": N distinct non-basic cards (the enchant prompt reads "選擇 3 張牌"), then confirm.
async function pickEnchant(n) {
  const u = await ui();
  const cards = (u.items || []).filter(i => i.type === "card");
  const picks = [];
  for (const c of cards.filter(c => !/^(STRIKE|DEFEND)_/.test(c.id || "")).concat(cards)) {
    if (picks.length >= n) break;
    if (!picks.includes(c) && !picks.some(p => p.id === c.id && cards.some(o => !picks.includes(o) && o.id !== c.id))) picks.push(c);
  }
  for (const c of picks) { await click(c.i); await sleep(300); }
  log("enchant", picks.map(c => c.id).join(","));
  await clickWhere(i => i.type === "NConfirmButton", "confirm");
  // The preview screen stacks a second confirm on top of the picker's; the topmost is the last in the list.
  await sleep(800);
  const after = ((await ui()).items || []).filter(i => i.type === "NConfirmButton");
  if (after.length > 1) { await click(after[after.length - 1].i); log("click confirm (preview)"); await sleep(900); }
}

async function shop() {
  for (let n = 0; n < 14; n++) {
    let r = await get(READER, "/rewards");
    if ((r.error || !r.shop) && n === 0) {   // the merchant slots are visible in the room before the inventory is open; the reader only reads the open inventory
      log("opening shop inventory"); await get(MOD, "/openshop", true); await sleep(1500);
      r = await get(READER, "/rewards");
    }
    if (r.error || !r.shop) { log("shop: reader has no shop data:", r.error || Object.keys(r).join(",")); break; }
    const ranked = globalThis.planRewards(r);
    const best = ranked.find(c => c.evaluated && c.score > 5 && c.affordable !== false && String(c.kind).toLowerCase() !== "skip");
    if (!best) { log("shop: nothing worth buying; top:", ranked.slice(0, 3).map(c => `${c.name}=${c.score?.toFixed?.(1)}${c.affordable === false ? "(no gold)" : ""}`).join(", "), `gold ${r.gold}`); break; }
    const u = await ui();
    let target;
    if (best.kind === "remove") target = (u.items || []).find(i => i.type === "removal");
    else target = (u.items || []).find(i => i.type === "shop" && i.stocked && i.enabled && (i.entry || "").toLowerCase().endsWith(":" + snake(best.name).toLowerCase()));
    if (!target) { log("shop: cannot find", best.name, "in", JSON.stringify((u.items || []).filter(i => i.type === "shop").map(i => i.entry))); break; }
    await click(target.i); log("shop buy", best.name, best.price); await sleep(1200);
    if (best.kind === "remove") { await cardReward(); await sleep(800); }
  }
  await clickWhere(i => i.type === "NBackButton", "close inventory");
  await clickWhere(i => i.type === "NProceedButton", "leave shop");
}

// ---- main loop -----------------------------------------------------------
async function main() {
  let idle = 0, shopDone = false, chestOpened = false;
  for (;;) {
    const s = await get(MOD, "/state").catch(() => ({ ok: false }));
    const cap = await get(READER, "/capture").catch(() => ({ error: "reader down" }));
    if (s.ok && !cap.error) { idle = 0; await playCombat(); continue; }
    const u = await ui().catch(() => ({ items: [] }));
    const scope = u.scope || "";
    const types = new Set((u.items || []).map(i => i.type));
    let handled = false;
    if (/RewardsScreen/.test(scope)) { await rewards(); handled = true; }
    else if (/DeckEnchantSelect/.test(scope)) { await pickEnchant(3); handled = true; }
    else if (/DeckUpgradeSelect/.test(scope)) { await pickUpgrade(); handled = true; }
    else if (s.ok && /ChooseACard|CardSelect/.test(scope) && /Monster|Elite|Boss/.test(u.room || "")) { await handleChoice({}); handled = true; }   // opened by the fight itself
    else if (/CardReward|SimpleCardSelect|DeckCardSelect|ChooseACard/.test(scope)) { await cardReward(); handled = true; }
    else if (types.has("map")) { shopDone = false; chestOpened = false; await map(); handled = true; }
    else if (types.has("event")) { handled = await event(); }
    else if (types.has("rest")) { handled = await rest(); }
    else if (types.has("shop") || types.has("removal")) { await shop(); shopDone = true; handled = true; }
    else if (u.room === "Shop" && !shopDone) { log("opening shop"); await get(MOD, "/openshop", true); await sleep(1200); handled = true; }
    else if (types.has("chest") || types.has("relic")) { if (!(await clickWhere(i => i.type === "relic" && i.enabled, "relic"))) {
        if (!chestOpened) { chestOpened = true; await clickWhere(i => i.type === "chest", "chest"); }   // the chest node stays listed after it opens
        else await clickWhere(i => i.type === "NProceedButton", "proceed");
      } handled = true; }
    else if (types.has("NProceedButton")) { await clickWhere(i => i.type === "NProceedButton", "proceed"); handled = true; }
    if (handled) { idle = 0; await sleep(1200); continue; }
    if (++idle > 8) throw new Error(`nothing to do on screen "${scope}" with items ${JSON.stringify([...types])}`);
    await sleep(1500);
  }
}
main().catch(e => { console.error(e.pause ? e.message : "stopped: " + e.message); process.exit(e.pause ? 3 : 1); });
