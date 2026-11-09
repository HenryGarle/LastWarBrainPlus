import { useEffect, useState } from "react";
import { Matchup } from "../explorer/Intel.jsx";
import { allianceColour, int } from "../explorer/format.js";
import { setTitle } from "../nav.js";
import { useSearch, Star } from "../explorer/favs.jsx";

// #/war/<allianceA>,<allianceB>   the war game between two alliances
const parse = () => {
  const [path, qs = ""] = (window.location.hash || "#/war").split("?");
  const ids = (path.split("/")[2] || "").split(",").map(decodeURIComponent);
  return { a: ids[0] || "", b: ids[1] || "", qs };
};
const go = (a, b) => { window.location.hash = `#/war/${[a, b].map(encodeURIComponent).join(",")}`; };

/** One side: a search for an alliance by tag, or the picked one with a change button. */
function Side({ label, id, info, onPick, colour }) {
  const [q, setQ] = useState("");
  const [focused, setFocused] = useState(false);
  const [found] = useSearch(q, focused);   // favourite alliances first; the quick picks when first clicked
  const hits = found?.alliances || [];
  return (
    <div className="war-side" style={{ "--side": colour }}>
      <div className="war-side-label">{label}</div>
      {id && info ? (
        <div className="war-picked">
          <span className="tag big" style={{ "--al": allianceColour(id) }}>{info.tag}</span>
          <span className="muted small">#{info.server}</span>
          <button onClick={() => onPick("")}>change</button>
        </div>
      ) : (
        <div className="search" style={{ width: "100%" }} onFocus={() => setFocused(true)} onBlur={() => setTimeout(() => setFocused(false), 150)}>
          <input autoFocus={!id} placeholder="alliance tag, e.g. SaFa" value={q} onChange={(e) => setQ(e.target.value)} />
          {focused && hits.length > 0 && (
            <div className="search-hits" onMouseDown={(e) => e.preventDefault()}>
              {hits.slice(0, 12).map((h) => (
                <a key={h.allianceId} href="#" onClick={(e) => { e.preventDefault(); setQ(""); onPick(h.allianceId, h); }}>
                  <Star on={h.fav} /><span className="tag" style={{ "--al": allianceColour(h.allianceId) }}>{h.tag}</span> #{h.server}
                  <span className="muted small"> · {int(h.seenMembers)} members</span>
                </a>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

export default function War() {
  const [r, setR] = useState(parse);
  const [info, setInfo] = useState({});
  useEffect(() => {
    setTitle("War game");
    const on = () => setR(parse());
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);
  // Names for ids that came in the link.
  useEffect(() => {
    for (const id of [r.a, r.b]) {
      if (!id || info[id]) continue;
      fetch(`/api/x/alliance/${encodeURIComponent(id)}`).then((x) => x.json())
        .then((d) => setInfo((m) => ({ ...m, [id]: { tag: d.tag || id.slice(0, 6), server: d.server } }))).catch(() => {});
    }
  }, [r.a, r.b]);   // eslint-disable-line react-hooks/exhaustive-deps
  const pick = (side) => (id, h) => {
    if (h) setInfo((m) => ({ ...m, [id]: { tag: h.tag, server: h.server } }));
    go(side === "a" ? id : r.a, side === "b" ? id : r.b);
  };
  return (
    <div className="grid">
      <div className="page-head">
        <div>
          <h1 className="page-title">War game</h1>
          <p className="muted">Pick two alliances: who breaks whose building, when each side is strongest, and how the rallies and garrisons stack up.</p>
        </div>
      </div>
      <div className="card war-pick">
        <Side label="Attacker / side A" id={r.a} info={info[r.a]} onPick={pick("a")} colour="var(--series-1)" />
        <button className="war-swap" title="swap sides" disabled={!r.a || !r.b} onClick={() => go(r.b, r.a)}>⇄</button>
        <Side label="Defender / side B" id={r.b} info={info[r.b]} onPick={pick("b")} colour="var(--series-2)" />
      </div>
      {r.a && r.b ? <Matchup a={r.a} b={r.b} /> : <p className="muted">Choose both sides to run the war game.</p>}
    </div>
  );
}
