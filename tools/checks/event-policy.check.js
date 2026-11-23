// node tools/checks/event-policy.check.js — assert-based self-check of event-policy.js scoring.
const P = require("../../event-policy.js"), assert = require("assert");
const table = { events: { WELLSPRING: { options: {
  A: { effects: { randomPotions: 1 } },
  B: { effects: { removeCards: 1, curses: [{ id: "GUILTY", count: "$BatheCurses" }] } },
  C: { effects: { hp: "-$Dmg", gold: 100 } },
  D: { outcomes: [{ p: 0.5, effects: { gold: 50 } }, { p: 0.5, effects: { hp: -100 } }] },
  E: { proceed: true } } } } };
const r = P.planEvent({ eventId: "WELLSPRING", vars: { BatheCurses: 1, Dmg: 10 }, hp: 60, maxHp: 80, gold: 0,
  deck: [{ name: "StrikeFool" }], potions: ["X", null, null],
  options: ["A", "B", "C", "D", "E"].map(k => ({ textKey: k })) }, table);
const s = Object.fromEntries(r.ranked.map(o => [o.textKey, o.score]));
assert.equal(s.A, 3);            // one free potion slot used
assert.equal(s.B, 6 - 7);        // remove a basic card, gain one curse
assert.equal(s.C, -10 + 8);      // 10 HP for 100 gold
assert.equal(s.D, -Infinity);    // a possible lethal outcome is excluded
assert.equal(s.E, 0);
assert.equal(P.resolve("-$Dmg*2", { Dmg: 3 }), -6);
assert.equal(r.best, 0);
console.log("event-policy ok");
