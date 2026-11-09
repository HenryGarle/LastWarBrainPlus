// Season maps from the game's own tables (season-cities.js, generated from the 1.0.363 assets
// season_city_s2/s3.json + locale): every fixed building as [cityId, type, level, x, y, size, name].
// cityId is what a map scan reports for that building (MapFeature.BuildId), so owners join on it.
import CITIES from "./season-cities.js";

export const SEASONS = {
  2: { name: "Season 2 - Polar Storm", types: { 1: "City", 2: "Giant Cannon", 3: "Nuclear Furnace", 4: "Digging Stronghold" } },
  3: { name: "Season 3 - Golden Kingdom", types: { 1: "City", 2: "Giant Cannon", 3: "Great Pyramid", 4: "Digging Stronghold", 5: "Trade Post", 6: "Missile Factory" } },
};

// Both seasons divide the map into 13 x 13 zones: 75 tiles, the middle row and column 100. In
// season 2 every zone holds one city or dig site at its centre; in season 3 the dig sites hold
// the zones and cities / trade posts sit on the corners with their own 50 x 50 area (the layout
// Cpt Hedgehog's season maps show too). The in-game borders are these straight lines.
export const GRID = [0, 75, 150, 225, 300, 375, 450, 550, 625, 700, 775, 850, 925, 1000];
const CORNER = 25;   // half of a corner area

const cell = (v) => { let k = 0; while (k < GRID.length - 2 && v >= GRID[k + 1]) k++; return [GRID[k], GRID[k + 1]]; };
const nearLine = (v) => GRID.slice(1, -1).find((g) => Math.abs(g - v) <= 4);

/** Buildings and zones of a season: zone = { x0, y0, x1, y1, building, corner }. */
export function layout(season) {
  const rows = CITIES[season];
  if (!rows) return null;
  const buildings = rows.map(([id, type, level, x, y, size, name, buffs = [], res = 0]) => ({ id, type, level, x, y, size, name, buffs, res }));
  const zones = [];
  for (const b of buildings) {
    if (b.type === 2 || b.type === 6) continue;          // cannons / missile factories sit inside the capitol zone
    const gx = nearLine(b.x), gy = nearLine(b.y);
    if (gx !== undefined && gy !== undefined) zones.push({ x0: gx - CORNER, y0: gy - CORNER, x1: gx + CORNER, y1: gy + CORNER, building: b, corner: true });
    else { const [x0, x1] = cell(b.x), [y0, y1] = cell(b.y); zones.push({ x0, y0, x1, y1, building: b, corner: false }); }
  }
  // Corner areas are drawn over the zones they overlap, so they go last.
  zones.sort((a, b) => a.corner - b.corner);
  return { season, name: SEASONS[season].name, buildings, zones, byId: new Map(buildings.map((b) => [b.id, b])) };
}

// Point types a scan reports for season buildings: city, stronghold, trade post, mini-game city.
const SEASON_POINTS = new Set([11, 25, 35, 60]);
const matches = (lay, f) => { const b = lay.byId.get(f.buildId); return b && Math.abs(b.x - f.x) <= 3 && Math.abs(b.y - f.y) <= 3 ? b : null; };

/**
 * The season the map is on: the one whose buildings the scan's points line up with; failing that
 * (scans before 2026-09-29 kept no points), the one whose buildings no base stands on - bases cannot
 * be placed on a building, so the right layout has none under it (1967 on S2: 0 of 5,507; S3: 63).
 */
export function detectSeason(features, bases = []) {
  let best = null, bestN = 0;
  for (const s of Object.keys(SEASONS)) {
    const lay = layout(s);
    const n = features.filter((f) => SEASON_POINTS.has(f.type) && matches(lay, f)).length;
    if (n > bestN) { best = s; bestN = n; }
  }
  if (best || bases.length < 500) return best;
  const grid = new Map();
  for (const t of bases) { const k = `${t.x >> 4},${t.y >> 4}`; (grid.get(k) || grid.set(k, []).get(k)).push(t); }
  const under = (lay) => {
    let n = 0;
    for (const b of lay.buildings) {
      const h = b.size / 2 + 1;
      for (let gx = (b.x - 6) >> 4; gx <= (b.x + 6) >> 4; gx++) for (let gy = (b.y - 6) >> 4; gy <= (b.y + 6) >> 4; gy++)
        for (const t of grid.get(`${gx},${gy}`) || []) if (Math.abs(t.x - b.x) < h && Math.abs(t.y - b.y) < h) n++;
    }
    return n;
  };
  const fits = Object.keys(SEASONS).map((s) => [s, under(layout(s))]).sort((a, b) => a[1] - b[1]);
  return fits[0][1] <= 2 && fits[1][1] >= 20 ? fits[0][0] : null;
}

/**
 * cityId -> who holds it: the scan's own points first, else the newest intel.sweep holders (city /
 * stronghold ids from world.get.city.stronghold.info are the same season table ids).
 */
export function ownersOf(lay, features, holders = []) {
  const m = new Map();
  if (!lay) return m;
  for (const h of holders) if (lay.byId.has(h.id)) m.set(h.id, { allianceId: h.allianceId, tag: h.tag, allianceName: h.allianceName, at: h.at });
  for (const f of features) if (SEASON_POINTS.has(f.type)) { const b = matches(lay, f); if (b) m.set(b.id, f); }
  return m;
}

/** Decodes the scan's gzip bitset of greened tiles (bit pointId - 1) into a Uint8Array of bits. */
export async function decodeGreen(b64) {
  const bytes = Uint8Array.from(atob(b64), (ch) => ch.charCodeAt(0));
  const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}
// ---- land ranking
// What holding a building is worth: level x 10 for a city or trade post, level x 5 for a dig site,
// plus its percentage buffs in points (+20% = 20). The capitol (Nuclear Furnace / Great Pyramid),
// cannons and missile factories score nothing - some servers pass the capitol round - but the
// ranking still says who holds it.
export const LAND_RULE = "level × 10 for a city or trade post, level × 5 for a dig site, plus 1 point per 1% of its buffs; the capitol and cannons are shown but not scored";
const UNSCORED = new Set([2, 3, 6]);
export const landPoints = (b) => (UNSCORED.has(b.type) ? 0
  : b.level * (b.type === 4 ? 5 : 10) + b.buffs.filter(([, v]) => v < 1).reduce((s, [, v]) => s + Math.round(v * 1000) / 10, 0));

/** Per server + alliance: score, what they hold, and their buffs summed by effect. */
export function landRanking(lay, holders) {
  const m = new Map();
  for (const h of holders) {
    const b = lay.byId.get(h.id);
    if (!b) continue;
    const k = `${h.server}|${h.allianceId}`;
    const r = m.get(k) || { server: h.server, allianceId: h.allianceId, tag: h.tag, name: h.allianceName, score: 0, held: [], capitol: false, cannons: 0, buffs: new Map(), res: 0, at: h.at };
    r.held.push(b);
    r.score += landPoints(b);
    r.res += b.res;
    if (b.type === 3) r.capitol = true;
    if (b.type === 2 || b.type === 6) r.cannons++;
    for (const [e, v] of b.buffs) r.buffs.set(e, (r.buffs.get(e) || 0) + v);
    m.set(k, r);
  }
  return [...m.values()].map((r) => ({ ...r, score: Math.round(r.score * 10) / 10 })).sort((a, b) => b.score - a.score);
}

/** "+25%" for a fraction, "+200" for a flat amount, "" for a flag-like 1 (a title such as Esteemed Governor). */
export const buffText = (v) => (v < 1 ? `+${Math.round(v * 1000) / 10}%` : v === 1 ? "" : `+${v}`);