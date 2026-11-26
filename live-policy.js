/* Turn inputs supported by the Windows snapshot policy for STS2 v0.113.1 and v0.114.0. */
(function () {
  const safeRelics = new Set(["BurningBlood", "DowsingRod", "PrecariousShears", "SwordOfStone", "BloodVial"]);
  const attack = new Set(["StrikeIronclad", "Bash", "Cinder", "Unrelenting", "MoltenFist"]);
  const names = { StrikeIronclad: "打擊", DefendIronclad: "防禦", Bash: "痛擊", Cinder: "餘燼", Unrelenting: "無情猛攻", MoltenFist: "熔融之拳" };
  // [damage at A0–8, damage at A9–10, optional transition, optional hits, optional block, optional strength gain]
  const opponentModels = {
    ShrinkerBeetle: { name: "縮小甲蟲", moves: { SHRINKER_MOVE: [0, 0, "shrink"], CHOMP_MOVE: [7, 8], STOMP_MOVE: [13, 14] } },
    FuzzyWurmCrawler: { name: "毛絨伏地蟲", moves: { FIRST_ACID_GOOP: [4, 6], ACID_GOOP: [4, 6], INHALE: [0, 0, null, 1, 0, 7] } },
    LeafSlimeS: { name: "小樹葉史萊姆", moves: { GOOP_MOVE: [0, 0, "slimed"], TACKLE_MOVE: [3, 4] } },
    TwigSlimeM: { name: "中樹枝史萊姆", moves: { STICKY_SHOT_MOVE: [0, 0, "slimed"], POKEY_POUNCE_MOVE: [11, 12] } },
    TwigSlimeS: { name: "小樹枝史萊姆", moves: { TACKLE_MOVE: [4, 5] } },
    Nibbit: { name: "Nibbit", moves: { BUTT_MOVE: [12, 13], SLICE_MOVE: [6, 7, "slice"], HISS_MOVE: [0, 0, "hiss"] } },
    Inklet: { name: "墨寶", moves: { JAB_MOVE: [3, 4], WHIRLWIND_MOVE: [2, 3, null, 3], PIERCING_GAZE_MOVE: [10, 11] } },
    CubexConstruct: { name: "Cubex Construct", moves: {
      CHARGE_UP_MOVE: [0, 0, null, 1, 0, 2],
      REPEATER_BLAST_MOVE: [7, 8, null, 1, 0, 2],
      REPEATER_BLAST_MOVE_2: [7, 8, null, 1, 0, 2],
      EXPEL_MOVE: [5, 6, null, 2]
    } },
    Aeonglass: { name: "Aeonglass", boss: true, moves: { EBB_MOVE: [22, 26, null, 1, 33], EYE_LASERS_MOVE: [11, 12, null, 2] } }
  };
  const horizon = 4;
  const drawPerTurn = 5;

  function slimed(id, cost=1){return {id,owner:"p1",name:"黏液",cost,draw:1,exhaust:true};}

  function normalizedPile(pile, prefix, ignoredEffects) {
    const cards=[];
    for(let i=0;i<pile.length;i++){
      const card=pile[i];
      try {
        const [modeled]=cardsFor([card],`${prefix}${i}`,ignoredEffects);
        cards.push(modeled??{id:`${prefix}${i}`,owner:"p1",name:card.name,cost:0,skip:true});
      } catch(error) {
        return {cards,error:error.message};
      }
    }
    return {cards,error:""};
  }

  function cardsFor(hand, turn, ignoredEffects) {
    return hand.flatMap((card, index) => {
      if (card.name === "Dowsing" && card.unplayable) return [];
      if (card.name === "Slimed" && Number.isInteger(card.baseCost) && card.baseCost >= 0) {
        if (card.otherModifiers) throw new Error(`未支援的費用或重複出牌修飾：${card.name}`);
        if (card.modified) ignoredEffects.add(`${card.name} 的附魔或苦難效果`);
        return [slimed(`t${turn}-c${index}`,card.baseCost)];
      }
      const isAttack = attack.has(card.name) && card.targetType === 2;
      const isDefend = card.name === "DefendIronclad" && card.targetType === 1;
      const value = isAttack ? card.vars.damage : card.vars.block;
      if (!(isAttack || isDefend) || !Number.isInteger(card.baseCost) ||
          card.baseCost < 0 || !Number.isInteger(value) || value < 0) {
        throw new Error(`沒有可安全套用的基礎牌效果：${names[card.name] || card.name}`);
      }
      if (card.otherModifiers) throw new Error(`未支援的費用或重複出牌修飾：${names[card.name]}`);
      if (card.modified) {
        for (const effect of [card.enchantment,card.affliction].filter(Boolean))
          ignoredEffects.add(`${names[card.name]} 的 ${effect}`);
        if (!card.enchantment && !card.affliction)
          ignoredEffects.add(`${names[card.name]} 的附魔或苦難效果`);
      }
      return [{
        id: `t${turn}-c${index}`, owner: "p1",
        name: names[card.name] + (card.upgrades ? "+" : ""),
        cost: card.baseCost, damage: isAttack ? value : 0,
        block: isDefend ? value : 0, area: false, hits: 1,
        vulnerable: card.name === "Bash" ? 2 + card.upgrades : 0,
        doublesVulnerable: card.name === "MoltenFist",
        exhaust: card.name === "MoltenFist",
        endsSequence: card.name === "Cinder",
        grantsFreeAttack: card.name === "Unrelenting" ? 1 : 0
      }];
    });
  }

  function planLiveCombat(snapshot) {
    if (snapshot.schemaVersion !== 1 || !["0.113.1","0.114.0"].includes(snapshot.version)) throw new Error("戰鬥快照版本未支援。");
    if (snapshot.players.length !== 1) throw new Error("即時多回合求解目前限單人。");
    const [player] = snapshot.players;
    if (!player.hand || !player.drawPile || !Number.isInteger(player.energy)) throw new Error("手牌、抽牌堆或能量尚未讀取。");
    const ignoredEffects = new Set();
    // Inert relics have no combat hooks; other unknown relic effects are omitted from this fallback.
    for (const relic of player.relics)
      if (!safeRelics.has(relic) && !(player.inertRelics ?? []).includes(relic))
        ignoredEffects.add(`遺物 ${relic}`);
    if (player.powers.some(p => !["StrengthPower","FreeAttackPower"].includes(p.name) && (p.name !== "ShrinkPower" || p.amount !== -1)))
      throw new Error("玩家身上有尚未模擬的狀態。");
    const opponents = snapshot.enemies.map((enemy, index) => {
      const model = Object.hasOwn(opponentModels, enemy.name) ? opponentModels[enemy.name] : null;
      if (!model) throw new Error(`目前尚未模擬敵人 ${enemy.name}。`);
      if (enemy.powers.some(p => p.name !== "StrengthPower" && (!["VulnerablePower","ArtifactPower","SlipperyPower"].includes(p.name) || p.amount < 0)))
        throw new Error(`${model.name}身上有尚未模擬的狀態。`);
      return { enemy, model, id: `e${index+1}`, strength: enemy.powers.find(p => p.name === "StrengthPower")?.amount ?? 0 };
    });
    const enemies = opponents.map(({enemy, model, id}) => ({
      id, name: model.name, hp: enemy.hp, maxHp: enemy.maxHp, block: enemy.block, boss: !!model.boss,
      artifact: enemy.powers.find(p => p.name === "ArtifactPower")?.amount ?? 0,
      slippery: enemy.powers.find(p => p.name === "SlipperyPower")?.amount ?? 0,
      vulnerable: enemy.powers.find(p => p.name === "VulnerablePower")?.amount ?? 0
    }));
    const highDamage = snapshot.ascension >= 9;
    let shrunk = player.powers.some(p => p.name === "ShrinkPower");
    let stop = "";
    const turns = [];
    const draw = normalizedPile(player.drawPile,"draw",ignoredEffects);
    const discard = Array.isArray(player.discardPile)?normalizedPile(player.discardPile,"discard",ignoredEffects):null;
    const canEstimate = discard!==null&&!draw.error&&!discard.error;
    const drawBoundary = draw.error || discard?.error || "超過可模擬的洗牌範圍。";
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
      if (i > 0 && !canEstimate && draw.cards.length < end) {
        stop = drawBoundary;
        break;
      }
      const hand = i === 0 ? { cards: normalizedHand(player.hand,ignoredEffects) } : { drawCount: drawPerTurn };
      const damageMultiplier = shrunk ? 0.7 : 1;
      const intents = {};
      opponents.forEach((opponent, index) => {
        const move = moves[index];
        const base = move[highDamage ? 1 : 0];
        intents[opponent.id] = { damage: base > 0 ? Math.max(0, base + opponent.strength)*(move[3]??1) : 0,
          block:move[4]??0 };
        if (move[2] === "slimed") intents[opponent.id].addCards=[slimed(`enemy-${opponent.id}-${i}`)];
        if (move[2] === "shrink") shrunk = true;
        opponent.strength += move[5] ?? 0;
        if (move[2] === "slice") intents[opponent.id].block = snapshot.ascension >= 8 ? 6 : 5;
        if (move[2] === "hiss") opponent.strength += highDamage ? 3 : 2;
      });
      turns.push({ energy: i === 0 ? player.energy : player.maxEnergy, ...hand, damageMultiplier, intents });
    }
    if (!turns.length) throw new Error(stop);
    const strength = player.powers.find(p => p.name === "StrengthPower")?.amount ?? 0;
    const freeAttacks = player.powers.find(p => p.name === "FreeAttackPower")?.amount ?? 0;
    const strengthPotion = player.potions?.includes("StrengthPotion") ?? false;
    const input = { player: { ...player, id: "p1", strength, strengthPotion, freeAttacks }, enemies, turns,
      drawPile: draw.cards, discardPile: canEstimate?discard.cards:undefined };
    let result = globalThis.solveMultiTurn(input,{shuffleSeed:103});
    while(!result.plans.length&&input.turns.length>1){
      input.turns= input.turns.slice(0,-1);
      stop=drawBoundary;
      result=globalThis.solveMultiTurn(input,{shuffleSeed:103});
    }
    if (result.modelBoundary) stop = `部分路線會越過已知抽牌範圍。${drawBoundary}`;
    // Agreement is about the immediately executable turn, not a known future order.
    let shuffleAgreement=null;
    if(result.estimatedShuffle){
      const scenarios=[result,...[227,419].map(shuffleSeed=>globalThis.solveMultiTurn(input,{shuffleSeed}))];
      const signature=plan=>JSON.stringify(plan.turns[0].actions.map(({card,potion,targetIds})=>[card?.name??potion,targetIds]));
      const first=signature(result.plans[0]);
      shuffleAgreement=scenarios.filter(s=>s.plans.length&&signature(s.plans[0])===first).length;
      if(shuffleAgreement<scenarios.length){
        const immediate=globalThis.solveMultiTurn({...input,turns:input.turns.slice(0,1),discardPile:undefined});
        if(immediate.plans.length){
          return {...immediate,turns:1,enemies,ignoredEffects:[...ignoredEffects],
            stop:"模擬的洗牌結果對當前出牌有分歧，先採用只看本回合的路線。"};
        }
      }
    }
    return { ...result, turns: input.turns.length, stop, enemies, shuffleAgreement,
      ignoredEffects:[...ignoredEffects] };
  }

  function normalizedHand(hand,ignoredEffects){
    const result=normalizedPile(hand,"hand",ignoredEffects);
    if(result.error)throw new Error(result.error);
    return result.cards;
  }

  globalThis.planLiveCombat = planLiveCombat;
  if (typeof module === "object") module.exports = planLiveCombat;
})();
