/*
 * Event option ranking. Effects come from data/events.json (transcribed from the 0.113.1 event
 * models, keyed by option TextKey); "$Var" values resolve against the live event's DynamicVars,
 * which already include ascension scaling. Every effect is converted to one HP-equivalent score.
 * Global and CommonJS exports are both provided for browser and Node use.
 */
(function (root) {
  "use strict";

  // Approved defaults (HP-equivalent points).
  const VALUE = {
    hp: 1, hpLow: 1.5, maxHp: 1.5, gold: 0.08,
    removeBasic: 6, removeOther: 3, upgrade: 4, transform: 2, duplicate: 3,
    cardChoice: 2, randomCard: 1, card: 2, curse: -7,
    relic: 8, potion: 3, potionSlot: 2, enchant: 3,
    fight: { monster: -8, elite: -15, boss: -30 },
  };

  function resolve(value, vars) {
    if (typeof value === "number") return value;
    if (typeof value !== "string") return 0;
    const m = value.match(/^(-?)\$([A-Za-z0-9_]+)(?:\*(-?[\d.]+))?$/);
    if (!m) return Number(value) || 0;
    const base = Number(vars?.[m[2]] ?? NaN);
    if (!Number.isFinite(base)) return NaN;
    return (m[1] ? -1 : 1) * base * (m[3] ? Number(m[3]) : 1);
  }

  // Scores one deterministic effects object; returns { score, hpAfter, reasons, unknown }.
  function scoreEffects(effects, ctx) {
    const reasons = [];
    let score = 0, unknown = false;
    let hp = ctx.hp, maxHp = ctx.maxHp;
    const v = key => {
      const n = resolve(effects[key], ctx.vars);
      if (Number.isNaN(n)) { unknown = true; return 0; }
      return n;
    };
    const add = (points, text) => { score += points; reasons.push(`${text}（${points >= 0 ? "+" : ""}${points.toFixed(1)}）`); };

    if (effects.maxHp != null) { const n = v("maxHp"); maxHp += n; hp = Math.max(0, hp + Math.max(0, n)); add(n * VALUE.maxHp, `最大生命 ${n >= 0 ? "+" : ""}${n}`); }
    if (effects.hp != null) {
      const n = v("hp");
      const after = Math.min(maxHp, hp + n);
      const weight = after < maxHp * 0.5 ? VALUE.hpLow : VALUE.hp;
      add((after - hp) * weight, `生命 ${n >= 0 ? "+" : ""}${n}`);
      hp = after;
    }
    if (effects.healPct != null) {
      const healed = Math.min(maxHp - hp, Math.floor(maxHp * v("healPct")));
      add(healed * (hp < maxHp * 0.5 ? VALUE.hpLow : VALUE.hp), `回復 ${healed} 生命`);
      hp += healed;
    }
    if (effects.gold != null) { const n = v("gold"); add(n * VALUE.gold, `金幣 ${n >= 0 ? "+" : ""}${n}`); }
    if (effects.loseGold != null) {
      const n = effects.loseGold === "all" ? ctx.gold : v("loseGold");
      add(-n * VALUE.gold, `失去 ${n} 金幣`);
    }
    if (effects.removeCards) {
      const basic = ctx.deck.filter(c => /^(Strike|Defend)/.test(c.name ?? "")).length;
      const n = v("removeCards");
      const pts = Math.min(n, basic) * VALUE.removeBasic + Math.max(0, n - basic) * VALUE.removeOther;
      add(pts, `移除 ${n} 張牌`);
    }
    if (effects.upgradeCards) add(v("upgradeCards") * VALUE.upgrade, `升級 ${v("upgradeCards")} 張牌`);
    if (effects.upgradeRandom) add(v("upgradeRandom") * VALUE.upgrade * 0.75, `隨機升級 ${v("upgradeRandom")} 張`);
    // Fields the transcription added beyond the original schema (enchantments, bulk upgrades, slots).
    if (effects.upgradeAll) add(Math.min(ctx.deck.length, 6) * VALUE.upgrade, "升級整副牌組（以 6 張計）");
    if (effects.upgradeFixed) add(v("upgradeFixed") * VALUE.upgrade * 0.75, `升級指定的 ${v("upgradeFixed")} 張牌`);
    if (effects.downgradeRandom) add(-v("downgradeRandom") * VALUE.upgrade * 0.75, `隨機降級 ${v("downgradeRandom")} 張`);
    if (effects.enchantCards) add(v("enchantCards") * VALUE.enchant, `附魔 ${v("enchantCards")} 張牌${effects.enchantment ? `（${effects.enchantment}）` : ""}`);
    if (effects.enchantRandom) add(v("enchantRandom") * VALUE.enchant * 0.75, `隨機附魔 ${v("enchantRandom")} 張`);
    if (effects.enchantAll) add(Math.min(ctx.deck.length, 6) * VALUE.enchant * 0.5, "附魔多張牌（以 6 張計、效果減半）");
    if (effects.potionSlots) add(v("potionSlots") * VALUE.potionSlot, `藥水欄位 +${v("potionSlots")}`);
    if (effects.transformCards) add(v("transformCards") * VALUE.transform, `變化 ${v("transformCards")} 張牌`);
    if (effects.duplicateCards) add(v("duplicateCards") * VALUE.duplicate, `複製 ${v("duplicateCards")} 張牌`);
    if (effects.cardChoice) add(v("cardChoice") * VALUE.cardChoice, `選 ${v("cardChoice")} 張牌`);
    if (effects.randomCards) add(v("randomCards") * VALUE.randomCard, `獲得 ${v("randomCards")} 張隨機牌`);
    for (const card of effects.cards ?? []) add((resolve(card.count ?? 1, ctx.vars) || 1) * VALUE.card, `獲得 ${ctx.name(card.id)}`);
    for (const curse of effects.curses ?? []) add((resolve(curse.count ?? 1, ctx.vars) || 1) * VALUE.curse, `詛咒 ${ctx.name(curse.id)}`);
    if (effects.randomCurses) add(v("randomCurses") * VALUE.curse, `${v("randomCurses")} 張隨機詛咒`);
    for (const relic of effects.relics ?? []) add(VALUE.relic, `遺物 ${ctx.name(relic)}`);
    if (effects.randomRelics) add(v("randomRelics") * VALUE.relic, `${v("randomRelics")} 個隨機遺物`);
    const freeSlots = ctx.potionSlots + (effects.potionSlots ? v("potionSlots") : 0) - ctx.potions; // slots granted with the potions come first
    const potionCount = (effects.potions?.length ?? 0) + (effects.randomPotions ? v("randomPotions") : 0);
    if (potionCount) add(Math.min(potionCount, Math.max(0, freeSlots)) * VALUE.potion, `${potionCount} 瓶藥水${freeSlots < potionCount ? "（欄位不足）" : ""}`);
    if (effects.loseRelic) add(-(typeof effects.loseRelic === "number" ? effects.loseRelic : 1) * VALUE.relic, "失去遺物");
    if (effects.losePotions) add(-v("losePotions") * VALUE.potion, `失去 ${v("losePotions")} 瓶藥水`);
    if (effects.fight) {
      const cost = VALUE.fight[effects.fight] ?? VALUE.fight.monster;
      add(cost, `進入${{ monster: "戰鬥", elite: "菁英戰", boss: "首領戰" }[effects.fight] ?? "戰鬥"}（預估損血）`);
      hp = Math.max(0, hp + cost);
      if (effects.rewards) {
        const inner = scoreEffects(effects.rewards, { ...ctx, hp, maxHp });
        score += inner.score; reasons.push(...inner.reasons.map(r => `戰後 ${r}`)); unknown ||= inner.unknown;
      }
    }
    return { score, hpAfter: hp, reasons, unknown };
  }

  function planEvent(input, table) {
    input = input || {};
    const entry = table?.events?.[input.eventId];
    const ctx = {
      vars: input.vars ?? {}, hp: input.hp ?? 0, maxHp: Math.max(1, input.maxHp ?? 1), gold: input.gold ?? 0,
      deck: input.deck ?? [], potions: (input.potions ?? []).filter(Boolean).length,
      potionSlots: input.potionSlots ?? 3, name: input.name ?? (id => id),
    };
    const ranked = (input.options ?? []).map((option, index) => {
      const data = entry?.options?.[option.textKey];
      const base = { ...option, originalIndex: index, reasons: [], score: 0, evaluated: false };
      if (option.locked) return { ...base, reasons: ["目前無法選擇。"] };
      if (!data) return { ...base, reasons: ["此選項尚未建立效果資料。"] };
      if (data.proceed) return { ...base, evaluated: true, reasons: ["離開事件：基準分 0。"] };
      if (data.next && !data.effects && !data.outcomes) return { ...base, evaluated: true, reasons: ["進入下一頁，選項另行評估。"] };
      let score = 0, hpAfter = ctx.hp, unknown = false;
      const reasons = [];
      if (data.effects) {
        const r = scoreEffects(data.effects, ctx);
        score += r.score; hpAfter = r.hpAfter; unknown ||= r.unknown; reasons.push(...r.reasons);
      }
      if (data.outcomes?.length) {
        let worstHp = Infinity;
        for (const outcome of data.outcomes) {
          const r = scoreEffects(outcome.effects ?? {}, { ...ctx, hp: hpAfter });
          score += outcome.p * r.score; unknown ||= r.unknown; worstHp = Math.min(worstHp, r.hpAfter);
          reasons.push(`${Math.round(outcome.p * 100)}%：${r.reasons.join("、") || "無效果"}`);
        }
        hpAfter = worstHp;
      }
      if (data.note) reasons.push(`未計入：${data.note}`);
      // mayKill only says the option carries a WillKillPlayer check; trust it when the HP effect is not modelled.
      const lethal = hpAfter <= 0 || (option.mayKill && unknown);
      if (lethal) reasons.unshift("可能致死，排除。");
      return { ...base, score: lethal ? -Infinity : score, evaluated: !unknown, lethal, reasons };
    });
    const best = ranked.filter(o => Number.isFinite(o.score) && !o.locked && (o.evaluated || o.score !== 0))
      .sort((a, b) => b.score - a.score)[0];
    return { eventId: input.eventId, known: !!entry, ranked, best: best?.originalIndex ?? null };
  }

  const api = { planEvent, scoreEffects, resolve, VALUE };
  if (typeof module === "object" && module.exports) module.exports = api;
  root.planEvent = planEvent;
  root.EventPolicy = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
