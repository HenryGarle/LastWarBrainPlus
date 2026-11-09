import { useEffect, useMemo, useState } from "react";
import { useJson } from "./PlayerTabs.jsx";
import { big, int, ago } from "./format.js";
import { PlayerLink, ServerLink, Tag } from "./bits.jsx";
import { useHashParam } from "../nav.js";

// #/x/season[/<cfg>][?home=1967]  One season group: its servers side by side, seeds, alliance ranking guess, top players.
const HOME = 1967;

// Server measures, best first. get(r) reads one Overview row (+ depth counts) from /api/x/season-group.
const MEASURES = [
  ["fight", "Fighting strength", "hero power weighted by how recently each player was in the game (a day 1, 3 days 0.75, a week 0.35, older 0.1)", (r) => r.totals?.fight],
  ["activeHero7", "Active hero power", "estimated hero power of players active in the last 7 days", (r) => r.totals?.activeHero7],
  ["heroBoard", "Hero board top 200", "sum of the in-game top-200 hero power board (exact, not estimated)", (r) => r.totals?.heroBoard],
  ["top100Hero", "Top 100 hero", "estimated hero power of the server's 100 strongest", (r) => r.top100Hero],
  ["top20Hero", "Top 20 hero", "estimated hero power of the server's 20 strongest (rally leaders)", (r) => r.top20Hero],
  ["topHero", "Strongest player", "estimated hero power of the server's #1", (r) => r.topHero],
  ["hero100", "Active 100M+", "players active in 7 days with 100M+ estimated hero power", (r) => r.hero100],
  ["hero80", "Active 80M+", "players active in 7 days with 80M+ estimated hero power", (r) => r.hero80],
  ["hero60", "Active 60M+", "players active in 7 days with 60M+ estimated hero power", (r) => r.hero60],
  ["hq30", "Active HQ 30+", "players at HQ 30+ active in 7 days", (r) => r.hq30],
  ["active", "Active players", "logged in, donated or chatted in 7 days", (r) => r.active],
  ["power", "Total power", "every player from the server, newest profile", (r) => r.power],
];
const isCount = (k) => ["hero100", "hero80", "hero60", "hq30", "active"].includes(k);
const fmt = (k, v) => (v == null ? "–" : isCount(k) ? int(v) : big(v));

/** Explore -> Season group. With no cfg in the route, opens the newest group holding our server. */
export function SeasonGroupPage({ route }) {
  const groups = useJson(route.id ? null : "/api/x/season-groups?lo=1957&hi=2020");
  useEffect(() => {
    if (route.id || !Array.isArray(groups)) return;
    const mine = groups.filter((g) => g.server === HOME).map((g) => g.cfg);
    if (mine.length) window.location.replace(`#/x/season/${Math.max(...mine)}`);
  }, [route.id, groups]);
  if (!route.id) return <p className="muted">{groups && !Array.isArray(groups) ? "Could not load season groups." : "Finding our season group…"}</p>;
  return <SeasonGroup cfg={route.id} />;
}

function SeasonGroup({ cfg }) {
  const d = useJson(`/api/x/season-group/${cfg}`);
  const [homeQ, setHome] = useHashParam("home", String(HOME));
  const [sortKey, setSort] = useState("fight");
  const home = Number(homeQ) || HOME;
  const seeds = useMemo(() => new Set(String(d?.season?.seeds || "").split(",").filter(Boolean).map(Number)), [d]);
  if (!d) return <p className="muted">Loading season group {cfg}…</p>;
  if (d.error) return <p className="err">Could not load ({d.error}).</p>;
  const servers = d.servers || [];
  const byHand = (d.members || []).filter((m) => m.by === "hand").length;
  const date = (s) => (s ? String(s).slice(0, 10).replaceAll("/", "-") : "?");

  // Rank of every server on every measure (1 = best).
  const ranks = {};
  for (const [k, , , get] of MEASURES) {
    const sorted = [...servers].sort((a, b) => (get(b) ?? -1) - (get(a) ?? -1));
    sorted.forEach((r, i) => { (ranks[r.server] ??= {})[k] = i + 1; });
  }
  const sortGet = MEASURES.find((m) => m[0] === sortKey)[3];
  const rows = [...servers].sort((a, b) => (sortGet(b) ?? -1) - (sortGet(a) ?? -1));
  const homeRow = servers.find((r) => r.server === home);

  return (
    <>
      <div className="card">
        <h2>Season {d.season?.season ?? "?"} group {cfg}</h2>
        <p>
          Pre-season {date(d.season?.preStart)} · starts {date(d.season?.start)} · ends {date(d.season?.end)} · {servers.length} servers:{" "}
          {servers.map((r, i) => <span key={r.server}>{i > 0 && ", "}<ServerLink id={r.server} />{seeds.has(r.server) && <span title="seeded server (named in the game's season table)"> ★</span>}</span>)}
        </p>
        <p className="small muted">
          ★ = seeded: the two servers the game's own season table names for this group (decoded from lw_season). On the earlier draw these were the group&apos;s two strongest.
          {byHand > 0 && ` ${byHand} of the servers were entered by hand from the in-game display; the game confirms them when the season starts.`}
          {" "}Strength is estimated hero power from player profiles (the in-game hero board where shown). Active = logged in, donated or chatted in the last 7 days.
        </p>
      </div>

      {homeRow && <Standing row={homeRow} ranks={ranks[home] || {}} servers={servers} home={home} setHome={setHome} alliances={d.alliances || []} />}

      <div className="card">
        <h2>Servers side by side</h2>
        <p className="small muted">Click a column to rank by it. Bold = best in the group on that measure.</p>
        <div style={{ overflowX: "auto" }}>
          <table className="small">
            <thead>
              <tr>
                <th>#</th><th>server</th>
                {MEASURES.map(([k, label, tip]) => (
                  <th key={k} className="num" title={tip} style={{ cursor: "pointer", textDecoration: k === sortKey ? "underline" : undefined }} onClick={() => setSort(k)}>{label}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={r.server} style={r.server === home ? { background: "var(--hl, rgba(57,135,229,0.12))" } : undefined}>
                  <td className="num">{i + 1}</td>
                  <td><ServerLink id={r.server} />{seeds.has(r.server) && " ★"}</td>
                  {MEASURES.map(([k, , , get]) => (
                    <td key={k} className="num" style={ranks[r.server]?.[k] === 1 ? { fontWeight: 700 } : undefined}>{fmt(k, get(r))}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <Bars servers={servers} home={home} seeds={seeds} />
      <Alliances list={d.alliances || []} home={home} />
      <Players list={d.players || []} servers={servers} home={home} />
    </>
  );
}

/** Where one server stands: its rank and lead on every measure. */
function Standing({ row, ranks, servers, home, setHome, alliances }) {
  const n = servers.length;
  const firsts = MEASURES.filter(([k]) => ranks[k] === 1).map((m) => m[1]);
  const top3 = MEASURES.filter(([k]) => ranks[k] <= 3).length;
  return (
    <div className="card">
      <h2>
        Where{" "}
        <select value={home} onChange={(e) => setHome(e.target.value)}>
          {[...servers].sort((a, b) => a.server - b.server).map((s) => <option key={s.server} value={s.server}>#{s.server}</option>)}
        </select>{" "}
        stands
      </h2>
      <p>
        <b>#{home}</b> is first in the group on <b>{firsts.length}</b> of {MEASURES.length} measures and top 3 on <b>{top3}</b>.
        {firsts.length > 0 && <> First on: {firsts.join(", ")}.</>}
      </p>
      {alliances.length > 0 && (() => {
        const ranked = [...alliances].sort((a, b) => Number(b.fight) - Number(a.fight));
        const mine = ranked.map((a, i) => [a, i + 1]).filter(([a]) => a.server === home);
        return mine.length > 0 && (
          <p>
            Alliances from #{home} in the group ranking guess (of {ranked.length}):{" "}
            {mine.slice(0, 5).map(([a, r], i) => <span key={a.allianceId}>{i > 0 && ", "}<Tag id={a.allianceId} tag={a.tag} /> #{r}</span>)}.
          </p>
        );
      })()}
      <table className="small">
        <thead><tr><th>measure</th><th className="num">#{home}</th><th className="num">rank</th><th className="num">group median</th><th className="num">vs median</th><th className="num">next best / leader</th></tr></thead>
        <tbody>
          {MEASURES.map(([k, label, tip, get]) => {
            const vals = servers.map(get).filter((v) => v != null).map(Number).sort((a, b) => a - b);
            const mid = vals.length ? (vals.length % 2 ? vals[(vals.length - 1) / 2] : (vals[vals.length / 2 - 1] + vals[vals.length / 2]) / 2) : null;
            const v = get(row) == null ? null : Number(get(row));
            const others = servers.filter((s) => s.server !== home).map(get).filter((x) => x != null).map(Number);
            const bestOther = others.length ? Math.max(...others) : null;
            const lead = v != null && bestOther ? v / bestOther - 1 : null;
            return (
              <tr key={k} title={tip}>
                <td>{label}</td>
                <td className="num">{fmt(k, v)}</td>
                <td className="num" style={ranks[k] === 1 ? { fontWeight: 700 } : undefined}>{ranks[k]} of {n}</td>
                <td className="num muted">{fmt(k, mid)}</td>
                <td className="num">{v != null && mid ? `${v >= mid ? "+" : ""}${Math.round((v / mid - 1) * 100)}%` : "–"}</td>
                <td className="num">{lead == null ? "–" : `${Math.abs(Math.round(lead * 100))}% ${lead >= 0 ? "ahead of #2" : "behind #1"}`}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/** One measure as bars, server by server. */
function Bars({ servers, home, seeds }) {
  const [k, setK] = useState("activeHero7");
  const [, label, tip, get] = MEASURES.find((m) => m[0] === k);
  const rows = [...servers].sort((a, b) => (get(b) ?? 0) - (get(a) ?? 0));
  const max = Math.max(1, ...rows.map((r) => Number(get(r) ?? 0)));
  return (
    <div className="card">
      <h2>
        Compare by{" "}
        <select value={k} onChange={(e) => setK(e.target.value)}>{MEASURES.map(([key, l]) => <option key={key} value={key}>{l}</option>)}</select>
      </h2>
      <p className="small muted">{tip}</p>
      {rows.map((r) => (
        <div key={r.server} style={{ display: "flex", alignItems: "center", gap: 8, margin: "4px 0" }}>
          <div style={{ width: 70 }}><ServerLink id={r.server} />{seeds.has(r.server) && " ★"}</div>
          <div style={{ flex: 1, background: "rgba(127,127,127,0.12)", borderRadius: 3 }}>
            <div style={{ width: `${(Number(get(r) ?? 0) / max) * 100}%`, height: 18, borderRadius: 3, background: r.server === home ? "#d95926" : "#3987e5" }} />
          </div>
          <div className="mono small" style={{ width: 80, textAlign: "right" }}>{fmt(k, get(r))}</div>
        </div>
      ))}
      <p className="small muted">{label}: orange = #{home}.</p>
    </div>
  );
}

/** The season-ranking guess: every alliance on the group's servers by fighting strength. */
function Alliances({ list, home }) {
  const [sortKey, setSort] = useState("fight");
  const [all, setAll] = useState(false);
  const cols = [
    ["fight", "Fighting strength"], ["core30", "Top 30 strength"], ["hero", "Hero power"], ["members", "Members"],
    ["active7", "Active 7d"], ["hero80", "Active 80M+"], ["topHero", "Strongest"], ["power", "Total power"],
  ];
  const rows = [...list].sort((a, b) => Number(b[sortKey] ?? 0) - Number(a[sortKey] ?? 0));
  const shown = all ? rows : rows.slice(0, 20);
  const top = (n) => {
    const c = {};
    rows.slice(0, n).forEach((r) => { c[r.server] = (c[r.server] || 0) + 1; });
    return Object.entries(c).sort((a, b) => b[1] - a[1]);
  };
  return (
    <div className="card">
      <h2>Alliance ranking guess</h2>
      <p className="small muted">
        Our guess at the season&apos;s alliance standings: every alliance (5+ members) from these servers, ranked by fighting strength - the
        whole alliance&apos;s hero power weighted by recent activity. Season points come from taking and holding cities, which needs strong,
        active players; &quot;top 30 strength&quot; is the same for its 30 strongest (rally and garrison core). A guess, not a forecast of the game&apos;s scoring.
      </p>
      <p className="small">
        Top 10 by server: {top(10).map(([s, n], i) => <span key={s}>{i > 0 && " · "}<b style={Number(s) === home ? { color: "#d95926" } : undefined}>#{s}</b> {n}</span>)}
        {" "}&nbsp;|&nbsp; Top 20: {top(20).map(([s, n], i) => <span key={s}>{i > 0 && " · "}<b style={Number(s) === home ? { color: "#d95926" } : undefined}>#{s}</b> {n}</span>)}
      </p>
      <div style={{ overflowX: "auto" }}>
        <table className="small">
          <thead>
            <tr><th>#</th><th>alliance</th><th>server</th>
              {cols.map(([k, l]) => <th key={k} className="num" style={{ cursor: "pointer", textDecoration: k === sortKey ? "underline" : undefined }} onClick={() => setSort(k)}>{l}</th>)}
            </tr>
          </thead>
          <tbody>
            {shown.map((r, i) => (
              <tr key={r.allianceId} style={r.server === home ? { background: "rgba(217,89,38,0.10)" } : undefined}>
                <td className="num">{i + 1}</td>
                <td className="subject"><Tag id={r.allianceId} tag={r.tag} /></td>
                <td><ServerLink id={r.server} /></td>
                {cols.map(([k]) => <td key={k} className="num">{["members", "active7", "hero80"].includes(k) ? int(r[k]) : big(r[k])}</td>)}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {rows.length > 20 && <button onClick={() => setAll(!all)}>{all ? "Show top 20" : `Show all ${rows.length}`}</button>}
    </div>
  );
}

/** The group's 50 strongest players, and how many each server has among them. */
function Players({ list, servers, home }) {
  const counts = servers.map((s) => [s.server, list.filter((p) => p.server === s.server).length]).sort((a, b) => b[1] - a[1]);
  return (
    <div className="card">
      <h2>Strongest players in the group</h2>
      <p className="small">
        Of the group&apos;s top {list.length}: {counts.map(([s, n], i) => <span key={s}>{i > 0 && " · "}<b style={s === home ? { color: "#d95926" } : undefined}>#{s}</b> {n}</span>)}
      </p>
      <table className="small">
        <thead><tr><th>#</th><th>player</th><th>alliance</th><th>server</th><th className="num">hero (est)</th><th className="num">power</th><th className="num">HQ</th><th>last in game</th></tr></thead>
        <tbody>
          {list.map((p, i) => (
            <tr key={p.uid} style={p.server === home ? { background: "rgba(217,89,38,0.10)" } : undefined}>
              <td className="num">{i + 1}</td>
              <td><PlayerLink uid={p.uid} name={p.name} /></td>
              <td><Tag id={p.allianceId} tag={p.tag} /></td>
              <td><ServerLink id={p.server} /></td>
              <td className="num">{big(p.hero)}</td>
              <td className="num">{big(p.power)}</td>
              <td className="num">{p.level || "–"}</td>
              <td className="muted">{p.lastActive ? ago(p.lastActive) : "–"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
