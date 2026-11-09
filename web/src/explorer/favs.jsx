import { useEffect, useState } from "react";

// Favourite players, alliances and servers of whoever is signed in (/api/me/favourites). One
// shared list for every star on the page; the search API puts favourites first on its own.
let cache = null;
let loading = null;
const subs = new Set();
const load = () => (loading ||= fetch("/api/me/favourites").then((r) => (r.ok ? r.json() : [])).catch(() => [])
  .then((x) => { cache = x; loading = null; subs.forEach((f) => f(x)); return x; }));

export function useFavourites() {
  const [favs, setFavs] = useState(cache || []);
  useEffect(() => { subs.add(setFavs); load(); return () => { subs.delete(setFavs); }; }, []);
  return favs;
}

export const isFav = (favs, kind, id) => favs.some((f) => (f.kind || "player") === kind && String(f.uid) === String(id));

export async function setFav(kind, id, on) {
  await fetch(`/api/me/favourites/${encodeURIComponent(id)}?kind=${kind}`, { method: on ? "PUT" : "DELETE" });
  await load();
}

/** ☆ / ★ toggle for one player, alliance or server. */
export function FavStar({ kind, id }) {
  const favs = useFavourites();
  const on = isFav(favs, kind, id);
  return (
    <button className={`fav-star${on ? " on" : ""}`} onClick={() => setFav(kind, id, !on)}
      title={on ? "Remove from favourites" : "Add to favourites - favourites come first in every search"}>
      {on ? "★ Favourite" : "☆ Favourite"}
    </button>
  );
}

/** Search as you type, with the favourites as quick picks when the box is first clicked (empty). */
export function useSearch(q, focused, delay = 250) {
  const [hits, setHits] = useState(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    const t = q.trim();
    if (t.length === 1 || (t.length === 0 && !focused)) { setHits(null); setBusy(false); return; }
    const ctl = new AbortController();
    setBusy(true);
    const timer = setTimeout(() => {
      fetch(`/api/x/search?q=${encodeURIComponent(t)}`, { signal: ctl.signal })
        .then((r) => r.json()).then((h) => { setHits(h); setBusy(false); })
        .catch((e) => { if (e.name !== "AbortError") setBusy(false); });
    }, t.length ? delay : 0);
    return () => { ctl.abort(); clearTimeout(timer); };
  }, [q, focused, delay]);
  return [hits, busy];
}

export const Star = ({ on }) => (on ? <span className="fav-mark" title="favourite">★ </span> : null);
