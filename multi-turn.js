/* A pure planner for fixed combat inputs. Cards without vulnerable play in list order. */
(function () {
  function solveMultiTurn({ player, enemies, turns, drawPile, discardPile, assumedRefill }, options = {}) {
    const maxPlans = Math.max(1, Math.floor(options.maxPlans ?? 3));
    const beamWidth = Math.max(1, Math.floor(options.beamWidth ?? 256));
    const maxNodes = Math.max(1, Math.floor(options.maxNodes ?? 100000));
    const initialBossHp = enemies.filter(e => e.boss).reduce((sum, e) => sum + e.hp, 0);
    const initialEnemyHp = enemies.reduce((sum, e) => sum + e.hp, 0);
    let nodes = 0;
    let truncated = false;
    let modelBoundary = false;
    const knownDrawPile = Array.isArray(drawPile) ? drawPile : [];
    const canRefill = Array.isArray(discardPile);

    function shuffled(cards) {
      const result = [...cards];
      let seed = Number(options.shuffleSeed) >>> 0;
      if (!seed) seed = 0x9e3779b9;
      for (let i = result.length - 1; i > 0; i--) {
        seed ^= seed << 13;
        seed ^= seed >>> 17;
        seed ^= seed << 5;
        const j = (seed >>> 0) % (i + 1);
        [result[i], result[j]] = [result[j], result[i]];
      }
      return result;
    }

    function refillOrder(discard) {
      const remaining = [...discard];
      const ordered = [];
      if (Array.isArray(assumedRefill)) {
        for (const hint of assumedRefill) {
          const index = remaining.findIndex(card => card === hint ||
            (hint && card && hint.id !== undefined && card.id === hint.id));
          if (index >= 0) ordered.push(...remaining.splice(index, 1));
        }
      }
      return [...ordered, ...shuffled(remaining)];
    }

    function takeCards(state, count) {
      let next = { ...state };
      const drawn = [];
      while (drawn.length < count) {
        if (next.drawCursor < knownDrawPile.length) {
          drawn.push(knownDrawPile[next.drawCursor++]);
          continue;
        }
        if (!next.refillsUsed) {
          if (!canRefill || next.refillBlocked || !next.discard.length) {
            unknownDraw();
            return null;
          }
          next = {
            ...next,
            refillPile: refillOrder(next.discard),
            refillCursor: 0,
            refillsUsed: 1,
            discard: []
          };
        }
        if (next.refillCursor >= next.refillPile.length) {
          unknownDraw();
          return null;
        }
        drawn.push(next.refillPile[next.refillCursor++]);
      }
      return { state: next, cards: drawn };
    }

    function unknownDraw() {
      modelBoundary = true;
      truncated = true;
    }

    function enemyHp(state) {
      return Object.fromEntries(state.enemies.map(e => [e.id, e.hp]));
    }

    function isWon(state) {
      return state.enemies.every(e => e.hp <= 0);
    }

    function removeVanishing(state) {
      if (state.enemies.some(e => e.boss && e.hp <= 0)) {
        for (const enemy of state.enemies) if (enemy.vanish) enemy.hp = 0;
      }
    }

    function compare(a, b) {
      const rank = state => [
        isWon(state) ? 1 : 0,
        state.hp > 0 ? 1 : 0,
        state.hp,
        state.strengthPotion ? 1 : 0,
        isWon(state) ? -state.turns.length : 0,
        initialBossHp - state.enemies.filter(e => e.boss).reduce((sum, e) => sum + e.hp, 0),
        state.enemies.filter(e => e.hp <= 0).length,
        initialEnemyHp - state.enemies.reduce((sum, e) => sum + e.hp, 0),
        state.enemies.reduce((sum, e) => sum + (e.hp > 0 ? (e.vulnerable || 0) : 0), 0),
        state.freeAttacks,
        state.potionUsedAt,
        -state.turns.reduce((sum, turn) => sum + Object.values(turn.enemyHp).reduce((hp, current) => hp + current, 0), 0),
        -state.turns.reduce((sum, turn) => sum + turn.actions.length, 0)
      ];
      const x = rank(a), y = rank(b);
      for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return x[i] > y[i] ? 1 : -1;
      return a.key < b.key ? 1 : a.key > b.key ? -1 : 0;
    }

    function keep(list, state, limit, mayLoseOptimality) {
      if (list.length === limit && compare(state, list[list.length - 1]) <= 0) {
        if (mayLoseOptimality) truncated = true;
        return;
      }
      let lo = 0, hi = list.length;
      while (lo < hi) {
        const mid = (lo + hi) >> 1;
        if (compare(state, list[mid]) > 0) hi = mid;
        else lo = mid + 1;
      }
      list.splice(lo, 0, state);
      if (list.length > limit) {
        list.pop();
        if (mayLoseOptimality) truncated = true;
      }
    }

    function finish(state, turn, actions, energyLeft, hand, used) {
      const next = {
        hp: state.hp,
        block: state.block,
        strength: state.strength,
        strengthPotion: state.strengthPotion,
        potionUsedAt: state.potionUsedAt,
        freeAttacks: state.freeAttacks,
        drawCursor: state.drawCursor,
        refillPile: state.refillPile,
        refillCursor: state.refillCursor,
        refillsUsed: state.refillsUsed,
        refillBlocked: state.refillBlocked,
        discard: [...state.discard, ...hand.filter((_, index) => !used.has(index))],
        enemies: state.enemies.map(e => ({ ...e })),
        turns: state.turns,
        key: state.key
      };
      let damageTaken = 0;
      if (!isWon(next)) {
        for (const enemy of next.enemies) {
          if (enemy.hp <= 0) continue;
          const intent = turn.intents?.[enemy.id] || {};
          enemy.block = 0;
          const damage = Math.max(0, Number(intent.damage) || 0);
          const absorbed = Math.min(next.block, damage);
          next.block -= absorbed;
          const loss = Math.min(next.hp, damage - absorbed);
          next.hp -= loss;
          damageTaken += loss;
          enemy.block = Math.max(0, Number(intent.block) || 0);
          enemy.vulnerable = Math.max(0, (enemy.vulnerable || 0) - 1);
          if (Array.isArray(intent.addCards)) next.discard.push(...intent.addCards);
          if (next.hp <= 0) break;
        }
      }
      next.turns = [...next.turns, {
        actions,
        hp: next.hp,
        enemyHp: enemyHp(next),
        damageTaken,
        energyLeft
      }];
      return next;
    }

    const results = [];
    let frontier = [{
      hp: player.hp,
      block: player.block,
      strength: Number(player.strength) || 0,
      strengthPotion: !!player.strengthPotion,
      potionUsedAt: -1,
      freeAttacks: Math.max(0, Math.floor(Number(player.freeAttacks) || 0)),
      drawCursor: 0,
      refillPile: [],
      refillCursor: 0,
      refillsUsed: 0,
      refillBlocked: false,
      discard: canRefill ? [...discardPile] : [],
      enemies: enemies.map(e => ({ ...e })),
      turns: [],
      key: ""
    }];
    removeVanishing(frontier[0]);

    for (let turnIndex = 0; turnIndex < turns.length && frontier.length; turnIndex++) {
      const turn = turns[turnIndex];
      const savedFrontier = [];
      const spentFrontier = [];
      for (const start of frontier) {
        if (isWon(start) || start.hp <= 0) {
          keep(results, start, maxPlans, false);
          continue;
        }
        let cards = Array.isArray(turn.cards) ? turn.cards : [];
        let initial = { ...start, block: turnIndex === 0 ? start.block : 0 };
        if (!Array.isArray(turn.cards) && turn.drawCount) {
          const count = Math.max(0, Math.floor(Number(turn.drawCount) || 0));
          const draw = takeCards(initial, count);
          if (!draw) continue;
          initial = draw.state;
          cards = draw.cards;
        }

        function emit(state, actions, energyLeft, hand, used) {
          const outcome = finish(state, turn, actions, energyLeft, hand, used);
          if (isWon(outcome) || outcome.hp <= 0 || turnIndex === turns.length - 1) {
            keep(results, outcome, maxPlans, false);
          } else {
            keep(outcome.strengthPotion ? savedFrontier : spentFrontier,
              outcome, beamWidth, true);
          }
        }

        function isAttack(card) {
          return card.attack === true || (card.damage || 0) > 0;
        }

        function cardCost(card, state) {
          return isAttack(card) && state.freeAttacks > 0 ? 0 : card.cost;
        }

        function playable(card, state, energyLeft) {
          return !card.skip && card.owner === player.id && cardCost(card, state) <= energyLeft &&
            (card.damage || card.block || card.vulnerable || card.draw || card.grantsFreeAttack) &&
            (!(card.damage || card.vulnerable) || state.enemies.some(e => e.hp > 0));
        }

        function play(card, index, state, actions, energyLeft, targetIds, hand, used, continueSearch) {
          const drawCount = Math.max(0, Math.floor(Number(card.draw) || 0));
          const spendCharge = isAttack(card) && state.freeAttacks > 0;
          const cost = cardCost(card, state);
          const exhausts = card.exhaust === true || (drawCount > 0 && card.exhaust !== false);
          const nextUsed = new Set(used);
          nextUsed.add(index);
          const played = {
            ...state,
            block: state.block + (card.block || 0),
            freeAttacks: state.freeAttacks - (spendCharge ? 1 : 0) +
              (isAttack(card) ? Math.max(0, Math.floor(Number(card.grantsFreeAttack) || 0)) : 0),
            discard: exhausts ? state.discard : [...state.discard, card],
            refillBlocked: state.refillBlocked || !!card.endsSequence,
            enemies: state.enemies.map(e => ({ ...e })),
            key: state.key + `${turnIndex}:${String(index).padStart(3, "0")}:${targetIds.join(",")};`
          };
          for (const enemy of played.enemies) {
            if (!targetIds.includes(enemy.id) || enemy.hp <= 0) continue;
            for (let hit = 0; hit < (card.hits || 1) && enemy.hp > 0; hit++) {
              const powered = card.damage ? (card.damage + state.strength) : 0;
              const damage = Math.floor(Math.max(0, powered *
                (card.damageMultiplier ?? turn.damageMultiplier ?? 1)) *
                ((enemy.vulnerable || 0) > 0 ? 1.5 : 1));
              const absorbed = Math.min(enemy.block, damage);
              enemy.block -= absorbed;
              let hpLoss = damage - absorbed;
              if (enemy.slippery > 0 && hpLoss > 0) {
                enemy.slippery--;
                hpLoss = 1;
              }
              enemy.hp = Math.max(0, enemy.hp - hpLoss);
            }
            const addedVulnerable = card.doublesVulnerable ? enemy.vulnerable || 0 : card.vulnerable;
            if (enemy.hp > 0 && addedVulnerable) {
              if (enemy.artifact > 0) enemy.artifact--;
              else enemy.vulnerable = (enemy.vulnerable || 0) + addedVulnerable;
            }
          }
          removeVanishing(played);
          const chosen = [...actions, { card, targetIds: [...targetIds], energyCost: cost }];
          const draw = drawCount && !isWon(played) ? takeCards(played, drawCount) :
            { state: played, cards: [] };
          if (!draw) return;
          const nextHand = draw.cards.length ? [...hand, ...draw.cards] : hand;
          if (card.endsSequence) emit(draw.state, chosen, energyLeft - cost, nextHand, nextUsed);
          else continueSearch(draw.state, chosen, energyLeft - cost, nextHand, nextUsed);
        }

        function targetSets(card, state) {
          if (!(card.damage || card.vulnerable)) return [[]];
          const living = state.enemies.filter(e => e.hp > 0);
          return card.area ? [living.map(e => e.id)] : living.map(e => [e.id]);
        }

        function visit(index, state, actions, energyLeft, hand, used) {
          if (nodes >= maxNodes) {
            truncated = true;
            emit(state, actions, energyLeft, hand, used);
            return;
          }
          nodes++;
          if (isWon(state) || index === hand.length) {
            emit(state, actions, energyLeft, hand, used);
            return;
          }
          const card = hand[index];
          visit(index + 1, state, actions, energyLeft, hand, used);
          if (!playable(card, state, energyLeft)) return;
          for (const targetIds of targetSets(card, state)) {
            if (nodes >= maxNodes) {
              truncated = true;
              break;
            }
            play(card, index, state, actions, energyLeft, targetIds, hand, used,
              (played, chosen, energy, nextHand, nextUsed) =>
                visit(index + 1, played, chosen, energy, nextHand, nextUsed));
          }
        }

        function visitAny(used, state, actions, energyLeft, hand) {
          if (nodes >= maxNodes) {
            truncated = true;
            emit(state, actions, energyLeft, hand, used);
            return;
          }
          nodes++;
          emit(state, actions, energyLeft, hand, used);
          if (isWon(state)) return;
          for (let index = 0; index < hand.length; index++) {
            if (used.has(index) || !playable(hand[index], state, energyLeft)) continue;
            for (const targetIds of targetSets(hand[index], state)) {
              if (nodes >= maxNodes) {
                truncated = true;
                return;
              }
              play(hand[index], index, state, actions, energyLeft, targetIds, hand, used,
                (played, chosen, energy, nextHand, nextUsed) =>
                  visitAny(nextUsed, played, chosen, energy, nextHand));
            }
          }
        }

        function search(state, actions) {
          if (state.freeAttacks > 0 || cards.some(card => !card.skip &&
              (card.vulnerable || card.doublesVulnerable || card.endsSequence || card.draw || card.grantsFreeAttack))) {
            visitAny(new Set(), state, actions, turn.energy, cards);
          } else {
            visit(0, state, actions, turn.energy, cards, new Set());
          }
        }

        search(initial, []);
        if (initial.strengthPotion) {
          search({
            ...initial,
            strength: initial.strength + 2,
            strengthPotion: false,
            potionUsedAt: turnIndex,
            key: initial.key + `${turnIndex}:potion;`
          }, [{ potion: "StrengthPotion", targetIds: [] }]);
        }
      }
      frontier = [];
      for (let i = 0; i < beamWidth; i++) {
        if (savedFrontier[i]) frontier.push(savedFrontier[i]);
        if (spentFrontier[i]) frontier.push(spentFrontier[i]);
      }
    }
    for (const state of frontier) keep(results, state, maxPlans, false);
    const plans = results.map(state => ({
        turns: state.turns,
        hp: state.hp,
        enemyHp: enemyHp(state),
        won: isWon(state),
        dead: state.hp <= 0,
        potionRemaining: state.strengthPotion,
        freeAttacks: state.freeAttacks,
        estimatedShuffle: state.refillsUsed > 0
      }));
    return {
      plans,
      nodes,
      truncated,
      modelBoundary,
      estimatedShuffle: plans.some(plan => plan.estimatedShuffle)
    };
  }

  globalThis.solveMultiTurn = solveMultiTurn;
  if (typeof module !== "undefined" && module.exports) module.exports = solveMultiTurn;
})();
