/*
 * Conservative reward ranking for the Windows reader's normalized options.
 * Only supplied numerical fields are valued. Text-only effects stay unmodeled.
 * Global and CommonJS exports are both provided for browser and Node use.
 */
(function (root) {
  "use strict";

  function number(value, fallback) {
    const n = Number(value);
    return Number.isFinite(n) ? n : fallback;
  }

  function cleanName(value) {
    return String(value == null ? "Unknown option" : value).trim() || "Unknown option";
  }

  function isSkip(option) {
    return String(option.kind || "").toLowerCase() === "skip"
      || /^(skip|skip reward|略過|跳過)$/i.test(cleanName(option.name));
  }

  function planRewards(input) {
    input = input || {};
    const deck = Array.isArray(input.deck) ? input.deck : [];
    const options = Array.isArray(input.options) ? input.options : [];
    const act = Math.max(1, Math.floor(number(input.act, 1)));
    const hp = Math.max(0, number(input.hp, 0));
    const maxHp = Math.max(1, number(input.maxHp, 1));
    const hpRatio = Math.min(1, hp / maxHp);

    // A short deck needs more playable actions. Sparse numerical damage/block
    // also marks the corresponding role as a deficiency; no card text inference.
    let deckDamage = 0, deckBlock = 0, deckDraw = 0, measured = 0;
    for (const card of deck) {
      if (!card || String(card.kind || "card").toLowerCase() !== "card") continue;
      const damage = Math.max(0, number(card.damage, 0));
      const block = Math.max(0, number(card.block, 0));
      const draw = Math.max(0, number(card.draw, 0));
      deckDamage += damage;
      deckBlock += block;
      deckDraw += draw;
      if (card.damage != null || card.block != null || card.draw != null) measured++;
    }
    const count = deck.length;
    const avgDamage = measured ? deckDamage / measured : 0;
    const avgBlock = measured ? deckBlock / measured : 0;
    const avgDraw = measured ? deckDraw / measured : 0;
    const missingDamage = measured ? Math.max(0, 6 - avgDamage) / 6 : 0.5;
    const missingBlock = measured ? Math.max(0, 4 - avgBlock) / 4 : 0.5;
    const missingDraw = measured ? Math.max(0, 0.8 - avgDraw) / 0.8 : 0.5;
    const smallDeckBonus = Math.max(0, 15 - count) * 0.12;

    const ranked = options.map((option, index) => {
      option = option || {};
      const name = cleanName(option.name);
      const kind = String(option.kind || "unknown").toLowerCase();
      const reasons = [];
      let score = 0;
      if (isSkip(option)) {
        reasons.push("略過選項：基準分 0。");
        return { ...option, name, score, evaluated: true, reasons, originalIndex: index };
      }

      const hasNumbers = ["damage", "block", "draw", "combatStartHeal"].some(key => option[key] != null);
      const evaluated = hasNumbers && (kind === "card" || kind === "relic");
      if (evaluated) {
        const damage = Math.max(0, number(option.damage, 0));
        const block = Math.max(0, number(option.block, 0));
        const draw = Math.max(0, number(option.draw, 0));
        const heal = Math.max(0, number(option.combatStartHeal, 0));
        const cost = Math.max(0, number(option.cost, 1));
        // Weighted utility is deliberately limited to normalized numerical data.
        const damageValue = damage * (0.55 + 0.35 * missingDamage);
        const blockValue = block * (0.38 + 0.35 * missingBlock) * (hpRatio < 0.45 ? 1.35 : 1);
        const drawValue = draw * (4 + 2 * missingDraw);
        const costPenalty = kind === "card" ? Math.max(0, cost - 1) * 0.8 : 0;
        score = damageValue + blockValue + drawValue + heal * (hpRatio < 0.45 ? 1.8 : 1.2) - costPenalty;
        if (damage > 0) reasons.push(`已知傷害 ${damage}，依牌組傷害缺口計分。`);
        if (block > 0) reasons.push(`已知格擋 ${block}${hpRatio < 0.45 ? "；目前生命比例偏低，防禦權重提高" : ""}。`);
        if (draw > 0) reasons.push(`已知抽牌 ${draw}，按每張 4 分並依牌組抽牌缺口調整。`);
        if (heal > 0) reasons.push(`每場戰鬥開始時恢復 ${heal} HP。`);
        if (kind === "card" && cost > 1) reasons.push(`費用 ${cost} 帶來 ${costPenalty.toFixed(1)} 分可玩性折減。`);
        if (!damage && !block && !draw && !heal) reasons.push("沒有可評分的數值效果。");
        if (act <= 1 && kind === "card") {
          const bonus = smallDeckBonus;
          score += bonus;
          if (bonus > 0) reasons.push(`目前牌組 ${count} 張；早期小牌組成長加 ${bonus.toFixed(1)} 分。`);
        }
      } else if (kind === "card" || kind === "relic") {
        reasons.push("效果沒有可用的已知數值；不推測文字效果，依 0 分排序。");
      } else {
        reasons.push("選項類型或效果無法確認；不推測效果，依 0 分排序。");
      }

      return { ...option, name, score, evaluated, reasons, originalIndex: index };
    });

    ranked.sort((a, b) => b.score - a.score || a.originalIndex - b.originalIndex);
    return ranked.map(({ originalIndex, ...entry }, rank) => ({ ...entry, rank: rank + 1 }));
  }

  root.planRewards = planRewards;
  if (typeof module !== "undefined" && module.exports) module.exports = { planRewards };
})(typeof globalThis !== "undefined" ? globalThis : this);
