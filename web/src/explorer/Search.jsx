import { useEffect, useRef, useState } from "react";
import { allianceColour, href } from "./format.js";
import { useSearch, Star } from "./favs.jsx";

// The one search box, in the top banner so it is there from every tab. Picking a hit is just a
// hash link, which the app turns into the Explorer. "/" focuses it, Enter opens the first hit.
// Servers shown are the ones players are FROM (their home), never where a base is visiting.
// Favourites come first (the API orders them); clicking into the empty box lists them as quick picks.
export default function Search() {
  const [q, setQ] = useState("");
  const [open, setOpen] = useState(false);
  const [hits, busy] = useSearch(q, open, 200);
  const input = useRef(null);
  const list = useRef(null);

  useEffect(() => {
    const clear = () => { setQ(""); input.current?.blur(); };
    const key = (e) => {
      if (e.key === "/" && !/INPUT|TEXTAREA|SELECT/.test(document.activeElement?.tagName)) { e.preventDefault(); input.current?.focus(); }
    };
    window.addEventListener("hashchange", clear);
    window.addEventListener("keydown", key);
    return () => { window.removeEventListener("hashchange", clear); window.removeEventListener("keydown", key); };
  }, []);

  const onKey = (e) => {
    if (e.key === "Escape") { setQ(""); input.current?.blur(); }
    if (e.key === "Enter") list.current?.querySelector("a")?.click();
  };
  const none = hits && hits.servers.length + hits.alliances.length + hits.players.length + (hits.renamed || []).length === 0;

  return (
    <div className="search top-search" onFocus={() => setOpen(true)} onBlur={() => setTimeout(() => setOpen(false), 150)}>
      <input ref={input} placeholder="find a player, alliance, uid or server…  ( / )" value={q}
        onChange={(e) => setQ(e.target.value)} onKeyDown={onKey} />
      {open && (busy || hits) && (
        <div className="search-hits" ref={list} onMouseDown={(e) => e.preventDefault()}>
          {busy && !hits && <div className="muted small search-busy">Searching…</div>}
          {hits && <>
            {hits.quick && !none && <div className="search-head">Favourites</div>}
            {hits.servers.map((s) => <a key={`s${s.server}`} href={href.server(s.server)}><Star on={s.fav} />Server #{s.server}</a>)}
            {hits.alliances.map((a) => (
              <a key={a.allianceId} href={href.alliance(a.allianceId)}>
                <Star on={a.fav} /><span className="tag" style={{ "--al": allianceColour(a.allianceId) }}>{a.tag}</span>{" "}
                <span className="muted small">#{a.server} · {a.seenMembers} members</span>
              </a>
            ))}
            {hits.players.map((p) => (
              <a key={p.uid} href={href.player(p.uid)}>
                <Star on={p.fav} />{p.name} <span className="muted small">{p.tag ? `[${p.tag}] ` : ""}#{p.server}</span>
              </a>
            ))}
            {(hits.renamed || []).map((r) => (
              <a key={`r${r.id}`} href={href.player(r.id)}>{r.new} <span className="muted small">was {r.old}</span></a>
            ))}
            {none && hits.quick && <div className="muted small" style={{ padding: 8 }}>No favourites yet - use ☆ Favourite on a player, alliance or server page.</div>}
            {none && !hits.quick && !busy && <div className="muted small" style={{ padding: 8 }}>No match.</div>}
          </>}
        </div>
      )}
    </div>
  );
}
