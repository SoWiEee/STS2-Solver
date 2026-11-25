// node tools/checks/event-policy.check.js — assert-based self-check of event-policy.js scoring.
const P = require("../../event-policy.js"), assert = require("assert");
const table = { events: { WELLSPRING: { options: {
  A: { effects: { randomPotions: 1 } },
  B: { effects: { removeCards: 1, curses: [{ id: "GUILTY", count: "$BatheCurses" }] } },
  C: { effects: { hp: "-$Dmg", gold: 100 } },
  D: { outcomes: [{ p: 0.5, effects: { gold: 50 } }, { p: 0.5, effects: { hp: -100 } }] },
  E: { proceed: true },
  F: { effects: { potionSlots: 2, randomPotions: 2 } } } } } };
const r = P.planEvent({ eventId: "WELLSPRING", vars: { BatheCurses: 1, Dmg: 10 }, hp: 60, maxHp: 80, gold: 0,
  deck: [{ name: "StrikeFool" }], potions: ["X"], potionSlots: 3,
  options: ["A", "B", "C", "D", "E", "F"].map(k => ({ textKey: k, mayKill: k === "C" })) }, table);
const s = Object.fromEntries(r.ranked.map(o => [o.textKey, o.score]));
assert.equal(s.A, 3);            // one free potion slot used
assert.equal(s.B, 6 - 7);        // remove a basic card, gain one curse
assert.equal(s.C, -10 + 8);      // 10 HP for 100 gold; a WillKillPlayer check on a survivable known loss is not lethal
assert.equal(s.D, -Infinity);    // a possible lethal outcome is excluded
assert.equal(s.E, 0);
assert.equal(s.F, 2 * 2 + 2 * 3); // the two new slots hold the two potions
assert.equal(P.resolve("-$Dmg*2", { Dmg: 3 }), -6);
assert.equal(r.best, 5);         // F (10) beats A (3)
console.log("event-policy ok");
