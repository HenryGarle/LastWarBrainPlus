// Map points that are not bases, and the analysis of an alliance's hive. Pure functions: the map
// page draws with them, hive.check.mjs tests them (node web/src/explorer/hive.check.mjs).

// ---- alliance points (MapFeature on the server; type = the client's WorldPointType)
// Alliance buildings (type 15) by buildId / 1000, from alliance_res_build.json in the 1.0.363 assets:
// [name, tiles per side, territory range in tiles either side (offter_range)].
const BUILDINGS = {
  41: ["Alliance Center I", 3, 9], 42: ["Alliance Center II", 3, 9], 43: ["Alliance Center III", 3, 9], 44: ["Alliance Center IV", 3, 11],
  51: ["Doomsday Stronghold", 5, 0], 91: ["Siege Camp", 1, 0],
  80: ["Alliance Outpost", 3, 0], 81: ["Alliance Outpost", 3, 0], 82: ["Alliance Outpost", 3, 0], 83: ["Alliance Outpost", 3, 0], 84: ["Alliance Outpost", 3, 0],
  92: ["Alliance Outpost", 3, 0], 93: ["Alliance Outpost", 3, 0], 94: ["Alliance Outpost", 3, 0], 95: ["Alliance Outpost", 3, 0], 96: ["Alliance Outpost", 3, 0],
  200: ["Alliance Furnace", 5, 18], 201: ["Furnace Relocation Spot", 5, 0], 202: ["Warlord Missile Target", 34, 0], 203: ["Mummy Army Target", 34, 0],
  300: ["Alliance Center", 9, 20], 301: ["Alliance Center Relocation Spot", 9, 0], 302: ["Sacred Water Hut", 3, 0], 303: ["Exploratory Well", 3, 0],
  304: ["Mithril Plant", 3, 0], 305: ["Obelisk", 3, 0],
  400: ["Alliance Center", 9, 20], 401: ["Alliance Center Relocation Spot", 9, 0], 402: ["Stone Warehouse", 3, 0], 403: ["Oil Warehouse", 3, 0], 404: ["Magatama Warehouse", 3, 0],
  500: ["Thunder Feathers Target", 34, 0], 510: ["Alliance Outpost", 3, 0],
};
const KINDS = { 11: ["City", 7], 25: ["Stronghold", 5], 35: ["Trade Post", 5], 60: ["Mini-game City", 5], 59: ["Marshal's Guard", 3] };
// Points whose zone an alliance holds when it holds the point (the 100x100 zones on season maps).
export const ZONE_TYPES = new Set([11, 25, 35]);

export function describe(f) {
  if (f.type === 15) {
    const b = BUILDINGS[Math.floor(f.buildId / 1000)];
    return { name: b ? b[0] : `Alliance building ${f.buildId}`, size: b ? b[1] : 3, range: b ? b[2] : 0, building: true };
  }
  const k = KINDS[f.type] || [`Point type ${f.type}`, 3];
  const name = f.type === 59 && f.level ? `${k[0]} Lv.${f.level}` : k[0];
  return { name, size: k[1], range: 0, building: f.type === 59 };
}

// Territory, one cell per `cell` tiles: each cell takes the alliance of the nearest zone point
// (within `reach`) that has an owner, then alliance buildings claim their range square on top.
// An approximation of the in-game borders: zones are drawn as the area nearest each city.
export function territory(features, size = 1000, cell = 5, reach = 75) {
  const n = Math.ceil(size / cell);
  const owner = new Array(n * n).fill(null);
  const zones = features.filter((f) => ZONE_TYPES.has(f.type));
  if (zones.length) {
    const r2 = reach * reach;
    for (let cy = 0; cy < n; cy++)
      for (let cx = 0; cx < n; cx++) {
        const x = cx * cell + cell / 2, y = cy * cell + cell / 2;
        let best = null, bd = r2;
        for (const z of zones) { const d = (z.x - x) ** 2 + (z.y - y) ** 2; if (d < bd) { bd = d; best = z; } }
        if (best && best.allianceId) owner[cy * n + cx] = best.allianceId;
      }
  }
  for (const f of features) {
    if (f.type !== 15 || !f.allianceId) continue;
    const { range } = describe(f);
    if (!range) continue;
    for (let cy = Math.max(0, Math.floor((f.y - range) / cell)); cy <= Math.min(n - 1, Math.floor((f.y + range) / cell)); cy++)
      for (let cx = Math.max(0, Math.floor((f.x - range) / cell)); cx <= Math.min(n - 1, Math.floor((f.x + range) / cell)); cx++)
        owner[cy * n + cx] = f.allianceId;
  }
  return { n, cell, owner };
}

// ---- hive analysis
export const LINK = 8;   // bases this close (tiles) are in the same hive

/** The largest group of bases chained together at most LINK tiles apart. */
export function hiveOf(bases) {
  const parent = bases.map((_, i) => i);
  const find = (i) => (parent[i] === i ? i : (parent[i] = find(parent[i])));
  const grid = new Map();
  bases.forEach((b, i) => { const k = `${Math.floor(b.x / LINK)},${Math.floor(b.y / LINK)}`; (grid.get(k) || grid.set(k, []).get(k)).push(i); });
  bases.forEach((b, i) => {
    const gx = Math.floor(b.x / LINK), gy = Math.floor(b.y / LINK);
    for (let dx = -1; dx <= 1; dx++) for (let dy = -1; dy <= 1; dy++)
      for (const j of grid.get(`${gx + dx},${gy + dy}`) || [])
        if (j > i && Math.hypot(bases[j].x - b.x, bases[j].y - b.y) <= LINK) parent[find(j)] = find(i);
  });
  const groups = new Map();
  bases.forEach((b, i) => { const r = find(i); (groups.get(r) || groups.set(r, []).get(r)).push(b); });
  return [...groups.values()].sort((a, b) => b.length - a.length)[0] || [];
}

const median = (a) => { const s = [...a].sort((x, y) => x - y); const m = s.length >> 1; return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2; };
const clamp = (v) => Math.max(0, Math.min(1, v));

/**
 * How regular a hive is, 0-100. Three parts:
 *   spacing   (45%) every base the same distance from its nearest neighbour - 1 - CV/0.5
 *   alignment (35%) neighbour offsets fall into a few repeated directions (a lattice, square or not)
 *   filled    (20%) few empty lattice spots inside the hive (one per tenth of the hive = 0); the one nearest the middle is
 *                   treated as a deliberate gap and not counted
 * Needs at least 5 bases.
 */
export function scoreHive(hive) {
  const n = hive.length;
  if (n < 5) return null;
  const nn = hive.map((a) => Math.min(...hive.filter((b) => b !== a).map((b) => Math.hypot(a.x - b.x, a.y - b.y))));
  const pitch = median(nn);
  const mean = nn.reduce((s, v) => s + v, 0) / n;
  const sd = Math.sqrt(nn.reduce((s, v) => s + (v - mean) ** 2, 0) / n);
  const spacing = clamp(1 - sd / mean / 0.5);

  // Neighbour offsets up to 1.5 pitches, sign-normalised. Exact: one tile off a lattice is off it.
  const counts = new Map();
  let pairs = 0;
  for (let i = 0; i < n; i++)
    for (let j = i + 1; j < n; j++) {
      let dx = hive[j].x - hive[i].x, dy = hive[j].y - hive[i].y;
      if (Math.hypot(dx, dy) > pitch * 1.5) continue;
      if (dx < 0 || (dx === 0 && dy < 0)) { dx = -dx; dy = -dy; }
      counts.set(`${dx},${dy}`, (counts.get(`${dx},${dy}`) || 0) + 1);
      pairs++;
    }
  const groups = [];
  for (const [k, c] of [...counts].sort((a, b) => b[1] - a[1])) {
    const [dx, dy] = k.split(",").map(Number);
    const g = groups.find((g) => g.dx === dx && g.dy === dy);
    if (g) g.c += c; else groups.push({ dx, dy, c });
  }
  groups.sort((a, b) => b.c - a.c);
  const alignment = pairs ? groups.slice(0, 4).reduce((s, g) => s + g.c, 0) / pairs : 0;

  // Holes: a lattice step from a base that lands on nothing, with 3+ bases around it.
  const near = (x, y, r) => hive.filter((b) => Math.hypot(b.x - x, b.y - y) <= r).length;
  const holes = new Map();
  for (const b of hive)
    for (const g of groups.slice(0, 4))
      for (const s of [1, -1]) {
        const x = b.x + s * g.dx, y = b.y + s * g.dy, k = `${x},${y}`;
        if (holes.has(k) || near(x, y, 1.5) > 0 || near(x, y, pitch * 1.2) < 3) continue;
        holes.set(k, { x, y });
      }
  // Neighbouring hole spots are one hole.
  const list = [];
  for (const h of holes.values()) if (!list.some((o) => Math.hypot(o.x - h.x, o.y - h.y) < pitch * 0.75)) list.push(h);
  const cx = hive.reduce((s, b) => s + b.x, 0) / n, cy = hive.reduce((s, b) => s + b.y, 0) / n;
  list.sort((a, b) => Math.hypot(a.x - cx, a.y - cy) - Math.hypot(b.x - cx, b.y - cy));
  const gap = list.length && Math.hypot(list[0].x - cx, list[0].y - cy) <= pitch * 2 ? list.shift() : null;
  const filled = clamp(1 - list.length / Math.max(3, n * 0.1));   // a hole per 10% of the hive = 0

  return {
    n, pitch: Math.round(pitch * 10) / 10, spacing, alignment, filled, holes: list, gap,
    score: Math.round(100 * (0.45 * spacing + 0.35 * alignment + 0.2 * filled)),
  };
}

// ---- export for Cpt Hedgehog's hive planner (cpt-hedge.com/maps/planner, Import / Export)
// Its format, from the planner's own code (2026-09-29): { version, timestamp, placedElements,
// gridSettings: { originX, originY, gridWidth, gridHeight }, metadata }. A cell (col,row) is game
// (originX - gridWidth + col, originY - row - 1), so y runs UP the page; an element's coordinate
// is its centre cell. Element ids and sizes are the planner's catalogue.
const PLANNER = {
  base: { name: "Commander Base", width: 3, height: 3, image: "/images/planner/headquarters.png", color: "#497d00" },
  "alliance-event": { name: "Alliance Exercise", width: 3, height: 3, image: "/images/planner/alliance-event.png", color: "#DC2626" },
  "alliance-furnace-s2": { name: "Alliance Furnace", width: 5, height: 5, image: "", color: "#DC2626" },
  "alliance-center-s3": { name: "Alliance Center", width: 9, height: 9, image: "", color: "#DC2626" },
  city: { name: "City", width: 7, height: 7, image: "", color: "#2563eb" },
  digsite: { name: "Stronghold", width: 5, height: 5, image: "", color: "#2563eb" },
  "trade-post": { name: "Trade Post", width: 5, height: 5, image: "", color: "#2563eb" },
};
const plannerId = (f) => {
  if (f.type === 59) return "alliance-event";
  if (f.type === 11) return "city";
  if (f.type === 25) return "digsite";
  if (f.type === 35) return "trade-post";
  if (f.type === 15) { const g = Math.floor(f.buildId / 1000); if (g === 200) return "alliance-furnace-s2"; if (g === 300 || g === 400) return "alliance-center-s3"; }
  return null;
};

/** The planner JSON for these bases, with any alliance points that fall inside the same area. */
export function plannerExport(bases, features = [], pad = 8) {
  const minX = Math.min(...bases.map((b) => b.x)) - 1 - pad, maxX = Math.max(...bases.map((b) => b.x)) + 1 + pad;
  const minY = Math.min(...bases.map((b) => b.y)) - 1 - pad, maxY = Math.max(...bases.map((b) => b.y)) + 1 + pad;
  const gridWidth = maxX - minX + 1, gridHeight = maxY - minY + 1;
  const stamp = Date.now();
  const placed = [];
  const place = (id, gx, gy, label) => {
    const el = PLANNER[id];
    const x = gx - minX - Math.floor((el.width - 1) / 2), y = maxY - gy - Math.floor((el.height - 1) / 2);
    if (x < 0 || y < 0 || x + el.width > gridWidth || y + el.height > gridHeight) return;
    placed.push({ id: `placed-${stamp}-${placed.length}`, elementId: id, x, y, ...el, label: label || "", zIndex: 0 });
  };
  for (const f of features) { const id = plannerId(f); if (id) place(id, f.x, f.y, f.tag || ""); }
  for (const b of bases) place("base", b.x, b.y, b.name);
  return {
    version: "1.0", timestamp: new Date(stamp).toISOString(), placedElements: placed,
    gridSettings: { originX: minX + gridWidth, originY: maxY + 1, gridWidth, gridHeight },
    metadata: { elementCount: placed.length, exportedBy: "Grid Planner" },
  };
}