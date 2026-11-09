import { MultiLineChart, BarList, splitParts } from "./charts.jsx";
import { allianceColour, big, int, when, href } from "./format.js";
import { Delta, Tag, ServerLink, PlayerLink } from "./bits.jsx";

// #/x/rankings/<entity>/<metric>?at=&vs=&server=&limit=&chart=a,b
// Every alliance, player or server across the range on one measure; rank movement against an
// earlier snapshot; up to four of them charted over every snapshot.
const ENTITIES = [["alliances", "Alliances"], ["players", "Players"], ["servers", "Servers"]];
// Chart lines: the four validated slots first, then six more for when more rows are ticked.
const SLOTS = ["var(--series-1)", "var(--series-2)", "var(--series-3)", "var(--series-4)", "var(--series-5)",
  "var(--series-6)", "var(--series-7)", "var(--series-8)", "var(--series-9)", "var(--series-10)"];
const MAX_CHART = 12;
const PERIODS = ["1d", "7d", "30d", "none"];
const FMT = { big, int };
const TOPS = ["0", "3", "5", "10", "15"];

export const rankingsHref = (entity = "alliances", metric = "heroest", q = {}) => {
  const qs = Object.entries(q).filter(([, v]) => v !== undefined && v !== null && v !== "")
    .map(([k, v]) => `${k}=${encodeURIComponent(v)}`).join("&");
  return `#/x/rankings/${entity}/${metric}${qs ? `?${qs}` : ""}`;
};

export const parseRankingsRoute = (route) => {
  const [metric, qs] = (route.extra || "").split("?");
  const q = Object.fromEntries(new URLSearchParams(qs || ""));
  return { entity: route.id || "alliances", metric: metric || "heroest", q };
};

function Move({ rank, prev }) {
  if (prev === undefined || prev === null) return <span className="muted small">new</span>;
  const d = Number(prev) - Number(rank);
  if (d === 0) return <span className="muted">–</span>;
  return <span className={d > 0 ? "up" : "down"}>{d > 0 ? `▲ ${d}` : `▼ ${-d}`}</span>;
}

export default function Rankings({ d, route }) {
  const { entity, metric, q } = parseRankingsRoute(route);
  const go = (patch) => {
    const next = { ...q, ...patch };
    const e = patch.entity ?? entity;
    const m = patch.metric ?? (patch.entity ? (e === "servers" ? "activehero7" : "heroest") : metric);
    delete next.entity; delete next.metric;
    // A new measure or entity has its own snapshots and its own top entities (a period still applies).
    if (patch.entity || patch.metric) { delete next.at; if (!PERIODS.includes(next.vs)) delete next.vs; delete next.chart; }
    window.location.hash = rankingsHref(e, d.metrics.some((x) => x.entity === e && x.key === m) ? m : "heroest", next);
  };

  const metrics = d.metrics.filter((m) => m.entity === d.entity);
  const fmt = FMT[d.format] || big;
  const charted = d.series.map((s) => s.key);
  // Charts use the four validated series slots, not alliance colours: hashed hues can land close
  // together (ZRL, PEC and NDEV all came out green), and four lines must be told apart at a glance.
  const colour = (k, i) => SLOTS[i % SLOTS.length];
  const toggle = (k) => {
    const on = charted.includes(k) ? charted.filter((x) => x !== k) : [...charted, k].slice(-MAX_CHART);
    go({ chart: on.join(",") || undefined });
  };
  const servers = Array.from({ length: d.hi - d.lo + 1 }, (_, i) => d.lo + i);
  const advanced = q.adv === "1";

  return (
    <div className="grid">
      <div className="card rank-controls">
        <div className="cmp-tabs">
          {ENTITIES.map(([k, l]) => (
            <a key={k} href="#" className={k === d.entity ? "on" : ""} onClick={(e) => { e.preventDefault(); go({ entity: k }); }}>{l}</a>
          ))}
        </div>
        <div className="row small" style={{ gap: 10, flexWrap: "wrap", marginTop: 10 }}>
          <label>ranked by{" "}
            <select value={d.metric} onChange={(e) => go({ metric: e.target.value })}>
              {metrics.map((m) => <option key={m.key} value={m.key}>{m.label}</option>)}
            </select>
          </label>
          <label>change since{" "}
            <select value={PERIODS.includes(q.vs) ? q.vs : q.vs ? "exact" : ""} onChange={(e) => go({ vs: e.target.value || undefined })}>
              <option value="">the first scan</option>
              <option value="1d">a day ago</option>
              <option value="7d">7 days ago</option>
              <option value="30d">30 days ago</option>
              <option value="none">don't compare</option>
              {q.vs && !PERIODS.includes(q.vs) && <option value="exact" disabled>{when(q.vs)}</option>}
            </select>
          </label>
          {advanced && (
            <>
              <label>as of{" "}
                <select value={d.at || ""} onChange={(e) => go({ at: e.target.value })}>
                  {[...d.snapshots].reverse().map((s) => <option key={s} value={s}>{when(s)}</option>)}
                </select>
              </label>
              <label>compared with exactly{" "}
                <select value={d.vs || ""} onChange={(e) => go({ vs: e.target.value || "none" })}>
                  <option value="">none</option>
                  {[...d.snapshots].reverse().filter((s) => s !== d.at).map((s) => <option key={s} value={s}>{when(s)}</option>)}
                </select>
              </label>
            </>
          )}
          {d.entity !== "servers" && (
            <label>server{" "}
              <select value={d.server || ""} onChange={(e) => go({ server: e.target.value || undefined })}>
                <option value="">all {d.lo}-{d.hi}</option>
                {servers.map((s) => <option key={s} value={s}>#{s}</option>)}
              </select>
            </label>
          )}
          {d.splittable && (
            <>
              <label>split out{" "}
                <select value={String(d.top || 0)} onChange={(e) => go({ top: e.target.value === "0" ? undefined : e.target.value })}>
                  {TOPS.map((v) => <option key={v} value={v}>{v === "0" ? "no alliances" : `top ${v} alliances`}</option>)}
                </select>
              </label>
              <label>everyone else{" "}
                <select value={d.others ? "show" : "hide"} disabled={!d.top} onChange={(e) => go({ others: e.target.value === "hide" ? "hide" : undefined })}>
                  <option value="show">show</option>
                  <option value="hide">hide - rank on the top alliances only</option>
                </select>
              </label>
            </>
          )}
          <button className="linkish" onClick={() => go({ adv: advanced ? undefined : "1" })}>{advanced ? "Simple" : "Advanced…"}</button>
          <label>show{" "}
            <select value={q.limit || "100"} onChange={(e) => go({ limit: e.target.value })}>
              {[50, 100, 250, 500, 1000].map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
          </label>
        </div>
        <p className="muted small" style={{ margin: "8px 0 0" }}>
          {d.source === "profile" ? "From full profile sweeps - every player, by home server." : "From the in-game leaderboards - only who the game publishes."}
          {d.note && ` ${d.note[0].toUpperCase()}${d.note.slice(1)}.`}
          {d.vs ? ` Movement is against ${when(d.vs)}.` : ""}
          {d.top > 0 && !d.others && ` Each server counts only its ${d.top} strongest alliances on this measure.`}
        </p>
      </div>

      {d.series.length > 0 && d.snapshots.length > 1 && (
        <div className="card">
          <h2>{d.label} over time <span className="muted small" style={{ textTransform: "none", letterSpacing: 0 }}>- tick rows in the table to add them (up to 12)</span></h2>
          <MultiLineChart format={fmt} label={d.label}
            series={d.series.map((s, i) => ({
              key: s.key, label: s.label || s.key, color: colour(s.key, i),
              points: s.points.map((p) => ({ x: p.at, y: p.value })),
            }))} />
        </div>
      )}

      {d.top > 0 && d.rows.length > 0 && (
        <div className="card">
          <h2>{d.label} by server - top {d.top} alliances{d.others ? " and everyone else" : ""}</h2>
          <BarList format={fmt} limit={40} rows={d.rows.slice(0, 40).map((r) => {
            const share = (v) => `${Math.round((Number(v) / Number(r.value)) * 100)}%`;
            const rest = Number(r.value) - (r.parts || []).reduce((s, a) => s + Number(a.value), 0);
            return {
              key: r.key, label: `#${r.server}`, value: Number(r.value), href: href.server(r.server),
              parts: splitParts((r.parts || []).map((a) => ({ ...a, color: allianceColour(a.allianceId) })), Number(r.value), d.others),
              detail: (
                <>
                  {(r.parts || []).map((a) => <div key={a.allianceId}>{a.tag || "?"}: {fmt(a.value)} ({share(a.value)})</div>)}
                  {d.others && <div className="muted">everyone else: {fmt(rest)} ({share(rest)})</div>}
                </>
              ),
            };
          })} />
        </div>
      )}

      <div className="card">
        <h2>{d.label} - {d.entity}{d.server ? ` on #${d.server}` : ` across ${d.lo}-${d.hi}`}</h2>
        {d.rows.length === 0 ? <p className="muted">No data for this measure yet.</p> : (
          <div className="scroll">
            <table>
              <thead>
                <tr>
                  <th className="num">#</th>{d.vs && <th>move</th>}
                  <th>{d.entity === "servers" ? "server" : d.entity === "alliances" ? "alliance" : "player"}</th>
                  {d.entity === "players" && <th>alliance</th>}
                  {d.entity !== "servers" && <th>server</th>}
                  <th className="num">{d.label}</th>{d.vs && <th className="num">change</th>}
                  <th title="chart it">chart</th>
                </tr>
              </thead>
              <tbody>
                {d.rows.map((r) => {
                  const key = String(r.key);
                  return (
                    <tr key={key}>
                      <td className="num">{r.rank}</td>
                      {d.vs && <td><Move rank={r.rank} prev={r.prevRank} /></td>}
                      <td className="subject">
                        {d.entity === "players" ? <PlayerLink uid={key} name={r.name} />
                          : d.entity === "alliances" ? <><Tag id={r.allianceId} tag={r.tag || "?"} /> <span className="muted small">{r.name}</span></>
                          : <ServerLink id={r.server} />}
                      </td>
                      {d.entity === "players" && <td><Tag id={r.allianceId} tag={r.tag} /></td>}
                      {d.entity !== "servers" && <td><ServerLink id={r.server} /></td>}
                      <td className="num">{d.format === "int" ? int(r.value) : big(r.value)}</td>
                      {d.vs && <td className="num">{r.prevValue === undefined ? <span className="muted">–</span> : <Delta value={Number(r.value) - Number(r.prevValue)} />}</td>}
                      <td><input type="checkbox" style={{ width: "auto" }} checked={charted.includes(key)} onChange={() => toggle(key)} /></td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
