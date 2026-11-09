import { useEffect, useState } from "react";
import { big, int, when, ago } from "../explorer/format.js";
import { Hero, PlayerLink, ServerLink, Tag } from "../explorer/bits.jsx";
import { SortTable } from "../explorer/charts.jsx";
import { useJson } from "../explorer/PlayerTabs.jsx";
import Svs from "./Svs.jsx";
import { OnlinePanel } from "../explorer/Online.jsx";
import { Level } from "../explorer/ServersPage.jsx";
import { sectionArgs, useHashParam, setTitle } from "../nav.js";

// Events tab: one page per cross-server event. Meteorite Iron War, calendar, SvS (Warzone Duel).
const EVENTS = [["meteorite", "Meteorite Iron War"], ["calendar", "Calendar"], ["svs", "SvS"]];

export default function Events({ admin }) {
  const [args, setArgs] = useState(sectionArgs);
  const page = args[0] || "meteorite";
  useEffect(() => {
    const on = () => setArgs(sectionArgs());
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);
  useEffect(() => { setTitle("Events", EVENTS.find(([id]) => id === page)?.[1]); }, [page]);
  return (
    <div className="grid">
      <nav className="subnav big">
        {EVENTS.map(([id, label]) => (
          <a key={id} href={`#/events/${id}`} className={page === id ? "on" : ""}>{label}</a>
        ))}
      </nav>
      {page === "svs" ? <Svs admin={admin} initial={args[1]} />
        : page === "calendar" ? <Calendar admin={admin} id={args[1]} />
        : <Meteorite admin={admin} />}
    </div>
  );
}

const COLORS = ["#4ea1ff", "#f0883e", "#3fb950", "#d2a8ff", "#e3b341", "#ff7b72", "#56d4dd", "#a5d6a7"];
const parse = (s) => {
  const out = [];
  for (const part of String(s || "").split(/[\s,;]+/).filter(Boolean)) {
    const [a, b] = part.split("-").map(Number);
    if (!a) continue;
    if (b && b >= a && b - a < 16) for (let i = a; i <= b; i++) out.push(i); else out.push(a);
  }
  return [...new Set(out)].sort((x, y) => x - y);
};

// Next contest rounds: Saturday 00:10 and 12:30 server time (UTC), 30 minutes each.
function nextRounds(now = new Date()) {
  const out = [];
  const sat = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
  sat.setUTCDate(sat.getUTCDate() + ((6 - sat.getUTCDay() + 7) % 7));
  for (let w = 0; out.length < 2 && w < 2; w++) {
    for (const [h, m] of [[0, 10], [12, 30]]) {
      const t = new Date(sat.getTime() + w * 7 * 864e5 + (h * 60 + m) * 6e4);
      if (t.getTime() + 30 * 6e4 > now.getTime()) out.push(t);
    }
  }
  return out.slice(0, 2);
}
const until = (t) => {
  const m = Math.round((t - Date.now()) / 6e4);
  if (m <= 0) return "on now";
  const d = Math.floor(m / 1440), h = Math.floor((m % 1440) / 60);
  return d ? `in ${d} d ${h} h` : h ? `in ${h} h ${m % 60} min` : `in ${m} min`;
};
const local = (t) => t.toLocaleString(undefined, { weekday: "short", hour: "2-digit", minute: "2-digit" });
const utc = (t) => `${t.toUTCString().slice(0, 3)} ${t.toISOString().slice(11, 16)} UTC`;

// Role by HQ, as the guides put it: HQ 30+ Stellar Cores, 22-29 Crystals, 21 and under Debris.
const role = (hq) => (hq == null ? null : hq >= 30 ? ["Cores", "#d2a8ff"] : hq >= 22 ? ["Crystals", "#56d4dd"] : ["Debris", "#e3b341"]);
const Role = ({ hq }) => { const r = role(hq); return r ? <span className="small" style={{ color: r[1] }}>{r[0]}</span> : <span className="muted">–</span>; };

function Meteorite({ admin }) {
  // ?servers= in the URL looks at any servers without saving; none = the saved list.
  const [picked, setPicked] = useHashParam("servers", "");
  const [nonce, setNonce] = useState(0);
  const d = useJson(`/api/x/events/meteorite?servers=${encodeURIComponent(picked)}&n=${nonce}`);
  const [add, setAdd] = useState("");
  const [msg, setMsg] = useState("");
  const [onlineOf, setOnlineOf] = useState("top");
  const [only, setOnly] = useState(0);
  const [likely, setLikely] = useState("");
  const ids = d?.servers || [];
  const colour = Object.fromEntries(ids.map((s, i) => [s, COLORS[i % COLORS.length]]));
  const saved = d?.saved || [];
  const dirty = ids.join(",") !== saved.join(",");
  const setIds = (next) => setPicked(next.length ? next.join(",") : "");

  const save = async () => {
    const r = await fetch("/api/events/meteorite", { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ servers: ids.join(",") }) });
    setMsg(r.ok ? "Saved - everyone now opens on these servers." : `Could not save (${r.status}).`);
    if (r.ok) { setPicked(""); setNonce((n) => n + 1); }
  };
  const untracked = (d?.serverStats || []).filter((s) => s.level !== "full").map((s) => s.server);
  const track = async () => {
    const r = await fetch("/api/servers/track", { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ servers: untracked.join(","), level: "full", visible: true }) });
    setMsg(r.ok ? `Tracking ${untracked.join(", ")} fully from the next daily refresh (or run Refresh stats in Operations now).` : `Could not change tracking (${r.status}).`);
    if (r.ok) setNonce((n) => n + 1);
  };

  const rounds = nextRounds();
  const pool = (d?.pools || [])[0];
  const LIKELY = { "": [() => true, "everyone"], r1: [(p) => p.likely1, "likely on R1"], r2: [(p) => p.likely2, "likely on R2"], both: [(p) => p.likely1 && p.likely2, "likely on both"] };
  const top = (d?.top || []).filter((p) => (!only || p.server === only) && LIKELY[likely][0](p)).slice(0, 50);
  const past = d?.pastRounds || { r1: 0, r2: 0 };

  return (
    <>
      <div className="page-head">
        <div>
          <h1 className="page-title">Meteorite Iron War</h1>
          <p className="muted">About four warzones fight over a meteor that lands on one warzone&apos;s Capitol. Add the servers in this week&apos;s crater to size up who you face.</p>
        </div>
        <div className="small" style={{ textAlign: "right" }}>
          {rounds.map((t, i) => (
            <div key={i}><b>{i === 0 ? "Next round" : "Then"}</b> {utc(t)} <span className="muted">· {local(t)} your time · {until(t)}</span></div>
          ))}
        </div>
      </div>

      <div className="card">
        <div className="row small" style={{ gap: 8, flexWrap: "wrap", alignItems: "center" }}>
          <b>Servers</b>
          {ids.map((s) => (
            <span key={s} className="chip" style={{ borderColor: colour[s] }}>
              <span style={{ color: colour[s] }}>●</span> <ServerLink id={s} />
              <button className="linkish" title="remove" onClick={() => setIds(ids.filter((x) => x !== s))}> ×</button>
            </span>
          ))}
          <form onSubmit={(e) => { e.preventDefault(); const n = parse(add); if (n.length) setIds([...new Set([...ids, ...n])].sort((a, b) => a - b)); setAdd(""); }}>
            <input value={add} onChange={(e) => setAdd(e.target.value)} placeholder="add server, e.g. 1970 or 1970,1975" style={{ width: 210 }} />
          </form>
          {dirty && admin && <button className="primary" onClick={save}>Save for everyone</button>}
          {dirty && picked && <button className="linkish" onClick={() => setPicked("")}>back to saved</button>}
          {admin && untracked.length > 0 && <button onClick={track} title="Daily boards, profile sweep and top players for these servers">Track {untracked.length === 1 ? `#${untracked[0]}` : `${untracked.length} servers`} fully</button>}
        </div>
        {msg && <p className="small">{msg}</p>}
        {ids.length === 0 && <p className="muted small">No servers yet. The preview stage (Tuesday to Friday) shows in game which warzones share the crater - add them here.</p>}
        {pool && (
          <p className="small muted">
            Game&apos;s matchmaking pool{pool.current ? " this week" : ` (newest in the game tables: ${pool.open} to ${pool.close})`}: <span className="mono">{pool.groups}</span>
            {pool.season ? ` · season ${pool.season}` : " · off-season"}. The game picks the ~4 warzones from this pool each week, so the exact crater comes from the preview.
          </p>
        )}
      </div>

      {!d ? <p className="muted">Loading…</p> : d.error ? <p className="err">Could not load ({d.error}).</p> : ids.length > 0 && (
        <>
          <ServerTable rows={d.serverStats || []} colour={colour} />
          <div className="card">
            <div className="row" style={{ justifyContent: "space-between", flexWrap: "wrap", gap: 8 }}>
              <h3 style={{ margin: 0 }}>Top 50 players across these servers, by hero power</h3>
              <div className="chips">
                {Object.entries(LIKELY).map(([k, [, label]]) => <button key={k} className={`chip ${likely === k ? "on" : ""}`} onClick={() => setLikely(k)}>{label}</button>)}
              </div>
              <div className="chips">
                <button className={`chip ${!only ? "on" : ""}`} onClick={() => setOnly(0)}>all</button>
                {ids.map((s) => <button key={s} className={`chip ${only === s ? "on" : ""}`} onClick={() => setOnly(s)}>#{s}</button>)}
              </div>
            </div>
            <SortTable rows={top} initial={{ key: "rank", dir: 1 }} limit={50} columns={[
              { key: "rank", label: "#", num: true },
              { key: "name", label: "player", render: (p) => <PlayerLink uid={p.uid} name={p.name} /> },
              { key: "tag", label: "alliance", render: (p) => <Tag id={p.allianceId} tag={p.tag} /> },
              { key: "server", label: "server", render: (p) => <span style={{ borderLeft: `3px solid ${colour[p.server]}`, paddingLeft: 6 }}><ServerLink id={p.server} /></span> },
              { key: "hero", label: "hero power", num: true, render: (p) => <Hero board={p.exact ? p.hero : null} est={p.exact ? null : p.hero} /> },
              { key: "power", label: "total power", num: true, render: (p) => big(p.power) },
              { key: "hq", label: "HQ", num: true },
              { key: "role", label: "likely job", sort: (p) => p.hq, render: (p) => <Role hq={p.hq} /> },
              { key: "active7", label: "active 7 d", render: (p) => (p.active7 == null ? <span className="muted">?</span> : p.active7 ? "yes" : <span className="muted">no</span>) },
              { key: "hours7", label: "online h 7 d", num: true },
              { key: "round1Days", label: "around 00 UTC", num: true, render: (p) => <span title="days in the last 30 with a sign of life between 23:00 and 01:59 UTC (round 1 is 00:10)">{p.round1Days}</span> },
              { key: "round2Days", label: "around 12 UTC", num: true, render: (p) => <span title="days in the last 30 with a sign of life between 11:00 and 13:59 UTC (round 2 is 12:30)">{p.round2Days}</span> },
              { key: "meteorR1", label: "past R1", num: true, render: (p) => <span title={`seen in ${p.meteorR1} of the last ${past.r1} Meteorite round 1s we have readings for`}>{past.r1 ? `${p.meteorR1}/${past.r1}` : "–"}</span> },
              { key: "meteorR2", label: "past R2", num: true, render: (p) => <span title={`seen in ${p.meteorR2} of the last ${past.r2} Meteorite round 2s we have readings for`}>{past.r2 ? `${p.meteorR2}/${past.r2}` : "–"}</span> },
              { key: "likely", label: "likely", sort: (p) => (p.likely1 ? 2 : 0) + (p.likely2 ? 1 : 0), render: (p) => <span className="small">{[p.likely1 && "R1", p.likely2 && "R2"].filter(Boolean).join(" + ") || <span className="muted">–</span>}</span> },
              { key: "lastSeen", label: "last seen", render: (p) => (p.lastSeen ? ago(p.lastSeen) : "") },
            ]} />
            <p className="small muted">Hero power is the in-game hero board where they are on it (top 200 per server), otherwise an estimate from their profile (marked est). &quot;Around&quot; counts days in the last 30 with any sign of life within an hour of the round. <b>Likely on</b> a round = seen during a past Meteorite round of that number, or around its time on {d.likelyDays}+ days (a quarter of the {d.days} days we have readings for). The filters pick from the top 200, so the list stays 50 long. Our readings are a floor - treat &quot;likely&quot; as a good guess, and &quot;not likely&quot; as unknown.</p>
          </div>
          <div className="card">
            <h3>Strongest alliances (by their top 20)</h3>
            <SortTable rows={d.alliances || []} initial={{ key: "top20", dir: -1 }} columns={[
              { key: "tag", label: "alliance", render: (a) => <Tag id={a.allianceId} tag={a.tag} /> },
              { key: "server", label: "server", render: (a) => <span style={{ borderLeft: `3px solid ${colour[a.server]}`, paddingLeft: 6 }}><ServerLink id={a.server} /></span> },
              { key: "top20", label: "top 20 hero", num: true, render: (a) => big(a.top20) },
              { key: "hero", label: "all members", num: true, render: (a) => big(a.hero) },
              { key: "topHero", label: "strongest", num: true, render: (a) => <span>{big(a.topHero)} <span className="muted small">{a.top}</span></span> },
              { key: "inTop50", label: "in top 50", num: true },
              { key: "members", label: "members", num: true },
              { key: "hq30", label: "HQ 30+", num: true },
              { key: "active7", label: "active 7 d", num: true },
            ]} />
          </div>
          <div className="card">
            <div className="row" style={{ justifyContent: "space-between", flexWrap: "wrap", gap: 8 }}>
              <h3 style={{ margin: 0 }}>When they play</h3>
              <div className="chips">
                <button className={`chip ${onlineOf === "top" ? "on" : ""}`} onClick={() => setOnlineOf("top")}>strongest players</button>
                <button className={`chip ${onlineOf === "all" ? "on" : ""}`} onClick={() => setOnlineOf("all")}>everyone</button>
              </div>
            </div>
            <HourLines rows={d.serverStats || []} colour={colour} field={onlineOf === "top" ? "topHourDays" : "hourDays"} days={d.days} />
            <OnlinePanel online={onlineOf === "top" ? d.onlineTop : d.online} noun="players" title={onlineOf === "top" ? "Top 50 across the crater, online by hour (UTC)" : "Everyone on these servers, online by hour (UTC)"} />
          </div>
        </>
      )}
      <HowItWorks />
    </>
  );
}

// One row per server; the best value in each strength column is bold.
function ServerTable({ rows, colour }) {
  const best = (k) => Math.max(...rows.map((r) => Number(r[k]) || 0));
  const B = ({ r, k, f = big }) => <span style={{ fontWeight: rows.length > 1 && Number(r[k]) === best(k) && r[k] ? 700 : 400 }}>{f(r[k])}</span>;
  return (
    <div className="card">
      <h3>Warzones side by side</h3>
      <SortTable rows={rows} initial={{ key: "top20", dir: -1 }} columns={[
        { key: "server", label: "server", render: (r) => <span style={{ borderLeft: `4px solid ${colour[r.server]}`, paddingLeft: 6 }}><ServerLink id={r.server} /> <Level v={r.level} /></span> },
        { key: "president", label: "president", render: (r) => (r.president ? <span>{r.presidentTag ? <span className="muted">[{r.presidentTag}] </span> : null}{r.president}</span> : <span className="muted">–</span>) },
        { key: "top10", label: "top 10 hero", num: true, render: (r) => <B r={r} k="top10" /> },
        { key: "top20", label: "top 20", num: true, render: (r) => <B r={r} k="top20" /> },
        { key: "top50", label: "top 50", num: true, render: (r) => <B r={r} k="top50" /> },
        { key: "top100", label: "top 100", num: true, render: (r) => <B r={r} k="top100" /> },
        { key: "hero1", label: "#1", num: true, render: (r) => <B r={r} k="hero1" /> },
        { key: "hero50", label: "50th", num: true, render: (r) => <B r={r} k="hero50" /> },
        { key: "hero100", label: "100th", num: true, render: (r) => <B r={r} k="hero100" /> },
        { key: "hq30Active", label: "HQ 30+ active", num: true, render: (r) => <span title={`${int(r.hq30)} HQ 30+ bases on the newest map, ${int(r.hq30Active)} active in 7 days - Stellar Core gatherers`}><B r={r} k="hq30Active" f={int} /></span> },
        { key: "hq22", label: "HQ 22-29", num: true, render: (r) => <span title="Crystal gatherers">{int(r.hq22)}</span> },
        { key: "hq21", label: "HQ ≤21", num: true, render: (r) => <span title="Debris gatherers">{int(r.hq21)}</span> },
        { key: "active7", label: "active 7 d", num: true, render: (r) => <B r={r} k="active7" f={int} /> },
        { key: "topOnlineRound1", label: "strong on R1", num: true, render: (r) => <span title={`Of its top 100: typically online in the 00:00 UTC hour (everyone: ${r.onlineRound1})`}>{r.topOnlineRound1}</span> },
        { key: "topOnlineRound2", label: "strong on R2", num: true, render: (r) => <span title={`Of its top 100: typically online in the 12:00 UTC hour (everyone: ${r.onlineRound2})`}>{r.topOnlineRound2}</span> },
        { key: "likely1", label: "likely R1", num: true, render: (r) => <span title={`Of the ${r.inTop200} players this server has in the crater's top 200: likely on for round 1`}><B r={r} k="likely1" f={int} /></span> },
        { key: "likely2", label: "likely R2", num: true, render: (r) => <span title={`Of the ${r.inTop200} players this server has in the crater's top 200: likely on for round 2`}><B r={r} k="likely2" f={int} /></span> },
        { key: "boardAt", label: "data", render: (r) => <span className="small muted" title={`hero board ${when(r.boardAt) || "never"} · map ${when(r.mappedAt) || "never"}`}>{r.boardAt ? `board ${ago(r.boardAt)}` : "no board yet"}</span> },
      ]} />
      <p className="small muted">Hero sums use the in-game hero board (exact, top 200) topped up with profile estimates. HQ counts come from the newest map scan. &quot;Strong on R1/R2&quot;: how many of the server&apos;s top 100 are typically online in the round&apos;s hour.</p>
    </div>
  );
}

// Typical online per UTC hour, one line per server, with the two round hours shaded.
function HourLines({ rows, colour, field, days }) {
  const W = 720, H = 170, L = 40, R = 10, T = 10, Bm = 22;
  const series = rows.map((r) => ({ s: r.server, v: (r[field] || []).map((x) => (days ? x / days : 0)) }));
  const max = Math.max(1, ...series.flatMap((x) => x.v));
  const sx = (h) => L + (h / 23) * (W - L - R);
  const sy = (v) => T + (1 - v / max) * (H - T - Bm);
  return (
    <div className="chart">
      <div className="legend">{series.map((x) => <span key={x.s}><i style={{ background: colour[x.s] }} />#{x.s}</span>)}</div>
      <svg viewBox={`0 0 ${W} ${H}`} width="100%" role="img" aria-label="typical players online per UTC hour, per server">
        {[0, 12].map((h) => <rect key={h} x={sx(h) - 6} y={T} width={(W - L - R) / 23 + 6} height={H - T - Bm} fill="var(--accent)" opacity="0.12" />)}
        {[0, 6, 12, 18, 23].map((h) => <text key={h} x={sx(h)} y={H - 6} textAnchor="middle" className="axis">{String(h).padStart(2, "0")}</text>)}
        {[0, max / 2, max].map((v) => <text key={v} x={L - 6} y={sy(v) + 4} textAnchor="end" className="axis">{Math.round(v)}</text>)}
        {series.map((x) => (
          <path key={x.s} d={x.v.map((v, h) => `${h ? "L" : "M"}${sx(h).toFixed(1)},${sy(v).toFixed(1)}`).join("")} fill="none" stroke={colour[x.s]} strokeWidth="2" />
        ))}
      </svg>
      <p className="small muted">Typical players with a sign of life per UTC hour over the last {days} days. Shaded: round 1 (00:10) and round 2 (12:30).</p>
    </div>
  );
}

function HowItWorks() {
  return (
    <div className="card">
      <h3>How it works</h3>
      <div className="cols" style={{ gap: 16 }}>
        <div className="small">
          <p><b>The week.</b> Tuesday to Friday is the preview: the game shows where the meteor lands (a warzone&apos;s Capitol) and which warzones - usually four - will fight for it. Not available while Warzone Duel runs.</p>
          <p><b>Saturday.</b> Two contest rounds, 00:10 and 12:30 server time (UTC), 30 minutes each. Rankings count only the round&apos;s own score; rewards use your best round. Then the display stage settles the leaderboards.</p>
          <p><b>Getting in.</b> Teleport into the crater for free. Leaving drops your event items. Inside, radar is down. A falling meteor blasts bases in its impact zone away (with insurance mail).</p>
        </div>
        <div className="small">
          <table className="small">
            <thead><tr><th>Source</th><th className="num">per second</th><th className="num">on finish</th><th className="num">at base</th><th>notes</th></tr></thead>
            <tbody>
              <tr><td>Contaminated Land</td><td className="num">40</td><td className="num">–</td><td className="num">–</td><td>just be in the crater</td></tr>
              <tr><td>Debris</td><td className="num">40</td><td className="num">300</td><td className="num">–</td><td>yellow zone, works with shield up</td></tr>
              <tr><td>Crystals</td><td className="num">80</td><td className="num">1,500</td><td className="num">30</td><td>black zone, shield off</td></tr>
              <tr><td>Stellar Cores</td><td className="num">160</td><td className="num">6,000</td><td className="num">120</td><td>near the Capitol, shield off</td></tr>
            </tbody>
          </table>
          <p>Breach a base and you take some of its Crystals and Cores (not its points). Nine chests from 15,000 to 600,000 points; individual and alliance ranking rewards. Common split: HQ 30+ on Cores, 22-29 on Crystals, 21 and under on Debris; guard whoever carries Cores.</p>
          <p className="muted">Sources: the game&apos;s own tables (yuntie_battle_*) and texts, <a href="https://lastwarhandbook.com/guides/meteorite-iron-war-guide" target="_blank" rel="noreferrer">Last War Handbook</a>, <a href="https://www.lastwartutorial.com/meteorite-iron-war/" target="_blank" rel="noreferrer">lastwartutorial.com</a>.</p>
        </div>
      </div>
    </div>
  );
}

// ---- Calendar: every stored event, and who was on during one.
const KIND_COLOUR = { meteorite: "#d2a8ff", rampage: "#ff7b72", svs: "#4ea1ff", war: "#56d4dd", battle: "#f0883e", defence: "#3fb950", attack: "#e3b341", custom: "#8b97a6" };
const fmtWhen = (iso) => { const t = new Date(iso); return `${t.toISOString().slice(0, 16).replace("T", " ")} UTC`; };
const span = (a, b) => { const m = Math.round((new Date(b) - new Date(a)) / 6e4); return m >= 2880 ? `${Math.round(m / 1440)} d` : m >= 120 ? `${Math.round(m / 60)} h` : `${m} min`; };

function Calendar({ admin, id }) {
  const [nonce, setNonce] = useState(0);
  const [kind, setKind] = useState("");
  const d = useJson(`/api/x/events/calendar?kind=${kind}&n=${nonce}`);
  if (id) return <CalendarEvent id={decodeURIComponent(id)} admin={admin} onGone={() => { window.location.hash = "#/events/calendar"; setNonce((n) => n + 1); }} />;
  if (!d) return <p className="muted">Loading…</p>;
  const now = Date.now();
  const events = d.events || [];
  const upcoming = events.filter((e) => new Date(e.end) >= now);
  const past = events.filter((e) => new Date(e.end) < now).reverse();
  const Table = ({ rows }) => (
    <SortTable rows={rows} initial={{ key: "start", dir: rows === past ? -1 : 1 }} limit={300} columns={[
      { key: "start", label: "when (UTC)", render: (e) => <a href={`#/events/calendar/${encodeURIComponent(e.id)}`}>{fmtWhen(e.start)}</a> },
      { key: "kind", label: "kind", render: (e) => <span className="small" style={{ color: KIND_COLOUR[e.kind] }}>{d.kinds[e.kind] || e.kind}</span> },
      { key: "title", label: "event", render: (e) => <a href={`#/events/calendar/${encodeURIComponent(e.id)}`}>{e.title}</a> },
      { key: "length", label: "length", sort: (e) => new Date(e.end) - new Date(e.start), render: (e) => span(e.start, e.end) },
      { key: "servers", label: "servers", sort: (e) => e.servers.length, render: (e) => <span className="small mono">{e.servers.length > 6 ? `${e.servers.length} servers` : e.servers.map((s) => `#${s}`).join(" ")}</span> },
      { key: "source", label: "from", render: (e) => <span className="small muted" title={e.detail}>{e.source}{e.detail.startsWith("expected") ? " · expected" : ""}</span> },
    ]} />
  );
  return (
    <>
      <div className="page-head">
        <div>
          <h1 className="page-title">Event calendar</h1>
          <p className="muted">Important events, kept for good so we can see who turned up. Meteorite rounds, Rampage Boss spawns and the game&apos;s war windows fill in by themselves every day; add season battles, defences and attacks by hand.</p>
        </div>
      </div>
      <div className="chips">
        <button className={`chip ${!kind ? "on" : ""}`} onClick={() => setKind("")}>all</button>
        {Object.entries(d.kinds).map(([k, label]) => <button key={k} className={`chip ${kind === k ? "on" : ""}`} onClick={() => setKind(k)}>{label}</button>)}
      </div>
      {admin && <AddEvent kinds={d.kinds} onAdded={() => setNonce((n) => n + 1)} />}
      <div className="card"><h3>Coming up</h3><Table rows={upcoming} /></div>
      <div className="card"><h3>Past</h3><Table rows={past} /></div>
    </>
  );
}

function AddEvent({ kinds, onAdded }) {
  const [f, setF] = useState({ kind: "battle", title: "", start: "", minutes: 60, servers: "1967", detail: "" });
  const [msg, setMsg] = useState("");
  const set = (k) => (e) => setF({ ...f, [k]: e.target.value });
  const add = async (e) => {
    e.preventDefault();
    const start = new Date(f.start + "Z");   // the box is UTC (server time)
    const body = { kind: f.kind, title: f.title, start: start.toISOString(), end: new Date(start.getTime() + f.minutes * 6e4).toISOString(), servers: f.servers, detail: f.detail };
    const r = await fetch("/api/events/calendar", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
    const j = await r.json().catch(() => ({}));
    setMsg(r.ok ? "Added." : j.error || `Could not add (${r.status}).`);
    if (r.ok) onAdded();
  };
  return (
    <form className="card row small" style={{ gap: 8, flexWrap: "wrap", alignItems: "end" }} onSubmit={add}>
      <b style={{ width: "100%" }}>Add an event</b>
      <label>kind <select value={f.kind} onChange={set("kind")}>{Object.entries(kinds).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></label>
      <label>title <input value={f.title} onChange={set("title")} placeholder="e.g. Capitol war vs 1970" /></label>
      <label>start (UTC) <input type="datetime-local" value={f.start} onChange={set("start")} required /></label>
      <label>minutes <input type="number" min="5" max="4320" value={f.minutes} onChange={set("minutes")} style={{ width: 80 }} /></label>
      <label>servers <input value={f.servers} onChange={set("servers")} style={{ width: 140 }} /></label>
      <label>note <input value={f.detail} onChange={set("detail")} /></label>
      <button className="primary">Add</button>
      {msg && <span>{msg}</span>}
    </form>
  );
}

function CalendarEvent({ id, admin, onGone }) {
  const d = useJson(`/api/x/events/calendar/${encodeURIComponent(id)}`);
  if (!d) return <p className="muted">Loading…</p>;
  if (!d.found) return <p className="muted">No such event. <a href="#/events/calendar">Back to the calendar</a></p>;
  const e = d.event;
  const del = async () => { if ((await fetch(`/api/events/calendar/${encodeURIComponent(id)}`, { method: "DELETE" })).ok) onGone(); };
  return (
    <>
      <p className="small"><a href="#/events/calendar">← Calendar</a></p>
      <div className="page-head">
        <div>
          <h1 className="page-title">{e.title}</h1>
          <p className="muted">{fmtWhen(e.start)} for {span(e.start, e.end)} · {e.servers.map((s) => <span key={s}><ServerLink id={s} /> </span>)}{e.detail && <> · {e.detail}</>}</p>
        </div>
        {admin && <button className="danger" onClick={del}>Delete event</button>}
      </div>
      {d.note && <p className="small muted">{d.note}</p>}
      {d.perServer && (
        <div className="card">
          <h3>Seen during it</h3>
          <table className="small"><thead><tr><th>server</th><th className="num">seen</th><th className="num">players we know</th></tr></thead>
            <tbody>{d.perServer.map((s) => <tr key={s.server}><td><ServerLink id={s.server} /></td><td className="num">{int(s.seen)}</td><td className="num">{int(s.known)}</td></tr>)}</tbody></table>
          <SortTable rows={d.seen || []} initial={{ key: "hero", dir: -1 }} empty="Nobody seen - or no readings for that time." columns={[
            { key: "name", label: "player", render: (p) => <PlayerLink uid={p.uid} name={p.name} /> },
            { key: "tag", label: "alliance", render: (p) => <Tag id={p.allianceId} tag={p.tag} /> },
            { key: "server", label: "server", render: (p) => <ServerLink id={p.server} /> },
            { key: "hero", label: "hero power", num: true, render: (p) => <Hero board={p.exact ? p.hero : null} est={p.exact ? null : p.hero} /> },
            { key: "power", label: "total power", num: true, render: (p) => big(p.power) },
          ]} />
        </div>
      )}
    </>
  );
}
