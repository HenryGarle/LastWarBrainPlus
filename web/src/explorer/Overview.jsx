import { BarList, SortTable, splitParts } from "./charts.jsx";
import { allianceColour, big, href, int, pct, ago, when } from "./format.js";
import { useEffect, useState } from "react";
import { useHashParam } from "../nav.js";
import { ServerLink, NameChanges, Tag, PlayerLink } from "./bits.jsx";
import { rankingsHref } from "./Rankings.jsx";

// The "by server" chart: what to measure, and how many of each server's alliances to split out.
const MEASURES = {
  power: [["activeHero7", "Active hero power (7 days)", big], ["heroEst", "Hero power (everyone)", big], ["fight", "Fighting strength", big],
    ["heroBoard", "Hero power (top-200 boards)", big], ["power", "Total power", big]],
  activity: [["active7", "Active players, 7 days", int], ["active1d", "Active players, today", int]],
};
const NOTES = {
  power: "Every player's newest profile, by the server they are from. Includes buildings and troops.",
  heroEst: "Hero power estimated for every player from their profile - within about 1% of the in-game hero boards; accounts under 80M power are counted down (they are mostly buildings, and mostly abandoned).",
  heroBoard: "Only players on their server's newest top-200 hero board, so everyone below 200th is missing.",
  fight: "Hero power × how recently each player was in the game (a day 1, 3 days 0.75, a week 0.35, older 0.1).",
  active7: "Players from the server who logged in, donated or chatted in the last 7 days.",
  active1d: "Players from the server who logged in, donated or chatted in the last day.",
  activeHero7: "Hero power of the players active in the last 7 days - the strength that is actually playing.",
};
const TOPS = ["0", "3", "5", "10", "15"];
// The same measure on the server rankings.
const RANK_KEY = { power: "power", heroEst: "heroest", heroBoard: "hero200", fight: "fight", active7: "active7", active1d: "active7", activeHero7: "activehero7" };

function ByServer({ data }) {
  const [m, setM] = useHashParam("pm", "activeHero7");
  const [n, setN] = useHashParam("ptop", "5");
  const [o, setO] = useHashParam("pother", "hide");
  const [all, setAll] = useState(false);
  const view = MEASURES.activity.some(([k]) => k === m) ? "activity" : "power";
  const list = MEASURES[view];
  const [key, , fmt] = list.find(([k]) => k === m) || list[0];
  const top = Number(n) || 0;
  const others = o !== "hide" || top === 0;
  const rows = data.filter((r) => r.totals?.[key]).map((r) => {
    const total = r.totals[key];
    const al = [...(r.topAlliances || [])].sort((a, b) => b[key] - a[key]).slice(0, top).filter((a) => a[key] > 0);
    const rest = total - al.reduce((s, a) => s + a[key], 0);
    const value = others ? total : total - rest;
    const share = (v) => `${Math.round((v / total) * 100)}%`;
    const of = (a) => (view === "activity" ? ` · ${int(a.active7)} of ${int(a.n)} active this week` : "");
    return {
      key: r.server, label: `#${r.server}`, value, href: href.server(r.server),
      // Each alliance in its own colour - the same one it has on the map and on every tag.
      parts: top ? splitParts(al.map((a) => ({ tag: a.tag, value: a[key], color: allianceColour(a.allianceId), allianceId: a.allianceId })), value, others) : undefined,
      detail: (
        <>
          <div>{int(r.totals.n)} players · {int(r.totals.active7)} active this week ({Math.round((r.totals.active7 / Math.max(1, r.totals.n)) * 100)}%)</div>
          {al.map((a) => <div key={a.allianceId}>{a.tag || "?"}: {fmt(a[key])} ({share(a[key])}){of(a)}</div>)}
          {top > 0 && <div className="muted">everyone else: {fmt(rest)} ({share(rest)})</div>}
        </>
      ),
    };
  }).filter((r) => r.value > 0).sort((a, b) => b.value - a.value);
  return (
    <div className="card">
      <div className="row" style={{ justifyContent: "space-between", flexWrap: "wrap", gap: 8, marginBottom: 8 }}>
        <h2 style={{ margin: 0 }}>{view === "activity" ? "Activity by server" : "Strength by server"}</h2>
        <div className="cmp-tabs" style={{ margin: 0 }}>
          {[["power", "Strength"], ["activity", "Activity"]].map(([v, l]) => (
            <a key={v} href="#" className={v === view ? "on" : ""} onClick={(e) => { e.preventDefault(); setM(MEASURES[v][0][0]); }}>{l}</a>
          ))}
        </div>
      </div>
      <div className="rank-controls row small" style={{ gap: 14, flexWrap: "wrap", marginBottom: 6 }}>
        <label>measure
          <select value={key} onChange={(e) => setM(e.target.value)}>
            {list.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
          </select>
        </label>
        <label>alliances
          <select value={String(top)} onChange={(e) => setN(e.target.value)}>
            {TOPS.map((v) => <option key={v} value={v}>{v === "0" ? "none" : `top ${v}`}</option>)}
          </select>
        </label>
        <label>everyone else
          <select value={others ? "show" : "hide"} disabled={top === 0} onChange={(e) => setO(e.target.value)}>
            <option value="hide">hide</option>
            <option value="show">show</option>
          </select>
        </label>
        <a href={rankingsHref("servers", RANK_KEY[key] || "heroest", { top: top || undefined, others: others ? undefined : "hide" })}>open in rankings →</a>
      </div>
      <p className="muted small">{NOTES[key]} Click an alliance's part of a bar to open it.</p>
      <BarList rows={all ? rows : rows.slice(0, 12)} format={fmt} limit={40} />
      {rows.length > 12 && <button className="linkish" onClick={() => setAll(!all)}>{all ? "Show the top 12" : `Show all ${rows.length} servers`}</button>}
    </div>
  );
}

// ---- highlights -------------------------------------------------------------------------------
const PERIODS = [["1", "24 hours"], ["7", "7 days"], ["30", "30 days"]];
const signed = (v) => (v == null ? "–" : `${v > 0 ? "+" : "−"}${big(Math.abs(v))}`);
// Every list: [kind, title, icon, tone, how its number reads]. The Changes page shows them in full.
export const CHANGES = [
  ["troops", "Biggest troop losses", "☠", "bad", (r) => signed(r.dtroops)],
  ["kills", "Top killers", "⚔", "good", (r) => `${big(r.dkills)} kills`],
  ["allianceTroops", "Alliances losing most troops", "▼", "bad", (r) => signed(r.dtroops)],
  ["allianceKills", "Alliances with most kills", "★", "good", (r) => `${big(r.dkills)} kills`],
  ["allianceHero", "Alliances gaining hero power", "▲", "good", (r) => signed(r.dhero)],
  ["power", "Biggest power drops", "↘", "bad", (r) => signed(r.dpower)],
  ["serverHero", "Servers gaining hero power", "⬆", "good", (r) => signed(r.dhero)],
  ["serverTroops", "Servers losing most troops", "⬇", "bad", (r) => signed(r.dtroops)],
  ["hero", "Players gaining hero power", "✦", "good", (r) => signed(r.dhero)],
];

function Callout({ title, icon, rows, value, tone, kind, days }) {
  if (!rows?.length) return null;
  return (
    <div className={`callout ${tone || ""}`}>
      <a className="callout-title" href={`#/x/changes?kind=${kind}&period=${days}`} title="See the full list">
        <span className="callout-icon">{icon}</span>{title}<span className="callout-more">all →</span>
      </a>
      <ol>
        {rows.slice(0, 5).map((r, i) => (
          <li key={i}>
            <span className="callout-who">{r.who}</span>
            <span className="callout-val">{value(r)}</span>
          </li>
        ))}
      </ol>
    </div>
  );
}

function Highlights() {
  const [days, setDays] = useHashParam("period", "7");
  const [h, setH] = useState(null);
  useEffect(() => {
    let live = true;
    setH(null);
    fetch(`/api/x/highlights?days=${days}`).then((r) => r.json()).then((x) => live && setH(x)).catch(() => live && setH({}));
    return () => { live = false; };
  }, [days]);
  const p = (kind) => (h?.players || []).filter((r) => r.kind === kind).map((r) => ({
    ...r, who: <><PlayerLink uid={r.uid} name={r.name} /> {r.tag && <Tag id={r.allianceId} tag={r.tag} />} <span className="muted small">#{r.server}</span></>,
  }));
  const g = (kind) => (h?.groups || []).filter((r) => r.kind === kind).map((r) => ({
    ...r, who: r.allianceId ? <><Tag id={r.allianceId} tag={r.tag || "?"} /> <span className="muted small">#{r.server} · {r.members} members</span></> : <ServerLink id={r.server} />,
  }));
  return (
    <div className="card highlights">
      <div className="row" style={{ justifyContent: "space-between", flexWrap: "wrap", gap: 8 }}>
        <h2 style={{ margin: 0 }}>What changed</h2>
        <div className="chips">
          {PERIODS.map(([v, l]) => <button key={v} className={days === v ? "chip on" : "chip"} onClick={() => setDays(v)}>{l}</button>)}
        </div>
      </div>
      {!h ? <p className="muted small">Working it out…</p> : (
        <>
          <p className="muted small">
            Since {when(h.from)}{h.short ? " - as far back as our scans go, so this is the closest we can measure" : ""}. Click a heading for the full list.
          </p>
          <div className="callouts">
            {CHANGES.map(([kind, title, icon, tone, value]) => (
              <Callout key={kind} kind={kind} days={days} title={title} icon={icon} tone={tone}
                rows={kind.startsWith("alliance") || kind.startsWith("server") ? g(kind) : p(kind)} value={value} />
            ))}
          </div>
        </>
      )}
    </div>
  );
}

// ---- servers as cards ---------------------------------------------------------------------------
function ServerCards({ data }) {
  const rows = [...data].filter((r) => r.totals).sort((a, b) => (b.totals.activeHero7 || 0) - (a.totals.activeHero7 || 0));
  const max = Math.max(1, ...rows.map((r) => r.totals.activeHero7 || 0));
  return (
    <div className="card">
      <div className="row" style={{ justifyContent: "space-between" }}>
        <h2 style={{ margin: 0 }}>Servers</h2>
        <span className="muted small">by active hero power (players seen this week) · click one to open it</span>
      </div>
      <div className="server-cards" style={{ marginTop: 10 }}>
        {rows.map((r, i) => (
          <a key={r.server} className="server-card" href={href.server(r.server)}>
            <div className="sc-head"><span className="sc-num">#{r.server}</span><span className="sc-rank">{i + 1}</span></div>
            <div className="sc-hero">{big(r.totals.activeHero7)}<span className="muted small"> active hero</span></div>
            <div className="sc-bar"><i style={{ width: `${((r.totals.activeHero7 || 0) / max) * 100}%` }} /></div>
            <div className="small muted">{int(r.totals.n)} players · {pct(r.active, r.compared)} active</div>
            <div className="sc-tags">
              {[...(r.topAlliances || [])].sort((a, b) => b.heroEst - a.heroEst).slice(0, 3).map((a) => (
                <span key={a.allianceId} className="tag" style={{ "--al": allianceColour(a.allianceId) }}>{a.tag || "?"}</span>
              ))}
            </div>
          </a>
        ))}
      </div>
    </div>
  );
}

// The newest renames anywhere - its own page (Explorer › Name changes).
export function NamesPage() {
  const [kind, setKind] = useHashParam("names", "player");
  const [rows, setRows] = useState(null);
  useEffect(() => {
    let live = true;
    setRows(null);
    fetch(`/api/x/names?kind=${kind}&limit=200`).then((r) => r.json()).then((x) => live && setRows(x)).catch(() => {});
    return () => { live = false; };
  }, [kind]);
  return (
    <div className="card">
      <div className="row" style={{ justifyContent: "space-between", marginBottom: 8 }}>
        <h2 style={{ margin: 0 }}>Recent name changes</h2>
        <div className="cmp-tabs" style={{ margin: 0 }}>
          {[["player", "Players"], ["alliance", "Alliances"]].map(([k, l]) => (
            <a key={k} href="#" className={k === kind ? "on" : ""} onClick={(e) => { e.preventDefault(); setKind(k); }}>{l}</a>
          ))}
        </div>
      </div>
      {rows ? <NameChanges rows={rows} showWho /> : <p className="muted small">Loading…</p>}
    </div>
  );
}

const ROUTES = [
  ["#/maps", "◈", "Maps", "Every server's map, heat map of where the power sits"],
  [rankingsHref(), "▤", "Rankings", "Alliances, players and servers - who is rising, who is falling"],
  ["#/war", "⚔", "War game", "Two alliances head to head, hour by hour"],
  ["#/x/compare/alliances/", "⇆", "Compare", "Up to four servers, alliances or players side by side"],
];

export default function Overview({ data }) {
  const players = data.reduce((s, r) => s + (r.totals?.n || 0), 0);
  const hero = data.reduce((s, r) => s + (r.totals?.heroEst || 0), 0);
  const activeHero = data.reduce((s, r) => s + (r.totals?.activeHero7 || 0), 0);
  const compared = data.reduce((s, r) => s + (r.compared || 0), 0);   // everyone FROM a tracked server
  const active = data.reduce((s, r) => s + (r.active || 0), 0);
  const active1d = data.reduce((s, r) => s + (r.active1d || 0), 0);
  const newest = data.map((r) => r.snapshotAt).filter(Boolean).sort().pop();

  return (
    <div className="grid">
      <section className="dash-hero">
        <div className="dash-hero-text">
          <h1>Last War <b>Brain</b></h1>
          <p>{data.length} servers · {int(players)} players tracked · updated {ago(newest)}</p>
        </div>
        <div className="kpis">
          <div className="kpi"><div className="kpi-label">Active hero power</div><div className="kpi-value">{big(activeHero)}</div><div className="kpi-sub">players seen this week · {big(hero)} counting everyone</div></div>
          <div className="kpi"><div className="kpi-label">Active this week</div><div className="kpi-value">{compared ? pct(active, compared) : "–"}</div><div className="kpi-sub">{int(active)} players · {int(active1d)} today</div></div>
          <div className="kpi"><div className="kpi-label">Players</div><div className="kpi-value">{big(players)}</div><div className="kpi-sub">on {data.length} servers</div></div>
        </div>
      </section>

      <div className="routes">
        {ROUTES.map(([to, icon, title, sub]) => (
          <a key={title} className="route" href={to}>
            <span className="route-icon">{icon}</span>
            <span><b>{title}</b><span className="muted small">{sub}</span></span>
          </a>
        ))}
      </div>

      <Highlights />

      <ServerCards data={data} />

      <ByServer data={data} />

      <div className="card">
        <h2>Every server, in numbers</h2>
        {(
          <SortTable
            initial={{ key: "active7hero", dir: -1 }}
            rows={data}
            columns={[
              { key: "server", label: "server", render: (r) => <ServerLink id={r.server} /> },
              { key: "active7hero", label: "active hero power", num: true, sort: (r) => r.totals?.activeHero7 ?? null, render: (r) => big(r.totals?.activeHero7) },
              { key: "hero", label: "hero power (all)", num: true, sort: (r) => r.totals?.heroEst ?? null, render: (r) => big(r.totals?.heroEst) },
              { key: "topHero", label: "strongest", num: true, render: (r) => big(r.topHero) },
              { key: "players", label: "on the map", num: true, render: (r) => int(r.players) },
              { key: "alliances", label: "alliances", num: true },
              { key: "power", label: "total power", num: true, render: (r) => big(r.power) },
              { key: "compared", label: "players from", num: true, render: (r) => int(r.compared) },
              { key: "active1d", label: "active today", num: true, sort: (r) => (r.compared ? r.active1d / r.compared : null),
                render: (r) => (r.compared ? `${pct(r.active1d, r.compared)} (${int(r.active1d)})` : "–") },
              { key: "active", label: "active 7 days", num: true, sort: (r) => (r.compared ? r.active / r.compared : null),
                render: (r) => (r.compared ? `${pct(r.active, r.compared)} (${int(r.active)})` : "–") },
              { key: "scanned", label: "mapped", render: (r) => <span className="muted small">{ago(r.scanned)}</span> },
            ]}
          />
        )}
      </div>
    </div>
  );
}

// The full list behind one dashboard callout (Explore › Overview › What changed).
export function ChangesPage() {
  const [kind, setKind] = useHashParam("kind", "troops");
  const [days, setDays] = useHashParam("period", "7");
  const [d, setD] = useState(null);
  useEffect(() => {
    let live = true;
    setD(null);
    fetch(`/api/x/changes?kind=${kind}&days=${days}&limit=300`).then((r) => r.json()).then((x) => live && setD(x)).catch(() => live && setD({ rows: [] }));
    return () => { live = false; };
  }, [kind, days]);
  const def = CHANGES.find(([k]) => k === kind) || CHANGES[0];
  const group = kind.startsWith("alliance") ? "alliance" : kind.startsWith("server") ? "server" : "player";
  const num = (key, label) => ({ key, label, num: true, render: (r) => (key === "dkills" ? big(r[key]) : signed(r[key])) });
  const columns = [
    { key: "rank", label: "#", num: true, sort: (r) => r.rank },
    ...(group === "player" ? [
      { key: "name", label: "player", render: (r) => <PlayerLink uid={r.uid} name={r.name} /> },
      { key: "tag", label: "alliance", render: (r) => <Tag id={r.allianceId} tag={r.tag} /> },
      { key: "server", label: "server", render: (r) => <ServerLink id={r.server} /> },
    ] : group === "alliance" ? [
      { key: "tag", label: "alliance", render: (r) => <Tag id={r.allianceId} tag={r.tag || "?"} /> },
      { key: "server", label: "server", render: (r) => <ServerLink id={r.server} /> },
      { key: "members", label: "members", num: true },
    ] : [
      { key: "server", label: "server", render: (r) => <ServerLink id={r.server} /> },
      { key: "members", label: "players", num: true, render: (r) => int(r.members) },
    ]),
    { key: "hero", label: "hero power now", num: true, render: (r) => big(r.hero) },
    num("dhero", "hero change"), num("dtroops", "troop change"), num("dkills", "kills"), num("dpower", "power change"),
  ];
  const rows = (d?.rows || []).map((r, i) => ({ ...r, rank: i + 1 }));
  return (
    <div className="grid">
      <div className="card">
        <div className="row" style={{ justifyContent: "space-between", flexWrap: "wrap", gap: 8 }}>
          <label className="small muted">list{" "}
            <select value={kind} onChange={(e) => setKind(e.target.value)} style={{ width: "auto" }}>
              {CHANGES.map(([k, t]) => <option key={k} value={k}>{t}</option>)}
            </select>
          </label>
          <div className="chips">
            {PERIODS.map(([v, l]) => <button key={v} className={days === v ? "chip on" : "chip"} onClick={() => setDays(v)}>{l}</button>)}
          </div>
        </div>
        <p className="muted small" style={{ marginBottom: 0 }}>
          {d ? <>Since {when(d.from)}{d.short ? " - as far back as our scans go, the closest we can measure" : ""}. Players whose older reading lacked the value are left out rather than counted from zero.</> : "Working it out…"}
        </p>
      </div>
      <div className="card">
        <h2>{def[2]} {def[1]}</h2>
        {!d ? <p className="muted small">Loading…</p> : <SortTable rows={rows} initial={{ key: "rank", dir: 1 }} columns={columns} empty="Nothing moved in this period." />}
      </div>
    </div>
  );
}
