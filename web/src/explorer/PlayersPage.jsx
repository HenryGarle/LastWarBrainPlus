import { useEffect, useState } from "react";
import { useHashParam } from "../nav.js";
import { big, int } from "./format.js";
import { Tag, ServerLink, PlayerLink } from "./bits.jsx";
import { Seen } from "./Intel.jsx";
import { Flag, countryName, CountryOptions } from "./Online.jsx";
import { useJson } from "./PlayerTabs.jsx";

// Explore > Players: everyone on every server (Insights.PlayerDirectory). Filtering, sorting and paging all happen on the
// server - there are hundreds of thousands of players - so only one page is ever loaded. Every filter is in the URL.
const SEEN = [["", "any time"], ["1d", "in the last 24 h"], ["3d", "in the last 3 days"], ["7d", "in the last 7 days"], ["30d", "in the last 30 days"],
  ["not7d", "not in 7 days"], ["not30d", "not in 30 days"], ["never", "never seen"]];
const MOVED = [["", "any"], ["transferred", "transferred in (created elsewhere)"], ["native", "created on their server"], ["visiting", "base on another server now"]];
const HERO = [["", "any"], ["25000000", "25M+"], ["50000000", "50M+"], ["100000000", "100M+"], ["150000000", "150M+"], ["200000000", "200M+"]];
const POWER = [["", "any"], ["50000000", "50M+"], ["100000000", "100M+"], ["200000000", "200M+"], ["300000000", "300M+"]];
const COLS = [
  ["name", "player"], ["tag", "alliance"], ["server", "server"], ["country", "created in"], ["flag", "displays as"], ["hq", "HQ"],
  ["hero", "hero power"], ["power", "total power"], ["kills", "kills"], ["vip", "VIP"], ["seen", "last seen"],
];

// A text box that only updates its filter when typing stops, so each keystroke is not a query.
function Typed({ value, set, ...rest }) {
  const [v, setV] = useState(value);
  useEffect(() => setV(value), [value]);
  useEffect(() => { if (v === value) return undefined; const t = setTimeout(() => set(v), 450); return () => clearTimeout(t); }, [v]);   // eslint-disable-line react-hooks/exhaustive-deps
  return <input value={v} onChange={(e) => setV(e.target.value)} {...rest} />;
}

export function PlayersPage() {
  const [q, setQ] = useHashParam("q", "");
  const [servers, setServers] = useHashParam("servers", "");
  const [alliance, setAlliance] = useHashParam("alliance", "");
  const [country, setCountry] = useHashParam("country", "");
  const [flag, setFlag] = useHashParam("flag", "");
  const [hqMin, setHqMin] = useHashParam("hqMin", "");
  const [hqMax, setHqMax] = useHashParam("hqMax", "");
  const [heroMin, setHeroMin] = useHashParam("heroMin", "");
  const [powerMin, setPowerMin] = useHashParam("powerMin", "");
  const [seen, setSeen] = useHashParam("seen", "");
  const [moved, setMoved] = useHashParam("moved", "");
  const [sort, setSort] = useHashParam("sort", "hero");
  const [dir, setDir] = useHashParam("dir", "desc");
  const [page, setPage] = useHashParam("page", "0");
  const [size, setSize] = useHashParam("size", "100");

  const filters = { q, servers, alliance, country, flag, hqMin, hqMax, heroMin, powerMin, seen, moved };
  const params = new URLSearchParams(Object.entries({ ...filters, sort, dir, page, size }).filter(([, v]) => v !== ""));
  const d = useJson(`/api/x/players?${params}`);
  // Any filter change starts again at the first page.
  const key = JSON.stringify(filters) + sort + dir + size;
  const [lastKey, setLastKey] = useState(key);
  useEffect(() => { if (key !== lastKey) { setLastKey(key); if (page !== "0") setPage("0"); } }, [key]);   // eslint-disable-line react-hooks/exhaustive-deps

  const facets = d?.facets;
  const pages = d ? Math.max(1, Math.ceil(d.total / d.size)) : 1;
  const p = Number(page) || 0;
  const by = (k) => { if (sort === k) setDir(dir === "desc" ? "asc" : "desc"); else { setSort(k); setDir(k === "name" || k === "tag" || k === "country" || k === "flag" ? "asc" : "desc"); } };
  const reset = () => { [setQ, setServers, setAlliance, setCountry, setFlag, setHqMin, setHqMax, setHeroMin, setPowerMin, setSeen, setMoved].forEach((s) => s("")); };
  const any = Object.values(filters).some((v) => v !== "");

  return (
    <div className="grid">
      <div className="card">
        <div className="row" style={{ justifyContent: "space-between", gap: 8, flexWrap: "wrap" }}>
          <h2 style={{ margin: 0 }}>Players on every server {d && <span className="muted">({int(d.total)}{any ? " match" : ""})</span>}</h2>
          {any && <button className="small" onClick={reset}>Clear filters</button>}
        </div>
        <div className="filters" style={{ marginTop: 8 }}>
          <Typed value={q} set={setQ} placeholder="name or uid" style={{ width: 150 }} />
          <Typed value={servers} set={setServers} placeholder="servers: 1967 or 1957-1988" style={{ width: 190 }} title="One server, a range or a list - their home server. Empty = the tracked servers only (players glimpsed from far-off servers are left out unless you name their server)." />
          <Typed value={alliance} set={setAlliance} placeholder="alliance tag (or none)" style={{ width: 150 }} />
          <select className="flag-select" value={country} onChange={(e) => setCountry(e.target.value)} title="The profile country - where the account is from">
            <option value="">Created in: any</option>
            <CountryOptions list={facets?.countries} label="Created in" />
            <option value="unknown">Created in: unknown</option>
          </select>
          <select className="flag-select" value={flag} onChange={(e) => setFlag(e.target.value)} title="The flag the player chooses to show">
            <option value="">Displays as: any</option>
            <CountryOptions list={facets?.flags} label="Displays" />
            <option value="unknown">Displays: no flag</option>
          </select>
          <label className="small">HQ <input type="number" min="1" max="40" value={hqMin} onChange={(e) => setHqMin(e.target.value)} placeholder="min" style={{ width: 60 }} />
            – <input type="number" min="1" max="40" value={hqMax} onChange={(e) => setHqMax(e.target.value)} placeholder="max" style={{ width: 60 }} /></label>
          <label className="small">hero power <select value={heroMin} onChange={(e) => setHeroMin(e.target.value)}>{HERO.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></label>
          <label className="small">total power <select value={powerMin} onChange={(e) => setPowerMin(e.target.value)}>{POWER.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></label>
          <label className="small" title="Login, alliance donation or chat">last seen <select value={seen} onChange={(e) => setSeen(e.target.value)}>{SEEN.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></label>
          <label className="small">moved <select value={moved} onChange={(e) => setMoved(e.target.value)}>{MOVED.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></label>
        </div>

        {!d ? <p className="muted small">Loading…</p> : d.error ? <p className="err small">{d.error}</p> : (
          <>
            <div className="scroll">
              <table className="small">
                <thead><tr>{COLS.map(([k, l]) => (
                  <th key={k} className={`sortable ${["hq", "hero", "power", "kills", "vip"].includes(k) ? "num" : ""}`} onClick={() => by(k)}>
                    {l}{sort === k ? (dir === "desc" ? " ↓" : " ↑") : ""}
                  </th>
                ))}</tr></thead>
                <tbody>
                  {d.rows.map((r) => (
                    <tr key={r.uid}>
                      <td><PlayerLink uid={r.uid} name={r.name} /></td>
                      <td>{r.tag ? <Tag id={r.allianceId} tag={r.tag} /> : <span className="muted">–</span>}</td>
                      <td>
                        <ServerLink id={r.server} />
                        {r.origin && r.origin !== r.server && <span className="muted" title="created on another server"> · from <ServerLink id={r.origin} /></span>}
                        {r.currentServer && r.currentServer !== r.server && <span title="their base is on another server right now"> · on <ServerLink id={r.currentServer} /></span>}
                      </td>
                      <td>{r.country ? <><Flag code={r.country} /> {countryName(r.country)}</> : <span className="muted">–</span>}</td>
                      <td>{!r.flag ? <span className="muted">–</span> : r.flag === r.country ? <span className="muted"><Flag code={r.flag} /> same</span> : <><Flag code={r.flag} /> {countryName(r.flag)}</>}</td>
                      <td className="num">{r.hq ?? "–"}</td>
                      <td className="num">{r.hero ? big(r.hero) : "–"}</td>
                      <td className="num">{r.power ? big(r.power) : "–"}</td>
                      <td className="num">{r.kills ? big(r.kills) : "–"}</td>
                      <td className="num">{r.vip || "–"}</td>
                      <td><Seen iso={r.lastSeen} /></td>
                    </tr>
                  ))}
                  {d.rows.length === 0 && <tr><td colSpan={COLS.length} className="muted">No players match those filters.</td></tr>}
                </tbody>
              </table>
            </div>
            <div className="row small" style={{ gap: 8, alignItems: "center", flexWrap: "wrap", marginTop: 10 }}>
              <button disabled={p <= 0} onClick={() => setPage("0")}>« first</button>
              <button disabled={p <= 0} onClick={() => setPage(String(p - 1))}>‹ previous</button>
              <span>page {int(p + 1)} of {int(pages)} · players {int(p * d.size + 1)}–{int(Math.min(d.total, (p + 1) * d.size))} of {int(d.total)}</span>
              <button disabled={p >= pages - 1} onClick={() => setPage(String(p + 1))}>next ›</button>
              <button disabled={p >= pages - 1} onClick={() => setPage(String(pages - 1))}>last »</button>
              <label>per page <select value={size} onChange={(e) => setSize(e.target.value)}>{["100", "250", "500"].map((s) => <option key={s} value={s}>{s}</option>)}</select></label>
            </div>
            <p className="small muted" style={{ marginBottom: 0 }}>
              Server = the home server (from their profile). Created in = the profile country; displays as = the flag they choose. Last seen = newest login,
              alliance donation or chat. Numbers are from each player&apos;s newest profile.
            </p>
          </>
        )}
      </div>
    </div>
  );
}
