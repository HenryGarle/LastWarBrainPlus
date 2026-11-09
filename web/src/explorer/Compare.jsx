import { useEffect, useMemo, useState } from "react";
import { MultiLineChart } from "./charts.jsx";
import { allianceColour, big, int, when, href } from "./format.js";
import { ServerLink, Tag, PlayerLink } from "./bits.jsx";
import { useSearch, Star } from "./favs.jsx";

const COLORS = ["var(--series-1)", "var(--series-2)", "var(--series-3)", "var(--series-4)"];
const KINDS = [["servers", "Servers"], ["alliances", "Alliances"], ["players", "Players"]];
const FMT = { big, int, rank: (v) => `#${Math.round(v)}`, stage: (v) => String(Math.round(v)) };
export const compareHref = (kind, ids) => `#/x/compare/${kind}/${ids.map(encodeURIComponent).join(",")}`;

const last = (pts) => (pts && pts.length ? pts[pts.length - 1] : null);
const prevOf = (pts) => (pts && pts.length > 1 ? pts[pts.length - 2] : null);

// Picks the entities: a search for each kind (a number for servers); favourites first, and listed
// as quick picks when the empty box is clicked.
function Adder({ kind, onAdd }) {
  const [q, setQ] = useState("");
  const [focused, setFocused] = useState(false);
  const [found] = useSearch(q, focused, 300);
  const hits = !found ? []
    : kind === "players" ? found.players.map((p) => ({ id: p.uid, fav: p.fav, text: `${p.name} ${p.tag ? `[${p.tag}]` : ""} #${p.server}` }))
    : kind === "alliances" ? found.alliances.map((a) => ({ id: a.allianceId, fav: a.fav, text: `${a.tag} #${a.server} · ${a.seenMembers} seen` }))
    : found.servers.map((s) => ({ id: String(s.server), fav: s.fav, text: `#${s.server}` }));
  const add = (id) => { onAdd(id); setQ(""); };
  return (
    <form className="cmp-add" onSubmit={(e) => { e.preventDefault(); if (kind === "servers" && /^\d+$/.test(q.trim())) add(q.trim()); }}
      onFocus={() => setFocused(true)} onBlur={() => setTimeout(() => setFocused(false), 150)}>
      <input value={q} onChange={(e) => setQ(e.target.value)}
        placeholder={kind === "servers" ? "add a server, e.g. 1972" : `add ${kind === "players" ? "a player (name or uid)" : "an alliance (tag)"}`} />
      {focused && hits.length > 0 && (
        <div className="search-hits" onMouseDown={(e) => e.preventDefault()}>
          {hits.map((h) => <a key={h.id} href="#" onClick={(e) => { e.preventDefault(); add(h.id); }}><Star on={h.fav} />{h.text}</a>)}
        </div>
      )}
    </form>
  );
}

export default function Compare({ d, route }) {
  const kind = route.id || "servers";
  const ids = route.extra ? route.extra.split(",").map(decodeURIComponent).filter(Boolean) : [];
  const go = (next) => { window.location.hash = compareHref(kind, next); };
  const entities = d?.entities || [];
  const metrics = d?.metrics || [];
  const [pick, setPick] = useState(null);
  // Hero power first where the entity has it (the site's default measure).
  const picked = metrics.find((m) => m.key === pick) || ["b13_sum", "hero_sum", "b4_sum"].map((k) => metrics.find((m) => m.key === k)).find(Boolean) || metrics[0];
  // Series slots, even for alliances: hashed alliance hues can collide, and a comparison chart must
  // separate its lines. The alliance colour still shows on each tag.
  const color = useMemo(() => Object.fromEntries(ids.map((id, i) => [id, COLORS[i % 4]])), [ids.join(",")]);

  return (
    <div className="grid">
      <div className="card">
        <div className="cmp-tabs">
          {KINDS.map(([k, label]) => <a key={k} className={k === kind ? "on" : ""} href={compareHref(k, [])}>{label}</a>)}
        </div>
        <div className="cmp-picks">
          {entities.map((e) => (
            <span key={e.key} className="cmp-chip">
              <i className="swatch" style={{ background: color[e.key] }} />
              {kind === "servers" ? <ServerLink id={e.server} /> : kind === "alliances"
                ? <a href={href.alliance(e.key)}>{e.label}</a> : <PlayerLink uid={e.key} name={e.label} />}
              <button title="remove" onClick={() => go(ids.filter((x) => x !== e.key))}>✕</button>
            </span>
          ))}
          {ids.length < 4 && <Adder kind={kind} onAdd={(id) => !ids.includes(id) && go([...ids, id])} />}
        </div>
        {kind === "alliances" && ids.length >= 2 && (
          <p className="small"><a className="action primary" href={`#/war/${ids.slice(0, 2).map(encodeURIComponent).join(",")}`}>⚔ War game: {entities[0]?.label || "A"} vs {entities[1]?.label || "B"}</a></p>
        )}
        {ids.length === 0 && <p className="muted small">Pick up to four {kind} to compare. Every measure is kept over time, so each side shows as it was at every snapshot.</p>}
      </div>


      {entities.length > 0 && metrics.length > 0 && (
        <>
          <div className="card">
            <h2>{picked.label}</h2>
            <MultiLineChart format={FMT[picked.format] || big} label={picked.label}
              series={entities.map((e) => ({
                key: e.key, label: e.label, color: color[e.key],
                points: (picked.series[e.key] || []).map((p) => ({ x: p.at, y: p.v })),
              }))} />
            {picked.better === "low" && <p className="muted small">Lower is better on this one.</p>}
          </div>

          <div className="card">
            <h2>Every measure, latest value - pick one to chart it</h2>
            <div style={{ overflowX: "auto" }}>
              <table className="cmp-table">
                <thead>
                  <tr>
                    <th>measure</th>
                    {entities.map((e) => <th key={e.key} className="num"><i className="swatch" style={{ background: color[e.key] }} />{e.label}</th>)}
                  </tr>
                </thead>
                <tbody>
                  {metrics.map((m) => {
                    const f = FMT[m.format] || big;
                    const vals = entities.map((e) => last(m.series[e.key])?.v);
                    const known = vals.filter((v) => v !== undefined && v !== null);
                    const best = known.length > 1 ? (m.better === "low" ? Math.min(...known) : Math.max(...known)) : null;
                    return (
                      <tr key={m.key} className={`pick ${m.key === picked.key ? "on" : ""}`} onClick={() => setPick(m.key)}>
                        <td>{m.label}</td>
                        {entities.map((e, i) => {
                          const pt = last(m.series[e.key]);
                          const before = prevOf(m.series[e.key]);
                          const isBest = best !== null && vals[i] === best;
                          return (
                            <td key={e.key} className={`num ${isBest ? "best" : ""}`} title={pt ? `as of ${when(pt.at)}` : "no data"}>
                              {pt ? f(pt.v) : <span className="muted">–</span>}
                              {isBest && <span className="best-mark">best</span>}
                              {pt && before && pt.v !== before.v && (
                                <div className={`small ${(pt.v > before.v) === (m.better !== "low") ? "up" : "down"}`}>
                                  {pt.v > before.v ? "▲" : "▼"} {f(Math.abs(pt.v - before.v))}
                                </div>
                              )}
                            </td>
                          );
                        })}
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            <p className="muted small">The arrow is the change since that entity's previous snapshot of the same measure. "Risen since last board" counts players whose value went UP - the only proof of activity.</p>
          </div>

          <div className="cmp-cols">
            {entities.map((e) => (
              <div key={e.key} className="card" style={{ borderTop: `3px solid ${color[e.key]}` }}>
                <h2>{e.label}</h2>
                {kind === "servers" && (
                  <>
                    <div className="muted small" style={{ marginBottom: 6 }}>Top alliances on the newest alliance power board</div>
                    {(e.alliances || []).map((a) => (
                      <div key={a.allianceId} className="row small" style={{ justifyContent: "space-between", gap: 8 }}>
                        <span><Tag id={a.allianceId} tag={a.tag} /> <span className="muted">{a.name}</span></span>
                        <span className="num">{big(a.power)} <span className="muted">· {a.members ?? "?"}</span></span>
                      </div>
                    ))}
                    {!(e.alliances || []).length && <p className="muted small">No alliance board saved yet.</p>}
                  </>
                )}
                {kind === "alliances" && (
                  <>
                    <div className="muted small" style={{ marginBottom: 6 }}>
                      {e.name} · on <ServerLink id={e.server} /> · strongest members as of {when(e.at)}
                    </div>
                    {(e.members || []).map((m) => (
                      <div key={m.uid} className="row small" style={{ justifyContent: "space-between", gap: 8 }}>
                        <PlayerLink uid={m.uid} name={m.name} /><span className="num">{big(m.power)}</span>
                      </div>
                    ))}
                  </>
                )}
                {kind === "players" && (
                  <div className="small" style={{ display: "grid", gap: 4 }}>
                    <span>from <ServerLink id={e.home} />{e.based && e.based !== e.home && <span className="warn-text"> · base on <ServerLink id={e.based} /></span>}</span>
                    <span>alliance <Tag id={e.allianceId} tag={e.tag} /></span>
                    <span>created on <ServerLink id={e.origin} /></span>
                    {e.level ? <span>HQ {e.level}</span> : null}
                    <span className="muted mono">{e.uid}</span>
                  </div>
                )}
                <p style={{ marginTop: 8 }}>
                  <a className="small" href={kind === "servers" ? href.server(e.server) : kind === "alliances" ? href.alliance(e.key) : href.player(e.key)}>open page →</a>
                </p>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  );
}
