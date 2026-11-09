// Numbers in this game run from tens to hundreds of billions; show them the way players say them.
export const big = (n) => {
  if (n === null || n === undefined || n === "") return "–";
  const v = Number(n);
  const a = Math.abs(v);
  if (a >= 1e12) return (v / 1e12).toFixed(2) + "T";
  if (a >= 1e9) return (v / 1e9).toFixed(a >= 1e11 ? 0 : 2) + "B";
  if (a >= 1e6) return (v / 1e6).toFixed(a >= 1e8 ? 0 : 1) + "M";
  if (a >= 1e3) return (v / 1e3).toFixed(a >= 1e5 ? 0 : 1) + "K";
  return String(Math.round(v));
};

export const int = (n) => (n === null || n === undefined ? "–" : Number(n).toLocaleString());

export const pct = (part, whole) =>
  whole ? `${Math.round((100 * Number(part)) / Number(whole))}%` : "–";

export const when = (iso) => {
  if (!iso) return "–";
  const d = new Date(iso);
  return d.toLocaleDateString(undefined, { day: "numeric", month: "short" }) +
    " " + d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
};

export const ago = (iso) => {
  if (!iso) return "–";
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 90) return "just now";
  if (s < 5400) return `${Math.round(s / 60)} min ago`;
  if (s < 129600) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} days ago`;
};

// Links are plain hash routes so every page is bookmarkable and the back button works.
export const href = {
  server: (id) => `#/x/server/${id}`,
  alliance: (id) => `#/x/alliance/${encodeURIComponent(id)}`,
  player: (uid) => `#/x/player/${uid}`,
  home: () => "#/x",
};

export const originOf = (uid) => (uid && uid.length >= 6 ? Number(uid.slice(-6)) : null);

// One colour per alliance, everywhere: hashed from the alliance ID (never the tag - tags repeat
// across servers and change), so the same alliance is the same colour on the map, in charts and
// on every tag. Hue spreads round the wheel; three lightness steps separate neighbouring hues.
// Blues/purples read darker on the dark surface, so they get a lift.
// ponytail: HSL, not OKLCH - close enough for categorical marks; switch if two collide badly.
const colourCache = new Map();
export const allianceColour = (id) => {
  if (!id) return null;
  let c = colourCache.get(id);
  if (c) return c;
  let h = 0x811c9dc5;                                    // FNV-1a
  for (let i = 0; i < id.length; i++) h = Math.imul(h ^ id.charCodeAt(i), 0x01000193) >>> 0;
  const hue = h % 360;
  const light = [58, 66, 74][(h >>> 12) % 3] + (hue >= 200 && hue <= 290 ? 8 : 0);
  c = `hsl(${hue} ${hue >= 40 && hue <= 70 ? 70 : 62}% ${light}%)`;
  colourCache.set(id, c);
  return c;
};
