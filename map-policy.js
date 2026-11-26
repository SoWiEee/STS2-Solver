/* Pure route ranking from a visible solo map. Scores are heuristics, not combat forecasts. */
(function () {
  const TYPES = ["monster", "elite", "rest", "shop", "treasure", "unknown", "ancient", "boss"];
  const MAX_NODES = 1024;
  const MAX_CHILDREN = 32;
  const MAX_DEPTH = 64;
  const MAX_VISITS = 100000;
  const MAX_PATHS = 20000;

  function planMap({ hp, maxHp, gold, nodes, startIds }) {
    const number = value => Number.isFinite(Number(value)) ? Number(value) : 0;
    const health = Math.max(0, Math.min(number(hp), Math.max(1, number(maxHp))));
    const healthRatio = health / Math.max(1, number(maxHp));
    const coins = Math.max(0, number(gold));
    const source = Array.isArray(nodes) ? nodes : [];
    let truncated = source.length > MAX_NODES;
    const byId = new Map();
    for (const raw of source.slice(0, MAX_NODES)) {
      if (!raw || typeof raw.id !== "string" || !raw.id || byId.has(raw.id)) {
        truncated = true;
        continue;
      }
      const type = TYPES.includes(raw.type) ? raw.type : "unknown";
      const children = Array.isArray(raw.children) ? raw.children : [];
      if (!Array.isArray(raw.children)) truncated = true;
      if (children.length > MAX_CHILDREN) truncated = true;
      byId.set(raw.id, {
        id: raw.id,
        type,
        children: [...new Set(children.slice(0, MAX_CHILDREN).filter(id => typeof id === "string"))]
      });
    }
    const requestedStarts = [...new Set(Array.isArray(startIds) ? startIds : [])];
    const starts = requestedStarts.filter(id => byId.has(id));
    if (!Array.isArray(startIds) || starts.length !== requestedStarts.length) truncated = true;
    let evaluated = 0;
    let visits = 0;
    const candidates = [];

    // Expected HP ratio is simulated along the path (shortcut: flat per-node costs from observed fights,
    // not a combat model): monster -10%, elite -30%, unknown -5%, rest +30%.
    const HP_AFTER = { monster: -0.10, elite: -0.30, unknown: -0.05, rest: 0.30 };
    function nodeScore(type, step, hpRatio) {
      const lowHp = 1 - hpRatio;
      // An elite entered below ~45% HP, or any fight below ~15%, is a likely death.
      const danger = (type === "elite" && hpRatio < 0.45 ? -40 : 0) + (type === "monster" && hpRatio < 0.15 ? -20 : 0);
      switch (type) {
        case "monster": return 2 - 8 * lowHp + danger;
        case "elite": return 10 - 20 * lowHp + danger;
        case "rest": return 1 + 14 * lowHp + 5 * lowHp / (1 + step);
        case "shop": return 0.5 + Math.min(4, coins / 100) + 2 * lowHp;
        case "treasure": return 6;
        case "ancient": return 4;
        case "boss": return 0;
        default: return 0;
      }
    }

    function compare(a, b) {
      if (a.score !== b.score) return b.score - a.score;
      if (a.nodes.length !== b.nodes.length) return b.nodes.length - a.nodes.length;
      const x = a.nodes.join("\u0000"), y = b.nodes.join("\u0000");
      return x < y ? -1 : x > y ? 1 : 0;
    }

    function record(path, score, counts) {
      evaluated++;
      const last = byId.get(path[path.length - 1]);
      const firstStep = starts.length > 1 ? path[0] : (path[1] || path[0]);
      candidates.push({
        nodes: [...path],
        score: Math.round((score + (last.type === "boss" ? 100 : 0)) * 100) / 100,
        counts: { ...counts },
        firstStep
      });
    }

    function walk(id, path, seen, score, counts, hpRatio) {
      if (visits >= MAX_VISITS || evaluated >= MAX_PATHS || path.length >= MAX_DEPTH) {
        truncated = true;
        return;
      }
      visits++;
      const node = byId.get(id);
      if (!node || seen.has(id)) return;
      const nextPath = [...path, id];
      const nextSeen = new Set(seen);
      nextSeen.add(id);
      const nextCounts = { ...counts, [node.type]: counts[node.type] + 1 };
      const nextScore = score + nodeScore(node.type, path.length, hpRatio);
      const nextHp = Math.max(0.01, Math.min(1, hpRatio + (HP_AFTER[node.type] || 0)));
      const children = node.children.filter(child => {
        if (!byId.has(child)) { truncated = true; return false; }
        if (nextSeen.has(child)) { truncated = true; return false; }
        return true;
      });
      if (!children.length) {
        record(nextPath, nextScore, nextCounts);
        return;
      }
      for (const child of children) {
        if (visits >= MAX_VISITS || evaluated >= MAX_PATHS) {
          truncated = true;
          break;
        }
        walk(child, nextPath, nextSeen, nextScore, nextCounts, nextHp);
      }
    }

    const emptyCounts = Object.fromEntries(TYPES.map(type => [type, 0]));
    for (const id of starts) {
      if (visits >= MAX_VISITS || evaluated >= MAX_PATHS) { truncated = true; break; }
      walk(id, [], new Set(), 0, emptyCounts, healthRatio);
    }
    candidates.sort(compare);
    const selected = [];
    const firstSteps = new Set();
    for (const candidate of candidates) {
      if (firstSteps.has(candidate.firstStep)) continue;
      selected.push(candidate);
      firstSteps.add(candidate.firstStep);
      if (selected.length === 3) break;
    }
    for (const candidate of candidates) {
      if (selected.length === 3) break;
      if (!selected.includes(candidate)) selected.push(candidate);
    }
    selected.sort(compare);
    return {
      paths: selected.map(({ nodes, score, counts }) => ({ nodes, score, counts })),
      evaluated,
      truncated
    };
  }

  globalThis.planMap = planMap;
  if (typeof module !== "undefined" && module.exports) module.exports = planMap;
})();
