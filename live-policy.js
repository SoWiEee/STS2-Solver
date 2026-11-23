/* Turn inputs supported by the Windows snapshot policy for STS2 v0.113.1. */
(function () {
  const safeRelics = new Set(["BurningBlood", "DowsingRod"]);
  const attack = new Set(["StrikeIronclad", "Bash"]);
  const names = { StrikeIronclad: "打擊", DefendIronclad: "防禦", Bash: "痛擊", ShrinkerBeetle: "縮小甲蟲" };
  const horizon = 4;
  const drawPerTurn = 5;

  function cardsFor(hand, turn, shrunk) {
    return hand.flatMap((card, index) => {
      if (card.name === "Dowsing" && card.unplayable && !card.modified) return [];
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
        cost: card.baseCost, damage: isAttack ? value * (shrunk ? 0.7 : 1) : 0,
        block: isDefend ? value : 0, area: false, hits: 1,
        vulnerable: card.name === "Bash" ? 2 + card.upgrades : 0
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
    if (player.powers.some(p => p.name !== "ShrinkPower" || p.amount !== -1))
      throw new Error("玩家身上有尚未模擬的狀態。");
    const [enemy] = snapshot.enemies;
    if (snapshot.enemies.length !== 1 || enemy.name !== "ShrinkerBeetle")
      throw new Error("這個遭遇尚未加入即時多回合模型。目前支援縮小甲蟲。");
    if (enemy.powers.some(p => p.name !== "VulnerablePower" || p.amount < 0))
      throw new Error("敵人身上有尚未模擬的狀態。");

    const enemies = [{
      id: "e1", name: names[enemy.name], hp: enemy.hp, maxHp: enemy.maxHp, block: enemy.block, boss: false,
      vulnerable: enemy.powers.find(p => p.name === "VulnerablePower")?.amount ?? 0
    }];
    const highDamage = snapshot.ascension >= 9;
    const damageForMove = { SHRINKER_MOVE: 0, CHOMP_MOVE: highDamage ? 8 : 7, STOMP_MOVE: highDamage ? 14 : 13 };
    let shrunk = player.powers.some(p => p.name === "ShrinkPower");
    let stop = "";
    const turns = [];
    for (let i = 0; i < horizon; i++) {
      const move = enemy.moves[i];
      const damage = damageForMove[move];
      if (damage === undefined || (!i && enemy.hasAttack !== (damage > 0))) {
        stop = "下一個敵招超出已知招式。";
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
        cards = cardsFor(hand, i, shrunk);
      } catch (error) {
        if (i === 0) throw error;
        stop = error.message;
        break;
      }
      turns.push({ energy: i === 0 ? player.energy : player.maxEnergy, cards, intents: { e1: { damage } } });
      if (move === "SHRINKER_MOVE") shrunk = true;
    }
    if (!turns.length) throw new Error(stop);
    const input = { player: { ...player, id: "p1" }, enemies, turns };
    return { ...globalThis.solveMultiTurn(input), turns: turns.length, stop, enemies };
  }

  globalThis.planLiveCombat = planLiveCombat;
  if (typeof module === "object") module.exports = planLiveCombat;
})();
