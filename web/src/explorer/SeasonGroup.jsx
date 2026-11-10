import { useMemo, useState } from "react";
import { useJson } from "./PlayerTabs.jsx";
import { big, int, ago } from "./format.js";
import { PlayerLink, ServerLink, Tag } from "./bits.jsx";
import { useHashParam } from "../nav.js";

// #/x/season[/<cfg>][?focus=<server>&favs=1&top=N]  One season group: its servers side by side, seeds, alliance ranking guess,
// top players. Anonymous by default: no server is singled out and favourite marks are hidden unless asked for.

// Server measures, best first. get(r) reads one Overview row (+ depth counts) from /api/x/season-group.
const MEASURES = [
  ["eventFight", "Event strength", "hero power weighted by how often each player was in the game at past season events (Wednesday and Saturday, 12:00 server time; the hour either side counts half)", (r) => r.eventFight],
  ["eventRegulars80", "Event regulars 80M+", "players with 80M+ estimated hero power seen at half or more of past events", (r) => r.eventRegulars80],
  ["fight", "Fighting strength", "hero power weighted by how recently each player was in the game (a day 1, 3 days 0.75, a week 0.35, older 0.1)", (r) => r.fight],
  ["activeHero7", "Active hero power", "estimated hero power of players active in the last 7 days", (r) => r.activeHero7],
  ["heroBoard", "Hero board top 200", "in-game top-200 hero board value of the counted players (exact, not estimated)", (r) => r.heroBoard],
  ["top100Hero", "Top 100 hero", "estimated hero power of the server's 100 strongest", (r) => r.top100Hero],
  ["top20Hero", "Top 20 hero", "estimated hero power of the server's 20 strongest (rally leaders)", (r) => r.top20Hero],
  ["topHero", "Strongest player", "estimated hero power of the server's #1", (r) => r.topHero],
  ["hero100", "Active 100M+", "players active in 7 days with 100M+ estimated hero power", (r) => r.hero100],
  ["hero80", "Active 80M+", "players active in 7 days with 80M+ estimated hero power", (r) => r.hero80],
  ["hero60", "Active 60M+", "players active in 7 days with 60M+ estimated hero power", (r) => r.hero60],
  ["hq30", "Active HQ 30+", "players at HQ 30+ active in 7 days", (r) => r.hq30],
  ["active", "Active players", "logged in, donated or chatted in 7 days", (r) => r.active],
  ["power", "Total power", "every counted player, newest profile", (r) => r.power],
];
const isCount = (k) => ["hero100", "hero80", "hero60", "hq30", "active", "eventRegulars80"].includes(k);
const fmt = (k, v) => (v == null ? "–" : isCount(k) ? int(v) : big(v));

/** Explore -> Season group. Nothing loads until a group is picked (the group data is heavy). */
export function SeasonGroupPage({ route }) {
  if (route.id) return <SeasonGroup cfg={route.id} />;
  return (
    <div className="card">
      <h2>Season group</h2>
      <p><GroupPicker cfg="" /></p>
      <p className="small muted">Pick a season group to compare its servers, alliances and players.</p>
    </div>
  );
}

/** Every season group we know in our block (from the game or entered by hand), newest config first. */
function GroupPicker({ cfg }) {
  const g = useJson("/api/x/season-groups?lo=1957&hi=2020");
  if (g && !Array.isArray(g)) return <span className="err">Could not load season groups.</span>;
  if (!g) return <span className="muted">Loading groups…</span>;
  const cfgs = [...new Map(g.map((r) => [r.cfg, r])).values()].sort((a, b) => b.cfg - a.cfg);
  const go = (c) => { if (!c) return; const q = window.location.hash.split("?")[1]; window.location.hash = `#/x/season/${c}${q ? `?${q}` : ""}`; };
  return (
    <label>Group{" "}
      <select value={cfg} onChange={(e) => go(e.target.value)}>
        {!cfg && <option value="">pick a group</option>}
        {cfgs.map((r) => <option key={r.cfg} value={r.cfg}>{r.season ? `S${r.season} ` : ""}{r.cfg}</option>)}
      </select>
    </label>
  );
}
function SeasonGroup({ cfg }) {
  const [topQ, setTop] = useHashParam("top", "0");
  const [favsQ, setFavs] = useHashParam("favs", "0");
  const [homeQ, setHome] = useHashParam("focus", "");
  const top = Math.max(0, Number(topQ) || 0);
  const d = useJson(`/api/x/season-group/${cfg}?top=${top}`);
  const [sortKey, setSort] = useState("eventFight");
  const home = Number(homeQ) || 0;   // 0 = no server singled out
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
    <div className={favsQ === "1" ? undefined : "hide-favs"}>
      <div className="card">
        <h2>Season {d.season?.season ?? "?"} group {cfg}</h2>
        <div style={{ display: "flex", gap: 16, flexWrap: "wrap", alignItems: "center", marginBottom: 8 }}>
          <GroupPicker cfg={cfg} />
          <label>Count players from{" "}
            <select value={top} onChange={(e) => setTop(e.target.value)}>
              <option value={0}>everyone on the server</option>
              {[1, 2, 3, 5, 10, 15, 20, 30].map((n) => <option key={n} value={n}>each server's top {n} alliance{n > 1 ? "s" : ""}</option>)}
            </select>
          </label>
          <label>Focus on{" "}
            <select value={home || ""} onChange={(e) => setHome(e.target.value)}>
              <option value="">no server</option>
              {[...servers].sort((a, b) => a.server - b.server).map((s) => <option key={s.server} value={s.server}>#{s.server}</option>)}
            </select>
          </label>
          <label><input type="checkbox" checked={favsQ === "1"} onChange={(e) => setFavs(e.target.checked ? "1" : "0")} /> Show my favourites</label>
        </div>
        <p>
          Pre-season {date(d.season?.preStart)} · starts {date(d.season?.start)} · ends {date(d.season?.end)} · {servers.length} servers:{" "}
          {servers.map((r, i) => <span key={r.server}>{i > 0 && ", "}<ServerLink id={r.server} />{seeds.has(r.server) && <span title="seeded server (named in the game's season table)"> ★</span>}</span>)}
        </p>
        <p className="small muted">
          ★ = seeded: the two servers the game's own season table names for this group (decoded from lw_season). On the earlier draw these were the group&apos;s two strongest.
          {byHand > 0 && ` ${byHand} of the servers were entered by hand from the in-game display; the game confirms them when the season starts.`}
          {" "}Strength is estimated hero power from player profiles (the in-game hero board where shown). Active = logged in, donated or chatted in the last 7 days.
          {top > 0 && `  Server measures count only players in each server's ${top} strongest alliance${top > 1 ? "s" : ""} (by fighting strength of their members from that server).`}
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
                <th>#</th><th>server</th>{top > 0 && <th>alliances counted</th>}
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
                  {top > 0 && <td className="muted">{r.counted || "–"}</td>}
                  {MEASURES.map(([k, , , get]) => (
                    <td key={k} className="num" style={ranks[r.server]?.[k] === 1 ? { fontWeight: 700 } : undefined}>{fmt(k, get(r))}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <EventEffect servers={servers} alliances={d.alliances || []} event={d.event} home={home} />
      <EventMatchups list={d.alliances || []} event={d.event} />
      <Pairings servers={servers} alliances={d.alliances || []} seeds={seeds} />
      <Bars servers={servers} home={home} seeds={seeds} />
      <Alliances list={d.alliances || []} home={home} />
      <Players list={d.players || []} servers={servers} home={home} />
    </div>
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
  const [k, setK] = useState("eventFight");
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
      {home > 0 && <p className="small muted">{label}: orange = #{home}.</p>}
    </div>
  );
}

/** The season-ranking guess: every alliance on the group's servers by fighting strength. */
function Alliances({ list, home }) {
  const [sortKey, setSort] = useState("fight");
  const [all, setAll] = useState(false);
  const cols = [
    ["fight", "Fighting strength"], ["core30", "Top 30 strength"], ["eventFight", "Event strength"], ["eventCore30", "Event top 30"], ["hero", "Hero power"], ["members", "Members"],
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

const rankBy = (list, key, id) => {
  const r = {};
  [...list].sort((a, b) => Number(b[key] ?? 0) - Number(a[key] ?? 0)).forEach((x, i) => { r[x[id]] = i + 1; });
  return r;
};
const Move = ({ from, to }) => {
  const d = from - to;
  return d === 0 ? <span className="muted">=</span> : <span className={d > 0 ? "up" : "down"}>{d > 0 ? `▲ ${d}` : `▼ ${-d}`}</span>;
};

/** Does event timing change anything? Overall fighting strength next to event strength, ranks before and after. */
function EventEffect({ servers, alliances, event, home }) {
  const sF = rankBy(servers, "fight", "server"), sE = rankBy(servers, "eventFight", "server");
  const aF = rankBy(alliances, "core30", "allianceId"), aE = rankBy(alliances, "eventCore30", "allianceId");
  const top = [...alliances].sort((a, b) => Number(b.eventCore30) - Number(a.eventCore30)).slice(0, 20);
  const share = (e, f) => (f ? `${Math.round((Number(e) / Number(f)) * 100)}%` : "–");
  return (
    <div className="card">
      <h2>Event timing: does it change anything?</h2>
      <p className="small muted">
        Season events run {event?.days} at {String(event?.hour ?? 12).padStart(2, "0")}:00 server time. For each player we look back over the
        last {event?.lookbackDays} days ({event?.eventsSeen} past events) at how often they showed a sign of play in the event hour (a login,
        donation or chat; the hour either side counts half). Event strength = hero power x that share. &quot;Turns up&quot; = event strength as a share of
        hero power - how much of the side is usually there at event time.
      </p>
      {event?.readingsPerDay < 12 && <p className="err small">This copy took only {event.readingsPerDay} profile readings a day in that window - too few to see who is on at event time. Use the hosted site (hourly readings).</p>}
      <div style={{ display: "flex", gap: 24, flexWrap: "wrap" }}>
        <table className="small">
          <thead><tr><th>server</th><th className="num">overall rank</th><th className="num">event rank</th><th>move</th><th className="num">turns up</th></tr></thead>
          <tbody>
            {[...servers].sort((a, b) => sE[a.server] - sE[b.server]).map((s) => (
              <tr key={s.server} style={s.server === home ? { background: "rgba(217,89,38,0.10)" } : undefined}>
                <td><ServerLink id={s.server} /></td><td className="num">{sF[s.server]}</td><td className="num">{sE[s.server]}</td>
                <td><Move from={sF[s.server]} to={sE[s.server]} /></td>
                <td className="num">{share(s.eventFight, s.fight)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <table className="small">
          <thead><tr><th>alliance</th><th>server</th><th className="num">top-30 rank</th><th className="num">event top-30 rank</th><th>move</th><th className="num">turns up</th></tr></thead>
          <tbody>
            {top.map((a) => (
              <tr key={a.allianceId} style={a.server === home ? { background: "rgba(217,89,38,0.10)" } : undefined}>
                <td className="subject"><Tag id={a.allianceId} tag={a.tag} /></td><td><ServerLink id={a.server} /></td>
                <td className="num">{aF[a.allianceId]}</td><td className="num">{aE[a.allianceId]}</td>
                <td><Move from={aF[a.allianceId]} to={aE[a.allianceId]} /></td>
                <td className="num">{share(a.eventCore30, a.core30)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

/** Pick an alliance: its likely result at event time against every other alliance in the group. */
function EventMatchups({ list, event }) {
  const ranked = [...list].sort((a, b) => Number(b.eventCore30) - Number(a.eventCore30));
  const [pick, setPick] = useHashParam("al", "");
  const me = list.find((a) => a.allianceId === pick);
  // Square law on the 30 strongest (rallies and garrisons): chance = a^2 / (a^2 + b^2) - the Matchup's default steepness.
  const chance = (a, b) => { const x = Number(a) ** 2, y = Number(b) ** 2; return x + y > 0 ? x / (x + y) : 0.5; };
  const verdict = (p) => (p >= 0.65 ? ["likely win", "up"] : p >= 0.55 ? ["edge", "up"] : p > 0.45 ? ["toss-up", "muted"] : p > 0.35 ? ["behind", "down"] : ["likely lose", "down"]);
  return (
    <div className="card">
      <h2>
        Event battles:{" "}
        <select value={pick} onChange={(e) => setPick(e.target.value)}>
          <option value="">pick an alliance</option>
          {ranked.map((a) => <option key={a.allianceId} value={a.allianceId}>{a.tag} (#{a.server})</option>)}
        </select>
      </h2>
      <p className="small muted">
        Each side&apos;s 30 strongest, weighted by how often they were in the game at past events ({event?.days} {String(event?.hour ?? 12).padStart(2, "0")}:00).
        Chance = square law on those totals (a&sup2; / (a&sup2; + b&sup2;)), the War game&apos;s default. &quot;Any time&quot; uses general recency instead, to show what event timing changes.
        A heuristic from estimated hero power and attendance - it knows nothing of line-ups, gear, counters or who rallies.
      </p>
      {me && (
        <table className="small">
          <thead><tr><th>against</th><th>server</th><th className="num">their event top 30</th><th className="num">ours</th><th className="num">event chance</th><th>verdict</th><th className="num">any-time chance</th></tr></thead>
          <tbody>
            {ranked.filter((a) => a.allianceId !== me.allianceId)
              .map((a) => ({ a, p: chance(me.eventCore30, a.eventCore30), q: chance(me.core30, a.core30) }))
              .sort((x, y) => y.p - x.p)
              .map(({ a, p, q }) => {
                const [label, cls] = verdict(p);
                return (
                  <tr key={a.allianceId}>
                    <td className="subject"><Tag id={a.allianceId} tag={a.tag} /></td><td><ServerLink id={a.server} /></td>
                    <td className="num">{big(a.eventCore30)}</td><td className="num">{big(me.eventCore30)}</td>
                    <td className="num">{Math.round(p * 100)}%</td><td className={cls}>{label}</td>
                    <td className="num muted">{Math.round(q * 100)}%</td>
                  </tr>
                );
              })}
          </tbody>
        </table>
      )}
    </div>
  );
}
/** Every way of splitting the group into two factions of equal size. With two seeds they lead opposite sides and
 * never move; otherwise the first server anchors side A. Returns [{ a: [...], b: [...] }]. */
function splits(servers, seeds) {
  const ids = servers.map((s) => s.server).sort((x, y) => x - y);
  const half = ids.length / 2;
  const sd = ids.filter((s) => seeds.has(s));
  const anchorA = sd.length === 2 ? [sd[0]] : [ids[0]];
  const fixedB = sd.length === 2 ? [sd[1]] : [];
  const rest = ids.filter((s) => !anchorA.includes(s) && !fixedB.includes(s));
  const need = half - anchorA.length;
  const out = [];
  const pick = (start, chosen) => {
    if (chosen.length === need) { out.push({ a: [...anchorA, ...chosen], b: ids.filter((s) => !anchorA.includes(s) && !chosen.includes(s)) }); return; }
    for (let i = start; i < rest.length; i++) pick(i + 1, [...chosen, rest[i]]);
  };
  if (Number.isInteger(half) && need >= 0) pick(0, []);
  return out;
}
const sq = (a, b) => { const x = a * a, y = b * b; return x + y > 0 ? x / (x + y) : 0.5; };

/** Faction pairings: for a server, which partners make its side strongest; for an alliance, which servers it would
 * rather face. Worked out on demand from the data already on the page (20 splits for 8 servers with 2 seeds). */
function Pairings({ servers, alliances, seeds }) {
  const [open, setOpen] = useState(false);
  const [mode, setMode] = useState("server");
  const [measure, setMeasure] = useState("eventFight");
  const [srv, setSrv] = useHashParam("pserver", "");
  const [al, setAl] = useHashParam("palliance", "");
  if (!open) {
    return (
      <div className="card">
        <h2>Faction pairings</h2>
        <p className="small muted">Which servers to share a faction with, and which to face - every possible 4 v 4 split, worked out when you ask.</p>
        <button onClick={() => setOpen(true)}>Work out pairings</button>
      </div>
    );
  }
  const get = MEASURES.find((m) => m[0] === measure)[3];
  const val = Object.fromEntries(servers.map((s) => [s.server, Number(get(s) ?? 0)]));
  const all = splits(servers, seeds).map((s) => {
    const A = s.a.reduce((t, x) => t + val[x], 0), B = s.b.reduce((t, x) => t + val[x], 0);
    return { ...s, A, B };
  });
  const evt = measure.startsWith("event");
  const alKey = evt ? "eventCore30" : "core30";
  const ids = [...servers].map((s) => s.server).sort((x, y) => x - y);
  const List = ({ xs }) => xs.map((x, i) => <span key={x}>{i > 0 && ", "}<ServerLink id={x} />{seeds.has(x) && " ★"}</span>);

  let body = null;
  if (mode === "server" && srv) {
    const S = Number(srv);
    const rows = all.map((s) => {
      const mine = s.a.includes(S) ? "a" : "b";
      const us = mine === "a" ? s.A : s.B, them = mine === "a" ? s.B : s.A;
      return { partners: (mine === "a" ? s.a : s.b).filter((x) => x !== S), enemies: mine === "a" ? s.b : s.a, us, them, p: sq(us, them) };
    }).sort((x, y) => y.p - x.p);
    // Each other server: our average chance in the splits where it sits with us, and where it is against us.
    const others = ids.filter((x) => x !== S).map((x) => {
      const w = rows.filter((r) => r.partners.includes(x)), v = rows.filter((r) => r.enemies.includes(x));
      const avg = (l) => (l.length ? l.reduce((t, r) => t + r.p, 0) / l.length : null);
      return { x, with: avg(w), against: avg(v), nWith: w.length, nAgainst: v.length };
    }).sort((p, q) => (q.with ?? -1) - (p.with ?? -1));
    body = (
      <>
        <h3>Best partners for #{S}</h3>
        <table className="small">
          <thead><tr><th>server</th><th className="num">our chance with them</th><th className="num">our chance against them</th><th className="num">difference</th></tr></thead>
          <tbody>
            {others.map((o) => (
              <tr key={o.x}>
                <td><ServerLink id={o.x} />{seeds.has(o.x) && " ★"}</td>
                <td className="num">{o.with == null ? (seeds.has(o.x) ? "never (seed)" : "–") : `${Math.round(o.with * 100)}%`}</td>
                <td className="num">{o.against == null ? (seeds.has(o.x) ? "never (seed)" : "–") : `${Math.round(o.against * 100)}%`}</td>
                <td className="num">{o.with != null && o.against != null ? `${Math.round((o.with - o.against) * 100)} pts` : "–"}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <h3>Every split, best for #{S} first</h3>
        <table className="small">
          <thead><tr><th>#{S} with</th><th>against</th><th className="num">our side</th><th className="num">their side</th><th className="num">faction chance</th></tr></thead>
          <tbody>
            {rows.map((r, i) => (
              <tr key={i}><td><List xs={r.partners} /></td><td><List xs={r.enemies} /></td>
                <td className="num">{big(r.us)}</td><td className="num">{big(r.them)}</td><td className="num">{Math.round(r.p * 100)}%</td></tr>
            ))}
          </tbody>
        </table>
      </>
    );
  }
  if (mode === "alliance" && al) {
    const me = alliances.find((a) => a.allianceId === al);
    if (me) {
      const mine = Number(me[alKey] ?? 0);
      const strongest = (srvIds) => alliances.filter((a) => srvIds.includes(a.server) && a.allianceId !== me.allianceId)
        .sort((x, y) => Number(y[alKey]) - Number(x[alKey]));
      // Each other server as an opponent: how many of its alliances are stronger than us, and our chance against its best.
      const opp = ids.filter((x) => x !== me.server).map((x) => {
        const l = strongest([x]);
        const top = l[0];
        return { x, stronger: l.filter((a) => Number(a[alKey]) > mine).length, top, p: top ? sq(mine, Number(top[alKey])) : 1 };
      }).sort((p, q) => p.stronger - q.stronger || q.p - p.p);
      const rows = all.map((s) => {
        const side = s.a.includes(me.server) ? s : { a: s.b, b: s.a, A: s.B, B: s.A };
        const enemies = strongest(side.b);
        return { partners: side.a.filter((x) => x !== me.server), enemiesSrv: side.b, stronger: enemies.filter((a) => Number(a[alKey]) > mine).length,
                 best: enemies[0], p: enemies[0] ? sq(mine, Number(enemies[0][alKey])) : 1, faction: sq(side.A, side.B) };
      }).sort((x, y) => x.stronger - y.stronger || y.faction - x.faction);
      body = (
        <>
          <h3>Servers {me.tag} would rather face</h3>
          <table className="small">
            <thead><tr><th>server</th><th className="num">alliances stronger than {me.tag}</th><th>its strongest</th><th className="num">{me.tag}&apos;s chance against it</th></tr></thead>
            <tbody>
              {opp.map((o) => (
                <tr key={o.x}><td><ServerLink id={o.x} />{seeds.has(o.x) && " ★"}</td><td className="num">{o.stronger}</td>
                  <td>{o.top ? <Tag id={o.top.allianceId} tag={o.top.tag} /> : "–"}</td><td className="num">{Math.round(o.p * 100)}%</td></tr>
              ))}
            </tbody>
          </table>
          <h3>Every split for {me.tag} (#{me.server}), easiest first</h3>
          <table className="small">
            <thead><tr><th>with</th><th>against</th><th className="num">enemy alliances stronger</th><th>strongest enemy</th><th className="num">chance v it</th><th className="num">faction chance</th></tr></thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={i}><td><List xs={r.partners} /></td><td><List xs={r.enemiesSrv} /></td><td className="num">{r.stronger}</td>
                  <td>{r.best ? <Tag id={r.best.allianceId} tag={r.best.tag} /> : "–"}</td><td className="num">{Math.round(r.p * 100)}%</td>
                  <td className="num">{Math.round(r.faction * 100)}%</td></tr>
              ))}
            </tbody>
          </table>
        </>
      );
    }
  }

  return (
    <div className="card">
      <h2>Faction pairings</h2>
      <div style={{ display: "flex", gap: 16, flexWrap: "wrap", alignItems: "center", marginBottom: 8 }}>
        <label><input type="radio" checked={mode === "server"} onChange={() => setMode("server")} /> for a server</label>
        <label><input type="radio" checked={mode === "alliance"} onChange={() => setMode("alliance")} /> for an alliance</label>
        {mode === "server" ? (
          <select value={srv} onChange={(e) => setSrv(e.target.value)}>
            <option value="">pick a server</option>
            {ids.map((x) => <option key={x} value={x}>#{x}{seeds.has(x) ? " (seed)" : ""}</option>)}
          </select>
        ) : (
          <select value={al} onChange={(e) => setAl(e.target.value)}>
            <option value="">pick an alliance</option>
            {[...alliances].sort((x, y) => Number(y[alKey]) - Number(x[alKey])).map((a) => <option key={a.allianceId} value={a.allianceId}>{a.tag} (#{a.server})</option>)}
          </select>
        )}
        <label>strength by{" "}
          <select value={measure} onChange={(e) => setMeasure(e.target.value)}>{MEASURES.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>
        </label>
      </div>
      <p className="small muted">
        {all.length} possible 4 v 4 splits. {seeds.size === 2 ? "The two seeds (★) lead opposite factions and never move. " : ""}
        A faction&apos;s strength is the sum of its servers&apos; {MEASURES.find((m) => m[0] === measure)[1].toLowerCase()} (with the
        &quot;count players from&quot; setting above); chance = square law on the two totals. For an alliance, opponents are compared on
        {evt ? " event top-30 strength" : " top-30 strength"}. A heuristic for arguing a case, not a prediction.
      </p>
      {body}
    </div>
  );
}