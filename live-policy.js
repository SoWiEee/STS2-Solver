/* Turn inputs supported by the Windows snapshot policy for STS2 v0.113.1. */
(function () {
  const safeRelics = new Set(["BurningBlood", "DowsingRod", "PrecariousShears"]);
  const attack = new Set(["StrikeIronclad", "Bash", "Cinder"]);
  const names = { StrikeIronclad: "打擊", DefendIronclad: "防禦", Bash: "痛擊", Cinder: "餘燼" };
  // [damage at A0–8, damage at A9–10, optional turn transition]
  const opponentModels = {
    ShrinkerBeetle: { name: "縮小甲蟲", moves: { SHRINKER_MOVE: [0, 0, "shrink"], CHOMP_MOVE: [7, 8], STOMP_MOVE: [13, 14] } },
    FuzzyWurmCrawler: { name: "毛絨伏地蟲", moves: { FIRST_ACID_GOOP: [4, 6], ACID_GOOP: [4, 6], INHALE: [0, 0, "strength"] } },
    LeafSlimeS: { name: "小樹葉史萊姆", moves: { GOOP_MOVE: [0, 0], TACKLE_MOVE: [3, 4] } },
    TwigSlimeM: { name: "中樹枝史萊姆", moves: { STICKY_SHOT_MOVE: [0, 0], POKEY_POUNCE_MOVE: [11, 12] } },
    TwigSlimeS: { name: "小樹枝史萊姆", moves: { TACKLE_MOVE: [4, 5] } }
  };
  const horizon = 4;
  const drawPerTurn = 5;

  function cardsFor(hand, turn, shrunk, omitted) {
    return hand.flatMap((card, index) => {
      if (card.name === "Dowsing" && card.unplayable && !card.modified) return [];
      if (card.name === "Slimed" && !card.modified) {
        omitted.add("黏液（抽牌）");
        return [];
      }
      const isAttack = attack.has(card.name) && card.targetType === 2;
      const isDefend = card.name === "DefendIronclad" && card.targetType === 1;
      const value = isAttack ? card.vars.damage : card.vars.block;
      if (card.modified || !(isAttack || isDefend) || !Number.isInteger(card.baseCost) ||
          card.baseCost < 0 || !Number.isInteger(value) || value < 0) {
        throw new Error(`未支援的卡牌或卡牌修飾：${names[card.name] || card.name}`);
      }
      return [{
        id: `t${turn}-c${index}`, owner: "p1",
        name: names[card.name] + (card.upgrades ? "+" : ""),
        cost: card.baseCost, damage: isAttack ? value : 0, damageMultiplier: shrunk ? 0.7 : 1,
        block: isDefend ? value : 0, area: false, hits: 1,
        vulnerable: card.name === "Bash" ? 2 + card.upgrades : 0,
        endsSequence: card.name === "Cinder"
      }];
    });
  }

  function planLiveCombat(snapshot) {
    if (snapshot.schemaVersion !== 1 || snapshot.version !== "0.113.1") throw new Error("戰鬥快照版本未支援。");
    if (snapshot.players.length !== 1) throw new Error("即時多回合求解目前限單人。");
    const [player] = snapshot.players;
    if (!player.hand || !player.drawPile || !Number.isInteger(player.energy)) throw new Error("手牌、抽牌堆或能量尚未讀取。");
    const relic = player.relics.find(name => !safeRelics.has(name));
    if (relic) throw new Error(`目前尚未模擬遺物 ${relic}。`);
    if (player.powers.some(p => p.name !== "StrengthPower" && (p.name !== "ShrinkPower" || p.amount !== -1)))
      throw new Error("玩家身上有尚未模擬的狀態。");
    const opponents = snapshot.enemies.map((enemy, index) => {
      const model = Object.hasOwn(opponentModels, enemy.name) ? opponentModels[enemy.name] : null;
      if (!model) throw new Error(`目前尚未模擬敵人 ${enemy.name}。`);
      if (enemy.powers.some(p => p.name !== "StrengthPower" && (p.name !== "VulnerablePower" || p.amount < 0)))
        throw new Error(`${model.name}身上有尚未模擬的狀態。`);
      return { enemy, model, id: `e${index+1}`, strength: enemy.powers.find(p => p.name === "StrengthPower")?.amount ?? 0 };
    });
    const enemies = opponents.map(({enemy, model, id}) => ({
      id, name: model.name, hp: enemy.hp, maxHp: enemy.maxHp, block: enemy.block, boss: false,
      vulnerable: enemy.powers.find(p => p.name === "VulnerablePower")?.amount ?? 0
    }));
    const highDamage = snapshot.ascension >= 9;
    let shrunk = player.powers.some(p => p.name === "ShrinkPower");
    let stop = "";
    const turns = [];
    const omitted = new Set();
    for (let i = 0; i < horizon; i++) {
      const moves = opponents.map(({model, enemy}) => model.moves[enemy.moves[i]]);
      const unknown = moves.findIndex((move, index) =>
        !move || (!i && opponents[index].enemy.hasAttack !== (move[0] > 0)));
      if (unknown >= 0) {
        const {model, enemy} = opponents[unknown];
        stop = `${model.name}下一招未能確定（${enemy.moves[i] ?? "無後續資料"}）。`;
        break;
      }
      const end = i * drawPerTurn;
      if (i > 0 && player.drawPile.length < end) {
        stop = "再下一回合需洗牌，抽牌順序尚未納入模型。";
        break;
      }
      const hand = i === 0 ? player.hand : player.drawPile.slice(end - drawPerTurn, end);
      let cards;
      try {
        cards = cardsFor(hand, i, shrunk, omitted);
      } catch (error) {
        if (i === 0) throw error;
        stop = error.message;
        break;
      }
      const intents = {};
      opponents.forEach((opponent, index) => {
        const move = moves[index];
        const base = move[highDamage ? 1 : 0];
        intents[opponent.id] = { damage: base > 0 ? Math.max(0, base + opponent.strength) : 0 };
        if (move[2] === "shrink") shrunk = true;
        if (move[2] === "strength") opponent.strength += 7;
      });
      turns.push({ energy: i === 0 ? player.energy : player.maxEnergy, cards, intents });
    }
    if (!turns.length) throw new Error(stop);
    const strength = player.powers.find(p => p.name === "StrengthPower")?.amount ?? 0;
    const strengthPotion = player.potions?.includes("StrengthPotion") ?? false;
    const input = { player: { ...player, id: "p1", strength, strengthPotion }, enemies, turns };
    return { ...globalThis.solveMultiTurn(input), turns: turns.length, stop, enemies, omitted: [...omitted] };
  }

  globalThis.planLiveCombat = planLiveCombat;
  if (typeof module === "object") module.exports = planLiveCombat;
})();
