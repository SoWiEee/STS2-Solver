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
    let deckDamage = 0, deckBlock = 0, deckDraw = 0, measured = 0, attacks = 0, defenses = 0;
    for (const card of deck) {
      if (!card || String(card.kind || "card").toLowerCase() !== "card") continue;
      const damage = Math.max(0, number(card.damage, 0));
      const block = Math.max(0, number(card.block, 0));
      const draw = Math.max(0, number(card.draw, 0) - number(card.drawPutBack, 0));
      deckDamage += damage;
      deckBlock += block;
      deckDraw += draw;
      if (damage > 0) attacks++;
      if (block > 0) defenses++;
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
    const attackDensity = attacks / Math.max(1, count);
    const blockDensity = defenses / Math.max(1, count);
    const hasKnownStars = input.character === "Regent" || deck.some(card => card?.generatesStars);

    const ranked = options.map((option, index) => {
      option = option || {};
      const name = cleanName(option.name);
      const kind = String(option.kind || "unknown").toLowerCase();
      const reasons = [];
      let score = 0;
      if (isSkip(option)) {
        reasons.push(input.shop ? "維持目前牌組並保留金幣。" : "略過選項：基準分 0。");
        return { ...option, name, score, evaluated: true, reasons, originalIndex: index };
      }
      if (kind === "remove") {
        // Same HP-equivalent values as event-policy: a curse costs 7, a basic Strike/Defend removal is worth 6.
        const curse = deck.find(card => card?.pool === "CurseCardPool");
        const basic = deck.some(card => /^(Strike|Defend)[A-Z]/.test(cleanName(card?.name)));
        // Thinning matters more as the deck grows: +0.3 per card past 15.
        score = (curse ? 7 : basic ? 6 : 0) + (curse || basic ? Math.max(0, count - 15) * 0.3 : 0);
        reasons.push(curse ? `移除詛咒「${curse.title || curse.name}」。` : basic ? "移除一張基本打擊或防禦。" : "牌組沒有詛咒或基本牌可移除。");
        const affordable = !input.shop || option.price <= input.gold;
        if (input.shop && affordable) score *= 100 / Math.max(1, option.price);
        if (!affordable) reasons.unshift("按讀取價格估算，金幣不足。");
        return { ...option, name, score, evaluated: score > 0, affordable, reasons, originalIndex: index };
      }
      if (name === "TheGambit") {
        reasons.push("在本場戰鬥受到未格擋的攻擊傷害會立刻死亡，暫不以格擋量評分。");
        return { ...option, name, score, evaluated: false, affordable: !input.shop || option.price <= input.gold,
          reasons, originalIndex: index };
      }

      const hasNumbers = ["damage", "block", "draw", "combatStartHeal", "openingEnergy",
        "openingStrength", "openingDexterity", "openingVigor", "nextTurnEnergy", "blockPerTurn", "energyPerTurn", "drawPerTurn"].some(key => option[key] != null);
      const usesStars = kind === "card" && (number(option.starCost, 0) > 0 || option.starCostsX);
      const evaluated = hasNumbers && (kind === "card" || kind === "relic");
      if (evaluated) {
        const damage = Math.max(0, number(option.damage, 0));
        const block = Math.max(0, number(option.block, 0));
        const draw = Math.max(0, number(option.draw, 0));
        const heal = Math.max(0, number(option.combatStartHeal, 0));
        const energy = Math.max(0, number(option.openingEnergy, 0));
        const nextTurnEnergy = Math.max(0, number(option.nextTurnEnergy, 0));
        const strength = Math.max(0, number(option.openingStrength, 0));
        const dexterity = Math.max(0, number(option.openingDexterity, 0));
        const vigor = Math.max(0, number(option.openingVigor, 0));
        const cost = Math.max(0, number(option.cost, 1));
        const blockPerTurn = Math.max(0, number(option.blockPerTurn, 0));
        // A power repeating every turn: counted over ~3 turns of a fight (shortcut: fight length is not modelled).
        const energyPerTurn = Math.max(0, number(option.energyPerTurn, 0));
        const drawPerTurn = Math.max(0, number(option.drawPerTurn, 0));
        const everyTurnValue = 3 * (energyPerTurn * 2 + drawPerTurn * (4 + 2 * missingDraw));
        // Weighted utility is deliberately limited to normalized numerical data.
        const damageValue = damage * (0.55 + 0.35 * missingDamage);
        const blockValue = block * (0.38 + 0.35 * missingBlock) * (hpRatio < 0.45 ? 1.35 : 1);
        const putBack = Math.max(0, number(option.drawPutBack, 0));
        const drawValue = Math.max(0, draw - putBack) * (4 + 2 * missingDraw);
        const costPenalty = kind === "card" ? Math.max(0, cost - 1) * 0.8 : 0;
        // A card that returns to hand can be replayed each turn: its damage/block counts 50% extra.
        const replay = option.returnsToHand ? 0.5 * (damageValue + blockValue) : 0;
        // Block every turn is worth about one Dexterity on a deck of average block density.
        const perTurnValue = blockPerTurn * (0.38 + 0.35 * missingBlock) * 2.5;
        score = damageValue + blockValue + drawValue + replay + perTurnValue + everyTurnValue + heal * (hpRatio < 0.45 ? 1.8 : 1.2) - costPenalty
          + energy * 3 + nextTurnEnergy * 2 + strength * 6 * attackDensity
          + dexterity * 5 * blockDensity + vigor * 0.7 * Math.min(1, 5 * attackDensity);
        if (damage > 0) reasons.push(`已知傷害 ${damage}，依牌組傷害缺口計分。`);
        if (block > 0) reasons.push(`已知格擋 ${block}${hpRatio < 0.45 ? "；目前生命比例偏低，防禦權重提高" : ""}。`);
        if (draw > 0) reasons.push(putBack > 0
          ? `抽 ${draw} 張、放回 ${putBack} 張，依淨抽牌數估分。`
          : `抽 ${draw} 張，依牌組抽牌缺口估分。`);
        if (replay > 0) reasons.push(`打出後回到手牌，可重複使用，加 ${replay.toFixed(1)} 分。`);
        if (energyPerTurn > 0 || drawPerTurn > 0) reasons.push(`每回合${energyPerTurn > 0 ? ` ${energyPerTurn} 點能量` : ""}${drawPerTurn > 0 ? ` 抽 ${drawPerTurn} 張` : ""}，以約 3 回合估分。`);
        if (blockPerTurn > 0) reasons.push(`每回合多 ${blockPerTurn} 點格擋（伸展），以持續效果估分。`);
        if (heal > 0) reasons.push(`每場戰鬥開始時恢復 ${heal} HP。`);
        if (energy > 0) reasons.push(`首回合額外 ${energy} 點能量。`);
        if (nextTurnEnergy > 0) reasons.push(`下回合額外 ${nextTurnEnergy} 點能量。`);
        if (strength > 0) reasons.push(`開場取得 ${strength} 力量，依牌組的傷害牌比例估分。`);
        if (dexterity > 0) reasons.push(`開場取得 ${dexterity} 敏捷，依牌組的格擋牌比例估分。`);
        if (vigor > 0) reasons.push(`開場取得 ${vigor} 活力，依牌組的傷害牌比例估分。`);
        if (name === "BattleTrance") reasons.push("打出後，本回合無法再抽牌。");
        if (name === "TrueGrit") reasons.push("這張牌可能隨機消耗一張手牌，分數只估格擋。");
        if (kind === "card" && cost > 1) reasons.push(`費用 ${cost} 帶來 ${costPenalty.toFixed(1)} 分可玩性折減。`);
        if (reasons.length === 0) reasons.push("沒有可評分的數值效果。");
        // A card that adds a status/curse each play clogs the hand: about 8 points per junk card added (observed: Jest clogged 2-3 hand slots per fight).
        const junk = Math.max(0, number(option.junkCards, 0));
        if (junk > 0) { score -= 8 * junk; reasons.push(`每次打出會加入 ${junk} 張垃圾牌（佔手牌），扣 ${8 * junk} 分。`); }
        // shortcut: every card past 15 dilutes draws by a flat 0.25 (no draw-pile model); weak cards then fall below skip.
        if (kind === "card" && count > 15) {
          const dilution = (count - 15) * 0.25;
          score -= dilution;
          reasons.push(`目前牌組 ${count} 張；牌組越大越稀釋，扣 ${dilution.toFixed(1)} 分（控制牌數）。`);
        }
        if (act <= 1 && kind === "card") {
          const bonus = smallDeckBonus;
          score += bonus;
          if (bonus > 0) reasons.push(`目前牌組 ${count} 張；早期小牌組成長加 ${bonus.toFixed(1)} 分。`);
        }
      } else if (option.unmodeled) {
        reasons.push(option.unmodeled);
      } else if (kind === "card" || kind === "relic") {
        reasons.push("效果沒有可用的已知數值；不推測文字效果，依 0 分排序。");
      } else {
        reasons.push("選項類型或效果無法確認；不推測效果，依 0 分排序。");
      }

      if (usesStars && !hasKnownStars) {
        score = 0;
        reasons.unshift("需要星能，牌組中尚未辨識到星能源，分數暫以 0 計。");
      } else if (usesStars) {
        if (evaluated) score -= option.starCostsX ? 1 : number(option.starCost, 0);
        reasons.push(option.starCostsX ? "星能費用為 X，依可用星能變動。" : `另需 ${option.starCost} 點星能。`);
      }
      const affordable = !input.shop || option.price <= input.gold;
      if (input.shop && evaluated && affordable) score *= 100 / Math.max(1, option.price);
      if (!affordable) reasons.unshift("按讀取價格估算，金幣不足。");
      return { ...option, name, score, evaluated, affordable, reasons, originalIndex: index };
    });

    ranked.sort((a, b) => Number(b.affordable !== false) - Number(a.affordable !== false)
      || Number(b.evaluated) - Number(a.evaluated) || b.score - a.score || a.originalIndex - b.originalIndex);
    return ranked.map(({ originalIndex, ...entry }, rank) => ({ ...entry, rank: rank + 1 }));
  }

  root.planRewards = planRewards;
  if (typeof module !== "undefined" && module.exports) module.exports = { planRewards };
})(typeof globalThis !== "undefined" ? globalThis : this);
