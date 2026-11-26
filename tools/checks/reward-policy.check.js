// node tools/checks/reward-policy.check.js — assert-based self-check of reward-policy.js additions.
const { planRewards } = require("../../reward-policy.js"), assert = require("assert");
const strike = { name: "StrikeX", kind: "card", damage: 6, cost: 1 };
const deck = (n, extra = []) => [...extra, ...Array.from({ length: n - extra.length }, () => strike)];
const score = (input, name) => planRewards(input).find(o => o.name === name).score;
// Shop removal: a curse is worth 7 HP, normalized per 100 gold.
assert.equal(score({ shop: true, gold: 100, deck: deck(10, [{ name: "Writhe", pool: "CurseCardPool" }]),
  options: [{ name: "移除卡牌", kind: "remove", price: 100 }] }, "移除卡牌"), 7 + 0 * 1); // 11 cards: no bloat bonus
// A start-of-turn power counts its energy and draw over three turns.
const power = { name: "Demesne", kind: "card", cost: 1, energyPerTurn: 1, drawPerTurn: 1 };
assert.ok(score({ act: 2, deck: deck(10), options: [power] }, "Demesne") > 3 * 2);
// Past 15 cards each extra card costs 0.25.
const small = score({ act: 2, deck: deck(20), options: [strike] }, "StrikeX");
assert.ok(Math.abs(small - score({ act: 2, deck: deck(30), options: [strike] }, "StrikeX") - 2.5) < 1e-9);
console.log("reward-policy ok");
// A card that adds junk to the hand each play is penalised by 8 per junk card.
const clean = score({ act: 2, deck: deck(12), options: [{ name: "Jest", kind: "card", block: 15, cost: 1 }] }, "Jest");
const junky = score({ act: 2, deck: deck(12), options: [{ name: "Jest", kind: "card", block: 15, cost: 1, junkCards: 1 }] }, "Jest");
assert.ok(Math.abs(clean - junky - 8) < 1e-9);
console.log("reward-policy junk ok");
