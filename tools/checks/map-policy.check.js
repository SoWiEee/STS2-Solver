// node tools/checks/map-policy.check.js — assert-based self-check of map-policy.js HP simulation.
const planMap = require("../../map-policy.js"), assert = require("assert");
// Two routes from the start: elite then rest then boss, or rest then elite then boss.
const nodes = [
  { id: "s", type: "monster", children: ["e1", "r1"] },
  { id: "e1", type: "elite", children: ["r2"] }, { id: "r1", type: "rest", children: ["e2"] },
  { id: "r2", type: "rest", children: ["b"] }, { id: "e2", type: "elite", children: ["b"] },
  { id: "b", type: "boss", children: [] }];
const plan = hp => planMap({ hp, maxHp: 100, gold: 0, nodes, startIds: ["s"] });
const first = hp => plan(hp).paths[0].nodes;
// Low HP: rest before the elite; the elite-first route would enter it below 45%.
assert.deepStrictEqual(first(40).slice(0, 3), ["s", "r1", "e2"]);
console.log("map-policy ok");
