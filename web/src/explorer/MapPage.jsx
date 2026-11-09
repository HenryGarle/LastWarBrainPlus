import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { allianceColour, href, when, int, big } from "./format.js";
import { api } from "../api.js";
import { describe, territory, hiveOf, scoreHive, plannerExport } from "./hive.js";
import { SEASONS, layout, detectSeason, ownersOf, decodeGreen } from "./seasons.js";

const MUD = [150, 110, 60];                                   // season 3 sand / contaminated land
const LEVEL_TINT = (lv) => `rgba(240,180,41,${0.012 * lv})`;   // zones warm up towards the capitol
const GLYPH = { 1: "square", 3: "capitol", 4: "diamond", 5: "circle", 2: "tri", 6: "tri" };

// Every alliance has its site-wide hashed colour; players in no alliance are a dark neutral.
const NONE = "#39414b";
const UNOWNED = "#8b97a6";
const MIN_S = 0.3, MAX_S = 40;   // pixels per tile
const CELL = 20;                  // heat map cell, in tiles
const MIN_HERO = [[0, "any"], [5e6, "5M+"], [10e6, "10M+"], [25e6, "25M+"], [50e6, "50M+"], [100e6, "100M+"]];
const MEMBERS = [["all", "all players"], ["in", "in an alliance"], ["none", "not in an alliance"]];
// Heat ramp: transparent → deep red → orange → pale yellow (sequential, reads on the dark map).
const heat = (t) => {
  const stops = [[0, [120, 20, 40]], [0.45, [220, 70, 30]], [0.75, [250, 160, 40]], [1, [255, 240, 170]]];
  let i = 1; while (i < stops.length - 1 && t > stops[i][0]) i++;
  const [a, ca] = stops[i - 1], [b, cb] = stops[i];
  const f = Math.min(1, Math.max(0, (t - a) / (b - a)));
  return ca.map((v, k) => Math.round(v + (cb[k] - v) * f));
};
const store = (k, v) => { try { if (v === undefined) return localStorage.getItem(k); localStorage.setItem(k, v); } catch { /* private window */ } return null; };
const download = (name, text) => {
  const a = document.createElement("a");
  a.href = URL.createObjectURL(new Blob([text], { type: "application/json" }));
  a.download = name; a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
};

export default function MapPage({ d, route, base = "#/maps" }) {
  const size = d.size || 1000;
  const [x0, y0, focusUid] = (route.extra || "").split("?")[0].split(",");
  const focus = x0 !== undefined && x0 !== "" ? { x: Number(x0), y: Number(y0), uid: focusUid || null } : null;
  const wrap = useRef(null);
  const canvas = useRef(null);
  const [dims, setDims] = useState({ w: 800, h: 600 });
  // { x, y } = the top-left corner in SCREEN tiles, s = px per tile. Game y runs up the screen, as
  // in the game and Cpt Hedge's planner, so screen y = size - game y (toScreenY / tileAt convert).
  const [view, setView] = useState(null);
  const [hover, setHover] = useState(null);
  const [cursor, setCursor] = useState(null);
  const [menu, setMenu] = useState(null);           // context menu for a clicked base
  const [picked, setPicked] = useState(() => new Set());   // alliances shown (none = all)
  const [minHero, setMinHero] = useState(0);
  const [minHq, setMinHq] = useState(0);
  const [members, setMembers] = useState("all");
  const [showHeat, setShowHeat] = useState(false);
  const [showLand, setShowLand] = useState(true);
  const [showPoints, setShowPoints] = useState(true);
  const [layoutSel, setLayoutSel] = useState(() => store(`map.layout.${d.server}`) || "auto");   // auto | none | 2 | 3
  const [showMud, setShowMud] = useState(true);
  const [mud, setMud] = useState(null);
  const [legendBy, setLegendBy] = useState("hero");
  const [q, setQ] = useState("");
  const [panel, setPanel] = useState(() => store("map.panel") !== "closed");
  const [hiveId, setHiveId] = useState(route.hive || null);   // alliance scored in the right-hand panel (#...?hive=id)
  const [favs, setFavs] = useState([]);
  const [note, setNote] = useState(null);
  const drag = useRef(null);
  const pointers = useRef(new Map());

  const tiles = useMemo(() => d.tiles.map(([x, y, uid, name, tag, allianceId, hq, hero]) => ({ x, y, uid, name, tag, allianceId, hq, hero: hero || 0 })), [d.tiles]);
  const points = useMemo(() => (d.features || []).map(([x, y, type, buildId, level, allianceId, tag, allianceName, label, holder]) => {
    const f = { x, y, type, buildId, level, allianceId, tag, allianceName, label, holder };
    return { ...f, ...describe(f) };
  }), [d.features]);
  // Season layout: detected from the scan's points unless picked; fixed zones and buildings from the game's tables.
  const detected = useMemo(() => detectSeason(points, tiles), [points, tiles]);
  const season = layoutSel === "auto" ? detected : layoutSel === "none" ? null : layoutSel;
  const lay = useMemo(() => (season ? layout(season) : null), [season]);
  const owners = useMemo(() => ownersOf(lay, points, d.holders || []), [lay, points, d.holders]);
  const matched = useMemo(() => new Set(owners.values()), [owners]);
  useEffect(() => { store(`map.layout.${d.server}`, layoutSel); }, [layoutSel, d.server]);
  // Season 3 mud = every tile the scan did not report as greened. Painted once into a map-sized image.
  useEffect(() => {
    setMud(null);
    if (!d.green || season !== "3") return undefined;
    let live = true;
    decodeGreen(d.green).then((bits) => {
      if (!live) return;
      const cv = document.createElement("canvas"); cv.width = size; cv.height = size;
      const g = cv.getContext("2d"), img = g.createImageData(size, size);
      for (let i = 0; i < size * size; i++) {
        if (bits[i >> 3] & (1 << (i & 7))) continue;
        const o = ((size - 1 - Math.floor(i / size)) * size + (i % size)) * 4;   // image row 0 = the top = game y size-1
        img.data[o] = MUD[0]; img.data[o + 1] = MUD[1]; img.data[o + 2] = MUD[2]; img.data[o + 3] = 255;
      }
      g.putImageData(img, 0, 0);
      setMud(cv);
    }).catch(() => {});
    return () => { live = false; };
  }, [d.green, season, size]);

  const land = useMemo(() => {
    const t = territory(points, size);
    if (!t.owner.some(Boolean)) return null;
    const cv = document.createElement("canvas");
    cv.width = t.n; cv.height = t.n;
    const g = cv.getContext("2d");
    t.owner.forEach((id, i) => { if (id) { g.fillStyle = allianceColour(id); g.fillRect(i % t.n, Math.floor(i / t.n), 1, 1); } });
    return { cv, cell: t.cell };
  }, [points, size]);

  const alliances = useMemo(() => {
    const m = new Map();
    for (const t of tiles) {
      if (!t.allianceId) continue;
      const a = m.get(t.allianceId) || { id: t.allianceId, tag: t.tag, n: 0, hero: 0, bases: [] };
      a.n++; a.hero += t.hero; a.bases.push(t); m.set(t.allianceId, a);
    }
    return [...m.values()].sort((a, b) => (legendBy === "hero" ? b.hero - a.hero : b.n - a.n));
  }, [tiles, legendBy]);
  const byId = useMemo(() => new Map(alliances.map((a) => [a.id, a])), [alliances]);

  // Only what passes the filters is drawn.
  const passes = useCallback((t) =>
    t.hero >= minHero && t.hq >= minHq
    && (members === "all" || (members === "in") === !!t.allianceId)
    && (picked.size === 0 || picked.has(t.allianceId)),
  [minHero, minHq, members, picked]);
  const shown = useMemo(() => tiles.filter(passes), [tiles, passes]);
  const shownHero = shown.reduce((s, t) => s + t.hero, 0);
  const cells = useMemo(() => {
    const m = new Map();
    for (const t of shown) { const k = `${Math.floor(t.x / CELL)},${Math.floor(t.y / CELL)}`; m.set(k, (m.get(k) || 0) + t.hero); }
    return m;
  }, [shown]);
  const heatMax = useMemo(() => Math.max(1, ...cells.values()), [cells]);
  // 10x10-tile buckets of what is drawn, so hover finds the nearest base without scanning thousands.
  const buckets = useMemo(() => {
    const b = new Map();
    for (const t of shown) { const k = `${Math.floor(t.x / 10)},${Math.floor(t.y / 10)}`; if (!b.has(k)) b.set(k, []); b.get(k).push(t); }
    return b;
  }, [shown]);
  const target = useMemo(() => focus && (tiles.find((t) => t.uid === focus.uid) || tiles.find((t) => t.x === focus.x && t.y === focus.y) || { ...focus, name: null }),
    [tiles, route.extra]);   // eslint-disable-line react-hooks/exhaustive-deps

  // Hive scores for the biggest alliances on the map, and the full breakdown for the chosen one.
  const scores = useMemo(() => [...alliances].sort((a, b) => b.n - a.n).slice(0, 15).map((a) => {
    const hive = hiveOf(a.bases);
    return { a, hive, s: scoreHive(hive) };
  }), [alliances]);
  const current = hiveId || (picked.size === 1 ? [...picked][0] : null) || scores[0]?.a.id || null;
  const hive = useMemo(() => {
    const a = byId.get(current);
    if (!a) return null;
    const h = hiveOf(a.bases);
    return { a, bases: h, s: scoreHive(h) };
  }, [byId, current]);

  const favSet = useMemo(() => new Set(favs.map((f) => f.uid)), [favs]);
  const loadFavs = useCallback(() => api.favourites().then((f) => setFavs(f.filter((x) => (x.kind || "player") === "player"))).catch(() => setFavs([])), []);
  useEffect(() => { loadFavs(); }, [loadFavs]);
  const toggleFav = async (t) => {
    const on = !favSet.has(t.uid);
    try { await api.favourite(t.uid, on); await loadFavs(); setNote(on ? `${t.name} added to favourites` : `${t.name} removed from favourites`); }
    catch (e) { setNote(e.message); }
  };
  useEffect(() => {
    const el = wrap.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setDims({ w: Math.round(el.clientWidth), h: Math.round(el.clientHeight) }));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  useEffect(() => { store("map.panel", panel ? "open" : "closed"); }, [panel]);
  useEffect(() => { if (!note) return; const t = setTimeout(() => setNote(null), 3500); return () => clearTimeout(t); }, [note]);
  useEffect(() => {
    if (!menu) return;
    const close = (e) => { if (e.type === "keydown" ? e.key === "Escape" : !e.target.closest?.(".map-menu")) setMenu(null); };
    window.addEventListener("pointerdown", close); window.addEventListener("keydown", close);
    return () => { window.removeEventListener("pointerdown", close); window.removeEventListener("keydown", close); };
  }, [menu]);

  const fit = useCallback(() => {
    const s = Math.min(dims.w, dims.h) / size;
    setView({ s, x: -(dims.w / s - size) / 2, y: -(dims.h / s - size) / 2 });
  }, [dims, size]);
  const centreOn = useCallback((t, s = 10) => setView({ s, x: t.x - dims.w / s / 2, y: size - t.y - dims.h / s / 2 }), [dims, size]);

  // First view: the linked player, zoomed in; otherwise the whole map.
  useEffect(() => {
    if (view || dims.w < 50) return;
    if (target) centreOn(target, 10); else fit();
  }, [dims, view, target, centreOn, fit]);

  useEffect(() => {
    const cv = canvas.current;
    if (!cv || !view) return;
    const dpr = window.devicePixelRatio || 1;
    cv.width = dims.w * dpr; cv.height = dims.h * dpr;
    const g = cv.getContext("2d");
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    const { s } = view;
    const px = (x) => (x - view.x) * s, py = (y) => (size - y - view.y) * s;   // py takes GAME y
    const sy = (y) => (y - view.y) * s;                                           // screen-space y
    g.fillStyle = "#0b0f14"; g.fillRect(0, 0, dims.w, dims.h);
    g.fillStyle = "#11161d"; g.fillRect(px(0), sy(0), size * s, size * s);

    // Season 3 mud, then the season's zones: level tint, the holder's colour, the borders.
    if (lay) {
      if (showMud && mud) { g.save(); g.globalAlpha = 0.22; g.imageSmoothingEnabled = false; g.drawImage(mud, px(0), sy(0), size * s, size * s); g.restore(); }
      for (const z of lay.zones) {
        const X = px(z.x0), Y = py(z.y1), W = (z.x1 - z.x0) * s, H = (z.y1 - z.y0) * s;
        if (X > dims.w || Y > dims.h || X + W < 0 || Y + H < 0) continue;
        const own = owners.get(z.building.id)?.allianceId;
        g.fillStyle = LEVEL_TINT(z.building.level); g.fillRect(X, Y, W, H);
        if (showLand && own) { g.globalAlpha = z.corner ? 0.16 : 0.12; g.fillStyle = allianceColour(own); g.fillRect(X, Y, W, H); g.globalAlpha = 1; }
        g.strokeStyle = z.corner ? "#4c5a6b" : "#34404e"; g.lineWidth = z.corner ? 1 : 1.5;
        g.strokeRect(Math.round(X) + 0.5, Math.round(Y) + 0.5, W, H);
      }
    }

    // Territory: a faint wash of the owner's colour, under everything else.
    if (showLand && land && !lay) {
      g.save(); g.globalAlpha = 0.1; g.imageSmoothingEnabled = false;
      g.translate(px(0), py(0)); g.scale(1, -1);          // image row 0 is game y 0, at the bottom
      g.drawImage(land.cv, 0, 0, land.cv.width * land.cell * s, land.cv.height * land.cell * s);
      g.restore();
    }

    // Grid: every 100 tiles always, every 10 once they are 4px apart, every tile from 12px.
    const lines = (step, stroke) => {
      g.strokeStyle = stroke; g.lineWidth = 1; g.beginPath();
      const a = Math.max(0, Math.floor(view.x / step) * step), b = Math.min(size, view.x + dims.w / s);
      for (let x = a; x <= b; x += step) { const X = Math.round(px(x)) + 0.5; g.moveTo(X, Math.max(0, sy(0))); g.lineTo(X, Math.min(dims.h, sy(size))); }
      const c = Math.max(0, Math.floor(view.y / step) * step), e = Math.min(size, view.y + dims.h / s);
      for (let y = c; y <= e; y += step) { const Y = Math.round(sy(y)) + 0.5; g.moveTo(Math.max(0, px(0)), Y); g.lineTo(Math.min(dims.w, px(size)), Y); }
      g.stroke();
    };
    if (s >= 12) lines(1, "#161c24");
    if (s >= 0.4) lines(10, "#1a212b");
    lines(100, "#2c3643");

    if (showHeat) {
      for (const [k, v] of cells) {
        const [cx, cy] = k.split(",").map(Number);
        const t = Math.sqrt(v / heatMax);
        if (t < 0.04) continue;
        const [r0, g0, b0] = heat(t);
        g.fillStyle = `rgba(${r0},${g0},${b0},${0.25 + 0.6 * t})`;
        g.fillRect(px(cx * CELL), py((cy + 1) * CELL), CELL * s + 0.5, CELL * s + 0.5);
      }
    }

    // Cities, alliance buildings and Marshal's Guard: their real footprint, outlined in the owner's colour.
    if (showPoints) {
      g.font = "11px system-ui, sans-serif"; g.textAlign = "center";
      // The season's fixed buildings: a glyph per kind, in the holder's colour.
      if (lay) {
        for (const b of lay.buildings) {
          const w = Math.max(b.type === 3 ? 12 : 6, b.size * s), X = px(b.x), Y = py(b.y), h = w / 2;
          if (X < -w || Y < -w || X > dims.w + w || Y > dims.h + w) continue;
          const own = owners.get(b.id);
          const col = own?.allianceId ? allianceColour(own.allianceId) : "#9aa6b4";
          g.strokeStyle = col; g.fillStyle = col; g.lineWidth = 1.5; g.globalAlpha = 0.3;
          g.beginPath();
          const shape = GLYPH[b.type];
          if (shape === "diamond") { g.moveTo(X, Y - h); g.lineTo(X + h, Y); g.lineTo(X, Y + h); g.lineTo(X - h, Y); g.closePath(); }
          else if (shape === "circle") g.arc(X, Y, h, 0, Math.PI * 2);
          else if (shape === "tri") { g.moveTo(X, Y - h); g.lineTo(X + h, Y + h); g.lineTo(X - h, Y + h); g.closePath(); }
          else g.rect(X - h, Y - h, w, w);
          g.fill(); g.globalAlpha = 1; g.stroke();
          if (shape === "capitol") { g.strokeRect(X - h - 3, Y - h - 3, w + 6, w + 6); }
          if (s >= 1.6 || (b.type === 3 && s >= 0.5)) {
            g.fillStyle = "#d8dee6";
            g.fillText(`${b.type === 1 || b.type === 4 || b.type === 5 ? `Lv.${b.level} ` : ""}${b.name}${own?.tag ? ` [${own.tag}]` : ""}`, X, Y - h - 4);
          }
        }
      }
      for (const f of points) {
        if (matched.has(f)) continue;   // drawn above as its season building
        const w = Math.max(6, f.size * s), X = px(f.x), Y = py(f.y);
        if (X < -w || Y < -w || X > dims.w + w || Y > dims.h + w) continue;
        const col = f.allianceId ? allianceColour(f.allianceId) : UNOWNED;
        g.globalAlpha = 0.28; g.fillStyle = col; g.fillRect(X - w / 2, Y - w / 2, w, w);
        g.globalAlpha = 1; g.strokeStyle = col; g.lineWidth = f.type === 59 ? 2.5 : 1.5;
        if (f.type === 59) { g.beginPath(); g.moveTo(X, Y - w / 2 - 3); g.lineTo(X + w / 2 + 3, Y); g.lineTo(X, Y + w / 2 + 3); g.lineTo(X - w / 2 - 3, Y); g.closePath(); g.stroke(); }
        else g.strokeRect(X - w / 2, Y - w / 2, w, w);
        if (s >= 2.5 || (f.building && s >= 1)) {
          g.fillStyle = "#d8dee6";
          g.fillText(f.tag ? `${f.name} [${f.tag}]` : f.name, X, Y - w / 2 - 4);
        }
      }
      g.textAlign = "start";
    }

    // Bases cover about 2x2 tiles; never smaller than 2px so a zoomed-out map still reads.
    const r = Math.max(2, s * 2);
    for (const t of shown) {
      const X = px(t.x), Y = py(t.y);
      if (X < -r || Y < -r || X > dims.w + r || Y > dims.h + r) continue;
      g.globalAlpha = showHeat ? 0.55 : 1;
      g.fillStyle = t.allianceId ? allianceColour(t.allianceId) : NONE;
      g.fillRect(X - r / 2, Y - r / 2, r, r);
    }
    g.globalAlpha = 1;

    // Favourites: a gold ring, drawn even when a filter hides the base.
    g.strokeStyle = "#f0b429"; g.lineWidth = 1.5;
    for (const t of tiles) if (favSet.has(t.uid)) { g.beginPath(); g.arc(px(t.x), py(t.y), Math.max(5, r * 0.95), 0, Math.PI * 2); g.stroke(); }

    // The scored hive: its holes (red) and the deliberate middle gap (dashed).
    if (panel && hive?.s) {
      g.strokeStyle = "#ff6b6b"; g.lineWidth = 1.5;
      for (const h of hive.s.holes) g.strokeRect(px(h.x) - r / 2, py(h.y) - r / 2, r, r);
      if (hive.s.gap) { g.setLineDash([3, 3]); g.strokeStyle = "#9fb3c8"; g.strokeRect(px(hive.s.gap.x) - r / 2, py(hive.s.gap.y) - r / 2, r, r); g.setLineDash([]); }
    }

    if (target) {
      const X = px(target.x), Y = py(target.y);
      g.strokeStyle = "#ffffff"; g.lineWidth = 2;
      g.beginPath(); g.arc(X, Y, Math.max(9, r * 1.4), 0, Math.PI * 2); g.stroke();
      g.strokeStyle = "rgba(255,255,255,.35)"; g.beginPath(); g.arc(X, Y, Math.max(16, r * 2.4), 0, Math.PI * 2); g.stroke();
    }
    if (hover && !hover.point) { g.strokeStyle = "#ffffff"; g.lineWidth = 1.5; g.strokeRect(px(hover.x) - r / 2 - 2, py(hover.y) - r / 2 - 2, r + 4, r + 4); }

    g.fillStyle = "#8b97a6"; g.font = "11px ui-monospace, Consolas, monospace";
    for (let x = 0; x <= size; x += 100) { const X = px(x); if (X > 20 && X < dims.w - 10) g.fillText(String(x), X + 3, 12); }
    for (let y = 0; y <= size; y += 100) { const Y = py(y); if (Y > 24 && Y < dims.h - 4) g.fillText(String(y), 3, Y - 3); }
  }, [view, dims, tiles, shown, points, land, favSet, hive, panel, target, hover, size, showHeat, showLand, showPoints, cells, heatMax, lay, owners, matched, mud, showMud]);
  const tileAt = (e) => {
    const rect = canvas.current.getBoundingClientRect();
    const mx = e.clientX - rect.left, my = e.clientY - rect.top;
    return { mx, my, x: view.x + mx / view.s, y: size - (view.y + my / view.s) };   // game coordinates
  };
  const zoomAt = (mx, my, factor) => setView((v) => {
    const s = Math.min(MAX_S, Math.max(MIN_S, v.s * factor));
    const tx = v.x + mx / v.s, ty = v.y + my / v.s;
    return { s, x: tx - mx / s, y: ty - my / s };
  });

  const ready = view !== null;
  useEffect(() => {
    const cv = canvas.current;
    if (!cv || !ready) return;
    const onWheel = (e) => {
      e.preventDefault();
      const rect = cv.getBoundingClientRect();
      zoomAt(e.clientX - rect.left, e.clientY - rect.top, Math.exp(-e.deltaY * 0.0015));
    };
    cv.addEventListener("wheel", onWheel, { passive: false });
    return () => cv.removeEventListener("wheel", onWheel);
  }, [ready]);

  const onDown = (e) => {
    canvas.current.setPointerCapture(e.pointerId);
    pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    drag.current = { x: e.clientX, y: e.clientY, moved: false };
  };
  const onMove = (e) => {
    if (!view) return;
    const pts = pointers.current;
    if (pts.has(e.pointerId) && pts.size === 2) {
      const [a, b] = [...pts.values()];
      const before = Math.hypot(a.x - b.x, a.y - b.y);
      pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
      const [c, d2] = [...pts.values()];
      const after = Math.hypot(c.x - d2.x, c.y - d2.y);
      const rect = canvas.current.getBoundingClientRect();
      if (before > 0) zoomAt((c.x + d2.x) / 2 - rect.left, (c.y + d2.y) / 2 - rect.top, after / before);
      if (drag.current) drag.current.moved = true;
      return;
    }
    if (drag.current && pts.has(e.pointerId)) {
      const dx = e.clientX - drag.current.x, dy = e.clientY - drag.current.y;
      if (Math.abs(dx) + Math.abs(dy) > 2) drag.current.moved = true;
      drag.current.x = e.clientX; drag.current.y = e.clientY;
      pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
      setView((v) => ({ ...v, x: v.x - dx / v.s, y: v.y - dy / v.s }));
      return;
    }
    const t = tileAt(e);
    setCursor({ x: Math.floor(t.x), y: Math.floor(t.y) });
    let best = null, bestD = Math.max(1.5, 6 / view.s);
    for (let bx = Math.floor(t.x / 10) - 1; bx <= Math.floor(t.x / 10) + 1; bx++)
      for (let by = Math.floor(t.y / 10) - 1; by <= Math.floor(t.y / 10) + 1; by++)
        for (const b of buckets.get(`${bx},${by}`) || []) {
          const dd = Math.hypot(b.x - t.x, b.y - t.y);
          if (dd < bestD) { best = b; bestD = dd; }
        }
    if (!best && showPoints && lay) {
      const r2 = (b) => Math.max(b.size / 2, 3 / view.s);
      const b = lay.buildings.find((x) => Math.abs(x.x - t.x) <= r2(x) && Math.abs(x.y - t.y) <= r2(x));
      if (b) {
        const own = owners.get(b.id);
        best = { point: true, x: b.x, y: b.y, name: `${b.type === 1 || b.type === 4 || b.type === 5 ? `Lv.${b.level} ` : ""}${b.name}`,
          allianceId: own?.allianceId || "", tag: own?.tag || "", allianceName: own?.allianceName || "", label: own?.label || "", seasonal: true };
      }
    }
    if (!best && showPoints) {
      const f = points.find((p) => Math.abs(p.x - t.x) <= Math.max(p.size / 2, 3 / view.s) && Math.abs(p.y - t.y) <= Math.max(p.size / 2, 3 / view.s));
      if (f) best = { ...f, point: true };
    }
    setHover(best ? { ...best, mx: t.mx, my: t.my } : null);
  };
  const onUp = (e) => {
    pointers.current.delete(e.pointerId);
    const wasDrag = drag.current?.moved;
    drag.current = null;
    if (!wasDrag && hover && !hover.point && e.button === 0) setMenu({ t: hover, mx: hover.mx, my: hover.my });
  };

  const hits = q.trim().length >= 2 ? tiles.filter((t) => t.name.toLowerCase().includes(q.trim().toLowerCase())).slice(0, 12) : [];
  const pickScan = (scan) => { window.location.hash = `${base}/${d.server}/${(route.extra || "").split("?")[0]}?scan=${scan}`; };
  const toggle = (id) => setPicked((p) => { const n = new Set(p); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const filtered = minHero > 0 || minHq > 0 || picked.size > 0 || members !== "all";
  const clearAll = () => { setPicked(new Set()); setMinHero(0); setMinHq(0); setMembers("all"); setQ(""); };

  const exportHive = async (copy) => {
    if (!hive?.bases.length) return;
    const lo = (k) => Math.min(...hive.bases.map((b) => b[k])) - 12, hi = (k) => Math.max(...hive.bases.map((b) => b[k])) + 12;
    const near = points.filter((f) => f.x >= lo("x") && f.x <= hi("x") && f.y >= lo("y") && f.y <= hi("y"));
    const text = JSON.stringify(plannerExport(hive.bases, near), null, 2);
    const name = `hive_${d.server}_${(hive.a.tag || "alliance").replace(/[^\w-]/g, "")}.json`;
    if (copy) {
      try { await navigator.clipboard.writeText(text); setNote("Copied - paste it into the planner's Import box"); }
      catch { download(name, text); setNote("Could not copy, so it was downloaded instead"); }
    } else { download(name, text); setNote(`Saved ${name} - load it with the planner's Import`); }
  };

  const m = menu?.t;
  const bar = (v) => <span className="hive-bar"><i style={{ width: `${Math.round(v * 100)}%` }} /></span>;
  return (
    <div className={`map-page ${panel ? "with-panel" : ""}`}>
      <div className="card map-side">
        <div className="small muted">Map of <a href={href.server(d.server)}>#{d.server}</a> · {int(tiles.length)} bases · {size}×{size} tiles</div>
        <label className="small muted" style={{ display: "block", marginTop: 8 }}>Scan</label>
        <select value={d.scan} onChange={(e) => pickScan(e.target.value)}>
          {d.scans.map((s) => <option key={s.scan} value={s.scan}>{when(s.at)} · {int(s.tiles)} bases</option>)}
        </select>
        <div className="search" style={{ marginTop: 10, width: "100%" }}>
          <input placeholder="find a base by name" value={q} onChange={(e) => setQ(e.target.value)} />
          {hits.length > 0 && (
            <div className="search-hits">
              {hits.map((t) => (
                <a key={t.uid} href="#" onClick={(e) => { e.preventDefault(); setQ(""); centreOn(t, 12); setHover(null); }}>
                  {t.name} <span className="muted small">{t.tag ? `[${t.tag}] ` : ""}{t.x}:{t.y}</span>
                </a>
              ))}
            </div>
          )}
        </div>
        <div className="row small" style={{ gap: 6, marginTop: 10, flexWrap: "wrap" }}>
          <button onClick={() => zoomAt(dims.w / 2, dims.h / 2, 1.6)} title="zoom in">+</button>
          <button onClick={() => zoomAt(dims.w / 2, dims.h / 2, 1 / 1.6)} title="zoom out">−</button>
          <button onClick={fit}>Whole map</button>
          {target && <button onClick={() => centreOn(target, 10)}>Back to {target.name || `${target.x}:${target.y}`}</button>}
        </div>

        <div className="map-filters">
          <label className={`toggle ${showHeat ? "on" : ""}`}>
            <input type="checkbox" checked={showHeat} onChange={(e) => setShowHeat(e.target.checked)} /> Heat map of hero power
          </label>
          {showHeat && <div className="heat-scale"><span>less</span><i /><span>more</span></div>}
          <label className="small muted">Map layout
            <select value={layoutSel} onChange={(e) => setLayoutSel(e.target.value)}>
              <option value="auto">auto{detected ? ` - ${SEASONS[detected].name}` : " - none recognised"}</option>
              <option value="none">none (plain grid)</option>
              {Object.entries(SEASONS).map(([k, v]) => <option key={k} value={k}>{v.name}</option>)}
            </select>
          </label>
          {season === "3" && (
            <label className="small muted"><input type="checkbox" checked={showMud} onChange={(e) => setShowMud(e.target.checked)} style={{ width: "auto" }} disabled={!mud} /> mud (not yet greened) {!d.green && <span>- none in this scan; recorded by map scans during season 3</span>}</label>
          )}
          <label className="small muted"><input type="checkbox" checked={showLand} onChange={(e) => setShowLand(e.target.checked)} style={{ width: "auto" }} disabled={!land && !(lay && owners.size)} /> territory {!land && !(lay && owners.size) && <span>(no owners in this scan)</span>}</label>
          <label className="small muted"><input type="checkbox" checked={showPoints} onChange={(e) => setShowPoints(e.target.checked)} style={{ width: "auto" }} disabled={!points.length && !lay} /> cities, alliance buildings, Marshal&apos;s Guard {!points.length && <span>(none in this scan)</span>}</label>
          <label className="small muted">Players
            <select value={members} onChange={(e) => setMembers(e.target.value)}>
              {MEMBERS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
            </select>
          </label>
          <label className="small muted">Hero power
            <select value={minHero} onChange={(e) => setMinHero(Number(e.target.value))}>
              {MIN_HERO.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
            </select>
          </label>
          <label className="small muted">HQ level
            <select value={minHq} onChange={(e) => setMinHq(Number(e.target.value))}>
              {[0, 15, 20, 25, 28, 30].map((v) => <option key={v} value={v}>{v ? `${v}+` : "any"}</option>)}
            </select>
          </label>
          <div className="small row" style={{ justifyContent: "space-between", gap: 6 }}>
            <span><strong>{int(shown.length)}</strong> of {int(tiles.length)} bases · <strong>{big(shownHero)}</strong> hero</span>
            <button className="small" onClick={clearAll} disabled={!filtered && !q}>Clear all</button>
          </div>
        </div>

        <div className="row small muted" style={{ margin: "12px 0 4px", justifyContent: "space-between" }}>
          <span>Alliances - click to show only them</span>
          <select value={legendBy} onChange={(e) => setLegendBy(e.target.value)} style={{ width: "auto" }}>
            <option value="hero">by hero power</option>
            <option value="n">by bases</option>
          </select>
        </div>
        <div className="map-legend">
          {alliances.slice(0, 40).map((a) => (
            <button key={a.id} className={picked.has(a.id) ? "on" : ""} onClick={() => toggle(a.id)} title={`${a.n} bases · ${big(a.hero)} hero power`}>
              <i style={{ background: allianceColour(a.id) }} />{a.tag || "?"} <span className="muted">{legendBy === "hero" ? big(a.hero) : a.n}</span>
            </button>
          ))}
        </div>
        <p className="muted small" style={{ marginTop: 10, marginBottom: 0 }}>
          Drag to move, scroll or pinch to zoom, double-click to zoom in, click a base for options.
          {cursor && <><br />Cursor {cursor.x}:{cursor.y}</>}
        </p>
      </div>

      <div className="card map-card">
        <div className="map-wrap" ref={wrap}>
          <canvas ref={canvas} style={{ width: dims.w, height: dims.h, cursor: hover ? "pointer" : "grab", touchAction: "none", display: "block" }}
            onPointerDown={onDown} onPointerMove={onMove} onPointerUp={onUp} onPointerCancel={onUp}
            onPointerLeave={() => { setHover(null); setCursor(null); }}
            onDoubleClick={(e) => { const t = tileAt(e); zoomAt(t.mx, t.my, 2); }} />
          {hover && !menu && (
            <div className="tooltip" style={{ left: Math.min(hover.mx + 14, dims.w - 220), top: hover.my + 10, transform: "none" }}>
              {hover.point ? (
                <>
                  <strong>{hover.name}</strong> {hover.tag && <span className="tag" style={{ "--al": allianceColour(hover.allianceId) }}>{hover.tag}</span>}
                  <div className="small muted">{hover.x}:{hover.y}{hover.allianceName ? ` · ${hover.allianceName}` : hover.allianceId ? "" : hover.seasonal && !d.features?.length ? " · no owner data in this scan" : " · unclaimed"}{hover.label ? ` · ${hover.label}` : ""}</div>
                </>
              ) : (
                <>
                  <strong>{favSet.has(hover.uid) ? "★ " : ""}{hover.name}</strong> {hover.tag && <span className="tag" style={{ "--al": allianceColour(hover.allianceId) }}>{hover.tag}</span>}
                  <div className="small muted">{hover.x}:{hover.y} · HQ {hover.hq} · {big(hover.hero)} hero power</div>
                </>
              )}
            </div>
          )}
          {menu && (
            <div className="map-menu" style={{ left: Math.min(menu.mx + 8, dims.w - 230), top: Math.min(menu.my + 8, dims.h - 250) }}>
              <div className="map-menu-head"><strong>{m.name}</strong> {m.tag && <span className="tag" style={{ "--al": allianceColour(m.allianceId) }}>{m.tag}</span>}
                <div className="small muted">{m.x}:{m.y} · HQ {m.hq} · {big(m.hero)} hero</div></div>
              <a href={href.player(m.uid)} onClick={() => setMenu(null)}>Open player</a>
              <button onClick={() => { toggleFav(m); setMenu(null); }}>{favSet.has(m.uid) ? "★ Remove from favourites" : "☆ Add to favourites"}</button>
              {m.allianceId && <button onClick={() => { setPicked(new Set([m.allianceId])); setMenu(null); }}>Show only [{m.tag}]</button>}
              {m.allianceId && <button onClick={() => { setHiveId(m.allianceId); setPanel(true); setMenu(null); }}>Score [{m.tag}]&apos;s hive</button>}
              {m.allianceId && <a href={href.alliance(m.allianceId)} onClick={() => setMenu(null)}>Open alliance</a>}
              <button onClick={() => { navigator.clipboard?.writeText(`X:${m.x} Y:${m.y}`); setNote(`Copied X:${m.x} Y:${m.y}`); setMenu(null); }}>Copy coordinates</button>
            </div>
          )}
          {note && <div className="map-note">{note}</div>}
          <button className="map-panel-toggle" onClick={() => setPanel((p) => !p)} title={panel ? "fold the panel away" : "open the panel"}>{panel ? "›" : "‹"}</button>
        </div>
      </div>
      {panel && (
        <div className="card map-right">
          <div className="row" style={{ justifyContent: "space-between" }}>
            <h3 style={{ margin: 0 }}>Hive</h3>
            <button className="small" onClick={() => setPanel(false)} title="fold away">Hide ›</button>
          </div>
          <select value={current || ""} onChange={(e) => setHiveId(e.target.value)} style={{ width: "100%", marginTop: 6 }}>
            {[...alliances].sort((a, b) => b.n - a.n).map((a) => <option key={a.id} value={a.id}>[{a.tag}] · {a.n} bases</option>)}
          </select>
          {hive && (hive.s ? (
            <div className="hive-card">
              <div className="hive-score" style={{ "--al": allianceColour(hive.a.id) }}>
                <span className="big">{hive.s.score}</span><span className="muted small">/ 100 uniformity</span>
              </div>
              <div className="small hive-rows">
                <span>Even spacing</span>{bar(hive.s.spacing)}
                <span>Lined up</span>{bar(hive.s.alignment)}
                <span>No holes</span>{bar(hive.s.filled)}
              </div>
              <div className="small muted">
                {hive.s.n} of {hive.a.n} bases in the hive, about every {hive.s.pitch} tiles.
                {" "}{hive.s.holes.length ? `${hive.s.holes.length} hole${hive.s.holes.length > 1 ? "s" : ""} (red on the map)` : "No holes"}
                {hive.s.gap ? "; one middle gap left as deliberate (dashed)." : "."}
                {hive.a.n - hive.s.n > 0 && ` ${hive.a.n - hive.s.n} base${hive.a.n - hive.s.n > 1 ? "s" : ""} outside it.`}
              </div>
              <div className="row small" style={{ gap: 6, flexWrap: "wrap", marginTop: 8 }}>
                <button onClick={() => { const c = hive.bases; centreOn({ x: c.reduce((s, b) => s + b.x, 0) / c.length, y: c.reduce((s, b) => s + b.y, 0) / c.length }, 6); }}>Go to hive</button>
                <button onClick={() => setPicked(new Set([hive.a.id]))}>Show only them</button>
              </div>
              <div className="hive-export">
                <div className="small"><strong>Export to Cpt Hedge&apos;s planner</strong></div>
                <div className="small muted">The hive&apos;s bases (with names) and any alliance buildings beside it, in the planner&apos;s own format. In the planner: Import / Export → Import.</div>
                <div className="row small" style={{ gap: 6, marginTop: 6 }}>
                  <button className="primary" onClick={() => exportHive(false)}>Download .json</button>
                  <button onClick={() => exportHive(true)}>Copy</button>
                  <a href="https://cpt-hedge.com/maps/planner" target="_blank" rel="noreferrer">open planner ↗</a>
                </div>
              </div>
            </div>
          ) : <p className="small muted">Fewer than 5 bases together - nothing to score.</p>)}

          <h4 style={{ margin: "14px 0 4px" }}>Hive scores here <span className="muted small">(hives of 10+)</span></h4>
          <table className="small hive-table"><tbody>
            {scores.filter((r) => r.s && r.s.n >= 10).sort((a, b) => b.s.score - a.s.score).map((r) => (
              <tr key={r.a.id} className={r.a.id === current ? "on" : ""} onClick={() => setHiveId(r.a.id)}>
                <td><i className="dot" style={{ background: allianceColour(r.a.id) }} />{r.a.tag}</td>
                <td className="muted">{r.s.n}/{r.a.n}</td>
                <td style={{ textAlign: "right" }}><strong>{r.s.score}</strong></td>
              </tr>
            ))}
          </tbody></table>

          <h4 style={{ margin: "14px 0 4px" }}>Favourites</h4>
          {favs.length === 0 && <p className="small muted" style={{ margin: 0 }}>None yet - click a base and choose Add to favourites. They are kept for your sign-in.</p>}
          <div className="fav-list">
            {favs.map((f) => {
              const here = tiles.find((t) => t.uid === f.uid);
              return (
                <div key={f.uid} className="small fav-row">
                  {here ? <a href="#" onClick={(e) => { e.preventDefault(); centreOn(here, 12); }}>★ {f.name || here.name}</a> : <a href={href.player(f.uid)}>★ {f.name || f.uid}</a>}
                  <span className="muted">{f.tag ? `[${f.tag}] ` : ""}{here ? `${here.x}:${here.y}` : f.server ? `#${f.server}` : ""}</span>
                  <button className="link" title="remove" onClick={() => toggleFav({ uid: f.uid, name: f.name || f.uid })}>×</button>
                </div>
              );
            })}
          </div>
          {points.length > 0 && (
            <>
              <h4 style={{ margin: "14px 0 4px" }}>Alliance points</h4>
              <div className="small muted">{points.filter((p) => p.allianceId).length} of {points.length} held · {points.filter((p) => p.type === 59).length} Marshal&apos;s Guard</div>
            </>
          )}
        </div>
      )}
    </div>
  );
}