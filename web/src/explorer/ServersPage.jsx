import { useMemo, useState } from "react";
import { int, big, when } from "./format.js";
import { ServerLink } from "./bits.jsx";
import { useJson } from "./PlayerTabs.jsx";
import { useHashParam } from "../nav.js";

// Every server the game knows (/api/x/servers), grouped by the game's own blocks: the ranges its
// cross-server event tables match servers in ("1893-1956|1957-2020"). Opens on our own block.

const day = (iso) => (iso ? String(iso).slice(0, 10) : "");
const ageDays = (iso) => (iso ? Math.floor((Date.now() - new Date(iso)) / 864e5) : null);
export const LEVEL = { full: ["full", "var(--accent)"], basic: ["basic", "#e3b341"], off: ["registry", "var(--muted, #8b97a6)"] };
export const Level = ({ v }) => <span className="small mono" style={{ color: LEVEL[v || "off"]?.[1] }}>{LEVEL[v || "off"]?.[0]}</span>;
// Season tag in the war table: "10008,S1" -> "S1".
const seasonOf = (tag) => (tag || "").split(",")[1] || "";
const WAR_KIND = { 4: "server duel", 8: "4 v 4 war" };

/** The group a view names: a war group (servers that fight in 4 v 4 wars together) or a 64-server event block. */
export function groupOf(view, d) {
  if (view.startsWith("war:")) return (d.warGroups || []).find((g) => g.id === view.slice(4)) && { kind: "war", ...(d.warGroups || []).find((g) => g.id === view.slice(4)) };
  if (view.startsWith("event:")) return d.blocks.find((b) => b.id === view.slice(6)) && { kind: "event", ...d.blocks.find((b) => b.id === view.slice(6)) };
  const b = d.blocks.find((x) => x.id === view);   // old links: #/x/servers?group=1957-2020
  return b ? { kind: "event", ...b } : null;
}

/** Which servers a picked view covers. */
export function inView(view, s, d) {
  if (view === "all") return true;
  if (view === "tracked") return s.level === "basic" || s.level === "full";
  if (view === "shown") return s.visible;
  const g = groupOf(view, d);
  if (!g) return true;
  return g.kind === "war" ? g.servers.includes(s.id) : s.id >= g.lo && s.id <= g.hi;
}

/** The default view: our war group (the 32 that transfer and run seasons together). */
export const defaultView = (d) => (d?.current ? `war:${d.current}` : "shown");

export function ViewPicker({ d, view, setView }) {
  const blocks = [...(d?.blocks || [])].sort((a, b) => a.lo - b.lo);
  const wars = [...(d?.warGroups || [])].filter((g) => g.size >= 8).sort((a, b) => a.lo - b.lo);
  return (
    <select value={view} onChange={(e) => setView(e.target.value)} title="Which servers">
      {d?.current && <option value={`war:${d.current}`}>our war group ({d.current})</option>}
      {d?.currentBlock && <option value={`event:${d.currentBlock}`}>our event group ({d.currentBlock})</option>}
      <option value="shown">shown servers</option>
      <option value="tracked">tracked (basic + full)</option>
      <option value="all">every server</option>
      <optgroup label="war groups - fight in 4 v 4 wars together">
        {wars.map((g) => <option key={g.id} value={`war:${g.id}`}>{g.id} ({g.size} servers{g.contiguous ? "" : ", not a clean range"})</option>)}
      </optgroup>
      <optgroup label="event groups - matched in cross-server events">
        {blocks.map((b) => <option key={b.id} value={`event:${b.id}`}>{b.id} ({b.size} servers{b.seasons.length ? `, S${b.seasons.join("/S")}` : ""})</option>)}
      </optgroup>
    </select>
  );
}

function Wars({ lo, hi, highlight }) {
  const w = useJson(`/api/x/servers/wars?lo=${lo}&hi=${hi}`);
  if (!w) return <p className="muted small">Loading wars…</p>;
  return (
    <>
      <div className="card">
        <h2>Server wars ({w.wars.length})</h2>
        {w.wars.length === 0 ? <p className="muted small">None in the game tables for these servers.</p> : (
          <table className="small">
            <thead><tr><th>from</th><th>to</th><th>season</th><th>kind</th><th>sides</th></tr></thead>
            <tbody>
              {w.wars.slice(0, 120).map((r) => (
                <tr key={r.id}>
                  <td className="mono">{day(r.open)}</td><td className="mono muted">{day(r.end)}</td>
                  <td>{seasonOf(r.tag)}</td><td className="muted">{WAR_KIND[r.type] || `type ${r.type}`}</td>
                  <td>{r.sides.map((side, i) => (
                    <span key={i}>{i > 0 && <b className="muted"> vs </b>}
                      {side.map((s, j) => <span key={s}>{j > 0 && ", "}<a href={`#/x/server/${s}`} style={highlight(s) ? { fontWeight: 600 } : { opacity: 0.6 }}>{s}</a></span>)}
                    </span>
                  ))}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        <p className="small muted">From the game&apos;s own war table (lw_zone_war). Bold = in this view.</p>
      </div>
      <div className="card">
        <h2>Cross-server event groups ({w.groups.length})</h2>
        <table className="small">
          <thead><tr><th>from</th><th>to</th><th>season</th><th>groups (| separates the sides matched together)</th></tr></thead>
          <tbody>
            {w.groups.slice(0, 60).map((g) => (
              <tr key={g.id}><td className="mono">{day(g.open?.replaceAll("/", "-"))}</td><td className="mono muted">{day(g.close?.replaceAll("/", "-"))}</td>
                <td>{g.season ? `S${g.season}` : "off-season"}</td><td className="mono">{g.groups}</td></tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

/** Season groups: servers on the same season config share a season map. Read by servers.season (a guest asks the
 * game to start a character on each server; a server in a season refuses with the config it is on). */
function SeasonGroups({ lo, hi, highlight }) {
  const g = useJson(`/api/x/season-groups?lo=${lo}&hi=${hi}`);
  if (!g || g.error || g.length === 0) return null;
  const by = new Map();
  g.forEach((r) => { if (!by.has(r.cfg)) by.set(r.cfg, []); by.get(r.cfg).push(r); });
  const seen = g.filter((r) => r.by !== "hand").reduce((m, r) => (r.seen > m ? r.seen : m), "");
  const anyHand = g.some((r) => r.by === "hand");
  const date = (s) => (s ? String(s).slice(0, 10).replaceAll("/", "-") : "");
  return (
    <div className="card">
      <h2>Season groups ({by.size})</h2>
      <table className="small">
        <thead><tr><th>season config</th><th>season</th><th>pre-season</th><th>starts</th><th>ends</th><th>servers</th></tr></thead>
        <tbody>
          {[...by.entries()].sort((a, b) => a[0] - b[0]).map(([cfg, rs]) => (
            <tr key={cfg}>
              <td className="mono">{cfg}</td><td>{rs[0].season ? `S${rs[0].season}` : ""}</td>
              <td className="mono muted">{date(rs[0].preStart)}</td><td className="mono">{date(rs[0].start)}</td><td className="mono muted">{date(rs[0].end)}</td>
              <td>{rs.map((r, j) => <span key={r.server}>{j > 0 && ", "}<a href={`#/x/server/${r.server}`} title={r.by === "hand" ? `entered by hand ${date(r.since)} - not yet reported by the game` : undefined} style={{ ...(highlight(r.server) ? { fontWeight: 600 } : { opacity: 0.6 }), ...(r.by === "hand" ? { fontStyle: "italic" } : {}) }}>{r.server}{r.by === "hand" && "*"}</a></span>)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="small muted">Read {when(seen)}. A guest asks the game to start a character on each server; a server in a season refuses and names the season config it is on. Servers on one config share a season map. The game switches a server to its next season&apos;s config when that season STARTS, not at pre-season.</p>
      {anyHand && <p className="small muted"><i>1234*</i> = entered by hand from the in-game display, until the game reports that server on that config. Such a server also still shows on its current config.</p>}
    </div>
  );
}
/** Explore -> Servers: every server, a group at a time. */
export function ServersPage() {
  const d = useJson("/api/x/servers");
  const [view, setView] = useHashParam("group", "");
  const [q, setQ] = useState("");
  const v = view || defaultView(d);
  const rows = useMemo(() => (d?.servers || []).filter((s) => s.type === 0 && inView(v, s, d) && (!q || String(s.id).includes(q) || (s.kingName || "").toLowerCase().includes(q.toLowerCase()) || (s.kingTag || "").toLowerCase().includes(q.toLowerCase()))), [d, v, q]);
  if (!d) return <p className="muted">Loading every server…</p>;
  if (d.error) return <p className="err">Could not load ({d.error}).</p>;
  const block = groupOf(v, d);
  const lo = block ? block.lo : Math.min(...rows.map((r) => r.id));
  const hi = block ? block.hi : Math.max(...rows.map((r) => r.id));
  const inRows = new Set(rows.map((r) => r.id));
  const known = d.servers.filter((s) => s.type === 0).length;
  return (
    <div className="grid">
      <div className="row small" style={{ gap: 8, flexWrap: "wrap" }}>
        <ViewPicker d={d} view={v} setView={setView} />
        <input placeholder="server, president or tag" value={q} onChange={(e) => setQ(e.target.value)} style={{ width: 180 }} />
        <span className="muted">{int(rows.length)} of {int(known)} servers the game lists</span>
      </div>
      {block?.kind === "war" && (
        <div className="card">
          <h2>War group {block.id}</h2>
          <p className="small">
            {block.size} servers that fight each other in the 4 v 4 wars, {block.firstWar} to {block.lastWar}{block.contiguous ? "" : " (not one clean range)"}.
            {(() => { const e = d.blocks.find((b) => block.lo >= b.lo && block.hi <= b.hi && b.hi - b.lo < 200); return e ? <> Part of event group <a href={`#/x/servers?group=event:${e.id}`}>{e.id}</a>.</> : null; })()}
          </p>
          <p className="small muted">Read off the game&apos;s war table: servers in any war together, and whoever they fought, form one group. It grows by itself when the game starts matching across groups.</p>
        </div>
      )}
      {block?.kind === "event" && (
        <div className="card">
          <h2>Event group {block.id}</h2>
          <p className="small">
            {block.size} servers the game matched together in cross-server events{block.seasons.length ? ` in season ${block.seasons.join(", ")}` : ""}, last in {block.lastSeen}.
            {block.pairedWith.length > 0 && <> Matched against {block.pairedWith.map((p, i) => <span key={p}>{i > 0 && ", "}<a href={`#/x/servers?group=event:${p}`}>{p}</a></span>)}.</>}
          </p>
          <p className="small muted">War groups inside it: {(d.warGroups || []).filter((g) => g.lo >= block.lo && g.hi <= block.hi).map((g, i) => <span key={g.id}>{i > 0 && ", "}<a href={`#/x/servers?group=war:${g.id}`}>{g.id}</a></span>)}.</p>
        </div>
      )}
      <div className="card">
        <table className="small">
          <thead><tr><th>server</th><th>opened</th><th>age</th><th>tracked</th><th>president</th><th>president since</th><th>badge</th><th>last mapped</th><th>season</th></tr></thead>
          <tbody>
            {rows.slice(0, 600).map((s) => (
              <tr key={s.id} style={s.visible ? undefined : { opacity: 0.75 }}>
                <td><ServerLink id={s.id} /></td>
                <td className="mono">{day(s.open)}</td>
                <td className="mono muted">{s.open ? `${ageDays(s.open)} d` : ""}</td>
                <td><Level v={s.level} /></td>
                <td>{s.kingName ? <><span className="muted">{s.kingTag ? `[${s.kingTag}] ` : ""}</span>{s.kingName}{s.kingPower ? <span className="muted small"> {big(s.kingPower)}</span> : null}</> : ""}</td>
                <td className="mono muted">{day(s.kingSince)}</td>
                <td className="mono small" title={s.badgeAt ? `badge ${s.badge}, given ${when(s.badgeAt)}` : ""}>{s.badge ? `${s.badge} · ${day(s.badgeAt)}` : ""}</td>
                <td className="muted">{s.mappedAt ? `${day(s.mappedAt)} (${int(s.mapPlayers)})` : ""}</td>
                <td title={s.season ? `S${s.season.season} ${day(s.season.start)} → ${day(s.season.end)}${s.season.from ? ` (read on #${s.season.from}, same group)` : ""}` : ""}>{s.season ? `S${s.season.season}${s.season.from ? "*" : ""}` : ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows.length > 600 && <p className="small muted">First 600 shown - pick a group or search.</p>}
        <p className="small muted">
          Badge = the war-zone badge its president holds; servers badged at the same moment were in the same season or war (e.g. every server given 511020 on the same day).
          The game&apos;s own API calls the president &quot;king&quot;.
          Season is read by an account on the server; * = taken from another server in the same group.
        </p>
      </div>
      {rows.length > 0 && rows.length <= 400 && <SeasonGroups lo={lo} hi={hi} highlight={(s) => inRows.has(s)} />}
      {rows.length > 0 && rows.length <= 400 && <Wars lo={lo} hi={hi} highlight={(s) => inRows.has(s)} />}
    </div>
  );
}

const EVENT = { king: "new president", badge: "badge", camp: "war side", season_group: "season group", warzone: "war zone", transfer_group: "transfer group" };

/** Server page card: group, president history, badges, season zone, seasons and wars. */
export function ServerSeason({ server }) {
  const d = useJson(`/api/x/server/${server}/story`);
  if (!d || d.error || !d.info) return null;
  const z = d.zones?.[0];
  let top = [];
  try { top = z?.top ? JSON.parse(z.top) : []; } catch { /* old row */ }
  return (
    <div className="card" style={{ gridColumn: "1 / -1" }}>
      <h2>Season &amp; group</h2>
      <p className="small">
        Opened {day(d.info.open_time) || "?"}{d.info.open_time ? ` (${ageDays(d.info.open_time)} days ago)` : ""}.
        {d.warGroup && <> War group <a href={`#/x/servers?group=war:${d.warGroup.id}`}>{d.warGroup.id}</a> ({d.warGroup.size} servers, last war {d.warGroup.lastWar}).</>}
        {d.block ? <> Event group <a href={`#/x/servers?group=event:${d.block.id}`}>{d.block.id}</a>{d.block.pairedWith.length ? <>, matched against {d.block.pairedWith.join(", ")}</> : null}.</> : " Not in any event group we know of."}
        {d.track && <> Tracked: <Level v={d.track.level} />{d.track.visible ? "" : " (hidden)"}.</>}
        {d.info.king_name && <> President {d.info.king_tag ? `[${d.info.king_tag}] ` : ""}{d.info.king_name}{d.info.king_since ? ` since ${day(d.info.king_since)}` : ""}.</>}
      </p>
      {z && (
        <p className="small">
          Season zone ({when(z.at)}): force rank {z.forceRank ?? "?"} of {z.zones ?? "?"}{z.camp ? `, war side ${z.camp}` : ""}{z.cities ? `, ${z.cities} cities (${z.destroyed || 0} destroyed)` : ""}.
          {top.length > 0 && <> Top: {top.map((a) => `${a.tag} (#${a.rank})`).join(", ")}.</>}
        </p>
      )}
      <div className="two">
        <div>
          <h2 style={{ marginTop: 8 }}>What changed</h2>
          {d.events.length === 0 ? <p className="muted small">Nothing logged yet.</p> : (
            <table className="small"><tbody>
              {d.events.slice(0, 20).map((e) => <tr key={e.kind + e.at}><td className="mono">{day(e.at)}</td><td>{EVENT[e.kind] || e.kind}</td><td>{e.detail || e.value}</td></tr>)}
            </tbody></table>
          )}
          {d.seasons.length > 0 && (
            <>
              <h2 style={{ marginTop: 10 }}>Seasons</h2>
              <table className="small"><tbody>
                {d.seasons.map((s) => <tr key={s.season}><td>S{s.season}</td><td className="mono">{day(s.start)} → {day(s.end)}</td><td className="muted">config {s.cfg}{s.group ? `, group ${s.group}` : ""}</td></tr>)}
              </tbody></table>
            </>
          )}
        </div>
        <div>
          <h2 style={{ marginTop: 8 }}>Server wars</h2>
          {d.wars.length === 0 ? <p className="muted small">None in the game tables.</p> : (
            <table className="small"><tbody>
              {d.wars.slice(0, 12).map((w) => (
                <tr key={w.id}><td className="mono">{day(w.open)}</td><td>{seasonOf(w.tag)}</td>
                  <td>{w.sides.map((side, i) => <span key={i}>{i > 0 && <b className="muted"> vs </b>}{side.map((s) => (s === server ? <b key={s}> {s}</b> : <a key={s} href={`#/x/server/${s}`}> {s}</a>))}</span>)}</td></tr>
              ))}
            </tbody></table>
          )}
        </div>
      </div>
    </div>
  );
}
