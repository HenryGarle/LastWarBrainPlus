import { useEffect, useMemo, useState } from "react";
import { useHashParam } from "../nav.js";
import { SortTable } from "./charts.jsx";
import { big, href, int, when } from "./format.js";
import { Tag, PlayerLink, ServerLink } from "./bits.jsx";

// New accounts: bases a map scan saw for the first time AFTER our first scan of the server, made
// there (uid ends in the server number) and small when first seen. See Insights.NewAccounts.
const PERIODS = [[1, "last day"], [7, "last 7 days"], [30, "last 30 days"], [3650, "all time"]];
const DEFAULT_NAME = /^\S+ ?[0-9a-f]{5}\d{3,4}$/u;   // "Commandant 196d21967", "Kommandant24bfd1967": never renamed
const useJson = (url) => {
  const [d, setD] = useState(null);
  const [err, setErr] = useState(null);
  useEffect(() => {
    let live = true; setD(null); setErr(null);
    fetch(url).then((r) => (r.ok ? r.json() : Promise.reject(new Error(r.statusText)))).then((x) => live && setD(x)).catch((e) => live && setErr(e.message));
    return () => { live = false; };
  }, [url]);
  return [d, err];
};
const Period = ({ days, setDays }) => (
  <select value={days} onChange={(e) => setDays(e.target.value)} style={{ width: "auto" }}>
    {PERIODS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
  </select>
);
const Explain = ({ fresh, since }) => (
  <p className="small muted" style={{ margin: "4px 0 8px" }}>
    Bases first seen by a map scan after we started watching{since ? ` (${when(since)})` : ""}, created on this server and HQ {fresh} or lower when first seen.
    A small account that had been gone from the map for a long time looks the same; a default name (never changed) is the tell of a brand-new one.
    Found as often as the map is refreshed.
  </p>
);

/** Server page card: that server's new accounts in the chosen period. */
export function ServerNewAccounts({ server }) {
  const [days, setDays] = useHashParam("newdays", "7");
  const [d, err] = useJson(`/api/x/server/${server}/new?days=${days}`);
  const rows = d?.accounts || [];
  const fresh = rows.filter((r) => DEFAULT_NAME.test(r.name)).length;
  return (
    <div className="card">
      <div className="row" style={{ justifyContent: "space-between", gap: 8, flexWrap: "wrap" }}>
        <h2 style={{ margin: 0 }}>New accounts {d && `(${rows.length})`}</h2>
        <span className="row small" style={{ gap: 8 }}>
          <Period days={days} setDays={setDays} />
          <a href={`#/x/newaccounts?days=${days}`}>every server</a>
        </span>
      </div>
      {d && <Explain fresh={d.freshHq} since={d.watchedSince} />}
      {err && <p className="err small">{err}</p>}
      {d && rows.length > 0 && <p className="small">{fresh} still have the default name · {rows.filter((r) => (r.hq || 0) > r.firstHq).length} have levelled their HQ since · {rows.filter((r) => r.server && r.server !== server).length} have moved on</p>}
      {d && (
        <SortTable
          rows={rows}
          empty="No new accounts seen in this period."
          initial={{ key: "firstAt", dir: -1 }}
          columns={[
            { key: "name", label: "player", render: (p) => <><PlayerLink uid={p.uid} name={p.name} />{DEFAULT_NAME.test(p.name) && <span className="muted small"> · default name</span>}</> },
            { key: "tag", label: "alliance", render: (p) => <Tag id={p.allianceId} tag={p.tag} /> },
            { key: "firstAt", label: "first seen", render: (p) => <span className="mono small">{when(p.firstAt)}</span> },
            { key: "hq", label: "HQ", sort: (p) => p.hq ?? p.firstHq, render: (p) => (p.hq && p.hq !== p.firstHq ? <>{p.firstHq} → <strong>{p.hq}</strong></> : p.hq ?? p.firstHq) },
            { key: "power", label: "power", render: (p) => (p.power ? big(p.power) : <span className="muted">–</span>) },
            { key: "server", label: "now on", render: (p) => (p.server && p.server !== server ? <ServerLink id={p.server} /> : <span className="muted">here</span>) },
          ]}
        />
      )}
    </div>
  );
}

/** Explore page: new accounts per server per day. A day with no map scan is blank, not 0. */
export function NewAccountsPage() {
  const [days, setDays] = useHashParam("days", "30");
  const [d, err] = useJson(`/api/x/newaccounts?days=${days}`);
  const table = useMemo(() => {
    if (!d) return null;
    const scanned = new Map(), firsts = new Set();
    for (const s of d.scans) { scanned.set(`${s.server}|${s.day}`, s.n); if (s.first) firsts.add(`${s.server}|${s.day}`); }
    const count = new Map(d.counts.map((c) => [`${c.server}|${c.day}`, c.n]));
    const dayList = [...new Set([...d.scans.map((s) => s.day), ...d.counts.map((c) => c.day)])].sort();
    const servers = [...new Set([...d.scans.map((s) => s.server), ...d.counts.map((c) => c.server)])].sort((a, b) => a - b);
    const rows = servers.map((sv) => ({ server: sv, total: dayList.reduce((t, dd) => t + (count.get(`${sv}|${dd}`) || 0), 0) }));
    const max = Math.max(1, ...count.values());
    return { dayList, rows, count, scanned, firsts, max, total: rows.reduce((t, r) => t + r.total, 0) };
  }, [d]);
  return (
    <div className="grid">
      <div className="card">
        <div className="row" style={{ justifyContent: "space-between", gap: 8, flexWrap: "wrap" }}>
          <h2 style={{ margin: 0 }}>New accounts by server {table && <span className="muted">({int(table.total)})</span>}</h2>
          <Period days={days} setDays={setDays} />
        </div>
        {d && <Explain fresh={d.freshHq} />}
        {err && <p className="err small">{err}</p>}
        {table && (table.dayList.length === 0 ? <p className="muted small">No map scans in this period.</p> : (
          <div className="scroll">
            <table className="small newacc">
              <thead><tr><th>server</th><th>total</th>{table.dayList.map((dd) => <th key={dd} className="mono">{dd.slice(5)}</th>)}</tr></thead>
              <tbody>
                {table.rows.map((r) => (
                  <tr key={r.server}>
                    <td><a href={`${href.server(r.server)}?newdays=${days}`}>#{r.server}</a></td>
                    <td><strong>{int(r.total)}</strong></td>
                    {table.dayList.map((dd) => {
                      const k = `${r.server}|${dd}`, n = table.count.get(k) || 0, seen = table.scanned.has(k);
                      return (
                        <td key={dd} title={seen ? `${n} new · ${table.scanned.get(k)} map scan(s)${table.firsts.has(k) ? " · first scan: baseline, nothing counted" : ""}` : "no map scan that day"}
                          style={{ background: n ? `rgba(240,180,41,${0.12 + 0.6 * Math.sqrt(n / table.max)})` : undefined, textAlign: "center" }}
                          className={seen ? "" : "muted"}>
                          {seen ? (table.firsts.has(k) && !n ? "base" : n || "0") : ""}
                        </td>
                      );
                    })}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ))}
        <p className="small muted" style={{ marginBottom: 0 }}>Blank = no map scan that day (unknown, not zero). &ldquo;base&rdquo; = the first scan of that server, which is the baseline. Click a server for its list.</p>
      </div>
    </div>
  );
}