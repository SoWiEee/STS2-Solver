/* A pure planner for fixed combat inputs. Cards without vulnerable play in list order. */
(function () {
  function solveMultiTurn({ player, enemies, turns }, options = {}) {
    const maxPlans = Math.max(1, Math.floor(options.maxPlans ?? 3));
    const beamWidth = Math.max(1, Math.floor(options.beamWidth ?? 256));
    const maxNodes = Math.max(1, Math.floor(options.maxNodes ?? 100000));
    const initialBossHp = enemies.filter(e => e.boss).reduce((sum, e) => sum + e.hp, 0);
    const initialEnemyHp = enemies.reduce((sum, e) => sum + e.hp, 0);
    let nodes = 0;
    let truncated = false;

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
        initialBossHp - state.enemies.filter(e => e.boss).reduce((sum, e) => sum + e.hp, 0),
        initialEnemyHp - state.enemies.reduce((sum, e) => sum + e.hp, 0),
        state.potionUsedAt
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

    function finish(state, turn, actions, energyLeft) {
      const next = {
        hp: state.hp,
        block: state.block,
        strength: state.strength,
        strengthPotion: state.strengthPotion,
        potionUsedAt: state.potionUsedAt,
        enemies: state.enemies.map(e => ({ ...e })),
        turns: state.turns,
        key: state.key
      };
      let damageTaken = 0;
      if (!isWon(next)) {
        for (const enemy of next.enemies) {
          if (enemy.hp <= 0) continue;
          const intent = turn.intents?.[enemy.id] || {};
          const damage = Math.max(0, Number(intent.damage) || 0);
          const absorbed = Math.min(next.block, damage);
          next.block -= absorbed;
          const loss = Math.min(next.hp, damage - absorbed);
          next.hp -= loss;
          damageTaken += loss;
          enemy.block += Math.max(0, Number(intent.block) || 0);
          enemy.vulnerable = Math.max(0, (enemy.vulnerable || 0) - 1);
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
      enemies: enemies.map(e => ({ ...e })),
      turns: [],
      key: ""
    }];
    removeVanishing(frontier[0]);

    for (let turnIndex = 0; turnIndex < turns.length && frontier.length; turnIndex++) {
      const turn = turns[turnIndex];
      const cards = turn.cards || [];
      const savedFrontier = [];
      const spentFrontier = [];
      for (const start of frontier) {
        if (isWon(start) || start.hp <= 0) {
          keep(results, start, maxPlans, false);
          continue;
        }
        const initial = { ...start, block: turnIndex === 0 ? start.block : 0 };

        function emit(state, actions, energyLeft) {
          const outcome = finish(state, turn, actions, energyLeft);
          if (isWon(outcome) || outcome.hp <= 0 || turnIndex === turns.length - 1) {
            keep(results, outcome, maxPlans, false);
          } else {
            keep(outcome.strengthPotion ? savedFrontier : spentFrontier,
              outcome, beamWidth, true);
          }
        }

        function playable(card, state, energyLeft) {
          return !card.skip && card.owner === player.id && card.cost <= energyLeft &&
            (card.damage || card.block || card.vulnerable) &&
            (!(card.damage || card.vulnerable) || state.enemies.some(e => e.hp > 0));
        }

        function play(card, index, state, actions, energyLeft, targetIds, continueSearch) {
          const played = {
            ...state,
            block: state.block + (card.block || 0),
            enemies: state.enemies.map(e => ({ ...e })),
            key: state.key + `${turnIndex}:${String(index).padStart(3, "0")}:${targetIds.join(",")};`
          };
          for (const enemy of played.enemies) {
            if (!targetIds.includes(enemy.id) || enemy.hp <= 0) continue;
            for (let hit = 0; hit < (card.hits || 1) && enemy.hp > 0; hit++) {
              const powered = card.damage ? (card.damage + state.strength) : 0;
              const damage = Math.floor(Math.max(0, powered * (card.damageMultiplier ?? 1)) *
                ((enemy.vulnerable || 0) > 0 ? 1.5 : 1));
              const absorbed = Math.min(enemy.block, damage);
              enemy.block -= absorbed;
              enemy.hp = Math.max(0, enemy.hp - (damage - absorbed));
            }
            if (enemy.hp > 0) enemy.vulnerable = (enemy.vulnerable || 0) + (card.vulnerable || 0);
          }
          removeVanishing(played);
          const chosen = [...actions, { card, targetIds: [...targetIds] }];
          if (card.endsSequence) emit(played, chosen, energyLeft - card.cost);
          else continueSearch(played, chosen, energyLeft - card.cost);
        }

        function targetSets(card, state) {
          if (!(card.damage || card.vulnerable)) return [[]];
          const living = state.enemies.filter(e => e.hp > 0);
          return card.area ? [living.map(e => e.id)] : living.map(e => [e.id]);
        }

        function visit(index, state, actions, energyLeft) {
          if (nodes >= maxNodes) {
            truncated = true;
            emit(state, actions, energyLeft);
            return;
          }
          nodes++;
          if (isWon(state) || index === cards.length) {
            emit(state, actions, energyLeft);
            return;
          }
          const card = cards[index];
          visit(index + 1, state, actions, energyLeft);
          if (!playable(card, state, energyLeft)) return;
          for (const targetIds of targetSets(card, state)) {
            if (nodes >= maxNodes) {
              truncated = true;
              break;
            }
            play(card, index, state, actions, energyLeft, targetIds,
              (played, chosen, energy) => visit(index + 1, played, chosen, energy));
          }
        }

        function visitAny(used, state, actions, energyLeft) {
          if (nodes >= maxNodes) {
            truncated = true;
            emit(state, actions, energyLeft);
            return;
          }
          nodes++;
          emit(state, actions, energyLeft);
          if (isWon(state)) return;
          for (let index = 0; index < cards.length; index++) {
            if (used.has(index) || !playable(cards[index], state, energyLeft)) continue;
            for (const targetIds of targetSets(cards[index], state)) {
              if (nodes >= maxNodes) {
                truncated = true;
                return;
              }
              const nextUsed = new Set(used);
              nextUsed.add(index);
              play(cards[index], index, state, actions, energyLeft, targetIds,
                (played, chosen, energy) => visitAny(nextUsed, played, chosen, energy));
            }
          }
        }

        function search(state, actions) {
          if (cards.some(card => !card.skip && (card.vulnerable || card.endsSequence))) {
            visitAny(new Set(), state, actions, turn.energy);
          } else {
            visit(0, state, actions, turn.energy);
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
    return {
      plans: results.map(state => ({
        turns: state.turns,
        hp: state.hp,
        enemyHp: enemyHp(state),
        won: isWon(state),
        dead: state.hp <= 0,
        potionRemaining: state.strengthPotion
      })),
      nodes,
      truncated
    };
  }

  globalThis.solveMultiTurn = solveMultiTurn;
  if (typeof module !== "undefined" && module.exports) module.exports = solveMultiTurn;
})();
