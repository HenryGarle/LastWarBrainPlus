import { useMemo } from "react";
import { useHashParam } from "../nav.js";
import { SortTable } from "./charts.jsx";
import { big, int, when } from "./format.js";
import { Tag, PlayerLink, ServerLink } from "./bits.jsx";
import { useJson } from "./PlayerTabs.jsx";

// HQ levels on the tracked servers (Insights.HqLevels): how many of each level every server has, and who reached HQ <min>+
// and when. Exact times come from the HQ board (top 100 a server); everyone else is dated by our ~hourly readings.
const stamp = (iso) => (iso ? iso.slice(0, 16).replace("T", " ") : "");
const reachedAt = (r) => r?.at || r?.by || null;

function Reached({ r }) {
  if (!r || !reachedAt(r)) return <span className="muted">?</span>;
  if (r.at) return <span className="mono small" title="exact: the moment the upgrade finished (HQ board)">{stamp(r.at)}</span>;
  return (
    <span className="mono small muted" title={r.after ? `between ${when(r.after)} and ${when(r.by)} (our readings)` : `by ${when(r.by)} - already there at our first reading`}>
      {r.after ? "≈ " : "≤ "}{stamp(r.by)}
    </span>
  );
}

export function HqLevelsPage() {
  const [min, setMin] = useHashParam("min", "31");
  const [server, setServer] = useHashParam("hqserver", "");
  const d = useJson(`/api/x/hq?min=${min}`);

  const servers = useMemo(() => {
    if (!d?.counts) return null;
    const levels = [...new Set(d.counts.map((c) => c.level))].filter((l) => l >= 30).sort((a, b) => a - b);
    const by = new Map();
    for (const c of d.counts) {
      const r = by.get(c.server) || { server: c.server, total: 0, below: 0 };
      r.total += c.n;
      if (c.level >= 30) r[`l${c.level}`] = c.n; else r.below += c.n;
      by.set(c.server, r);
    }
    const act = new Map((d.active || []).map((a) => [a.server, a.n]));
    const rows = [...by.values()].map((r) => ({ ...r, active30: act.get(r.server) || 0, up: levels.filter((l) => l >= Number(min)).reduce((s, l) => s + (r[`l${l}`] || 0), 0) }));
    const all = Object.fromEntries(levels.map((l) => [l, rows.reduce((s, r) => s + (r[`l${l}`] || 0), 0)]));
    all.active30 = rows.reduce((s, r) => s + r.active30, 0);
    return { levels, rows, all };
  }, [d, min]);

  const players = useMemo(() => (d?.players || []).filter((p) => !server || p.server === Number(server)), [d, server]);
  const levels = useMemo(() => {
    const top = Math.max(Number(min), ...(d?.players || []).map((p) => p.level));
    return Array.from({ length: top - Number(min) + 1 }, (_, i) => Number(min) + i);
  }, [d, min]);
  // The first to each level, and how many got there per day.
  const firsts = useMemo(() => levels.map((l) => {
    const got = players.map((p) => ({ p, at: reachedAt(p.reached?.[l]) })).filter((x) => x.at).sort((a, b) => a.at.localeCompare(b.at));
    const days = {};
    for (const x of got) days[x.at.slice(0, 10)] = (days[x.at.slice(0, 10)] || 0) + 1;
    return { level: l, n: players.filter((p) => p.level >= l).length, first: got[0], days };
  }), [players, levels]);

  if (!d) return <div className="card"><p className="muted">Loading…</p></div>;
  if (d.error) return <div className="card"><p className="err">{d.error}</p></div>;
  if (!servers) return <div className="card"><p className="muted">No profile sweep yet.</p></div>;
  return (
    <div className="grid">
      <div className="card">
        <div className="row" style={{ justifyContent: "space-between", gap: 8, flexWrap: "wrap" }}>
          <h2 style={{ margin: 0 }}>HQ levels by server</h2>
          <span className="small muted">players by home server, profile sweep {when(d.sweep)}</span>
        </div>
        <p className="small" style={{ margin: "6px 0" }}>
          All tracked servers: {servers.levels.map((l) => <span key={l} style={{ marginRight: 12 }}>HQ {l}: <strong>{int(servers.all[l])}</strong></span>)}
          <span>active HQ 30+: <strong>{int(servers.all.active30)}</strong></span>
        </p>
        <SortTable
          rows={servers.rows}
          initial={{ key: "up", dir: -1 }}
          limit={200}
          columns={[
            { key: "server", label: "server", render: (r) => <ServerLink id={r.server} /> },
            { key: "up", label: `HQ ${min}+`, render: (r) => <strong>{int(r.up)}</strong> },
            { key: "active30", label: "active HQ 30+", render: (r) => int(r.active30) },
            ...[...servers.levels].reverse().map((l) => ({ key: `l${l}`, label: `HQ ${l}`, render: (r) => (r[`l${l}`] ? int(r[`l${l}`]) : <span className="muted">–</span>) })),
            { key: "below", label: "below 30", render: (r) => <span className="muted">{int(r.below)}</span> },
            { key: "total", label: "players", render: (r) => int(r.total) },
          ]}
        />
      </div>

      <div className="card">
        <div className="row" style={{ justifyContent: "space-between", gap: 8, flexWrap: "wrap" }}>
          <h2 style={{ margin: 0 }}>Reached HQ {min}+ <span className="muted">({int(players.length)})</span></h2>
          <span className="row small" style={{ gap: 8 }}>
            <select value={min} onChange={(e) => setMin(e.target.value)} style={{ width: "auto" }}>
              {[31, 32, 33, 34, 35].map((l) => <option key={l} value={l}>HQ {l}+</option>)}
            </select>
            <select value={server} onChange={(e) => setServer(e.target.value)} style={{ width: "auto" }}>
              <option value="">every tracked server</option>
              {servers.rows.filter((r) => r.up > 0).sort((a, b) => a.server - b.server).map((r) => <option key={r.server} value={r.server}>#{r.server} ({r.up})</option>)}
            </select>
          </span>
        </div>
        <table className="small" style={{ margin: "8px 0" }}>
          <thead><tr><th>level</th><th className="num">players</th><th>first</th><th>new per day (UTC)</th></tr></thead>
          <tbody>
            {firsts.map((f) => (
              <tr key={f.level}>
                <td>HQ {f.level}</td>
                <td className="num">{int(f.n)}</td>
                <td>{f.first ? <><PlayerLink uid={f.first.p.uid} name={f.first.p.name} /> <ServerLink id={f.first.p.server} /> <span className="mono small">{stamp(f.first.at)}</span></> : <span className="muted">nobody yet</span>}</td>
                <td className="mono small">{Object.entries(f.days).sort().map(([dd, n]) => `${dd.slice(5)}: ${n}`).join(" · ") || "–"}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <SortTable
          rows={players}
          limit={3000}
          initial={{ key: `r${min}`, dir: 1 }}
          columns={[
            { key: "name", label: "player", render: (p) => <PlayerLink uid={p.uid} name={p.name} /> },
            { key: "tag", label: "alliance", render: (p) => <Tag id={p.allianceId} tag={p.tag} /> },
            { key: "server", label: "server", render: (p) => <ServerLink id={p.server} /> },
            { key: "level", label: "HQ", render: (p) => <strong>{p.level}</strong> },
            ...levels.map((l) => ({ key: `r${l}`, label: `reached ${l}`, sort: (p) => reachedAt(p.reached?.[l]), render: (p) => (p.level >= l ? <Reached r={p.reached?.[l]} /> : <span className="muted">–</span>) })),
            { key: "power", label: "power", render: (p) => big(p.power) },
          ]}
        />
        <p className="small muted" style={{ marginBottom: 0 }}>
          Active HQ 30+ = HQ 30 or higher and seen in the game (login, donation or chat) in the last 3 days. Times are UTC. A plain time is exact: the HQ board (top 100 per server) records when the upgrade finished.
          &ldquo;≈&rdquo; is from our own readings (roughly hourly): between the last reading below that level and this one - hover for the window.
          &ldquo;≤&rdquo; means they were already there at our first reading. Readings go back to late September.
        </p>
      </div>
    </div>
  );
}
