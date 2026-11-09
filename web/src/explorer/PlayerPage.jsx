import { LineChart, MultiLineChart, SortTable } from "./charts.jsx";
import { big, int, when, ago } from "./format.js";
import { Delta, Stat, Status, Tag, ServerLink, NameChanges } from "./bits.jsx";
import { OFFICIAL, Seen, later } from "./Intel.jsx";
import { useState } from "react";
import { Flag, PlayerOnline, countryName } from "./Online.jsx";
import { SendPosition } from "../panels/Account.jsx";
import { ActivityTab, DroneTab, useJson } from "./PlayerTabs.jsx";
import { useHashParam } from "../nav.js";

const TABS = [["overview", "Overview"], ["activity", "Activity report"], ["drone", "Drone"]];

// Admins only (a viewer gets 403 on /api/runners and sees nothing): one private chat message to
// this player from a service or privileged account (op chat.dm). A stopped account is logged in for
// this one send and out again (RunOnRunner). ponytail: plain text; map point shares (PostType 13) come later.
function SendMessage({ d }) {
  const runners = useJson("/api/runners");
  const [runner, setRunner] = useState("");
  const [body, setBody] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState(null);
  if (!Array.isArray(runners)) return null;
  const able = runners.filter((r) => r.tier === "service" || r.tier === "privileged");
  const pick = able.find((r) => r.id === runner) || able.find((r) => r.tier === "service") || able[0];
  if (!pick) return null;
  const send = async () => {
    const cold = pick.state !== "ready";
    if (!window.confirm(`Send this message from ${pick.name} to ${d.name || d.uid}?${cold ? `\n\n${pick.name} is not connected: it is logged in just for this send, which signs out anyone playing it on a phone right now.` : ""}`)) return;
    setBusy(true); setResult(null);
    try {
      const r = await fetch(`/api/runners/${encodeURIComponent(pick.id)}/operation`, {
        method: "POST", headers: { "content-type": "application/json" },
        body: JSON.stringify({ operation: "chat.dm", args: { uid: d.uid, text: body } }),
      });
      const j = await r.json().catch(() => ({}));
      setResult(r.ok ? { ok: /: ok$/.test(j.summary || ""), text: j.summary || "sent" } : { ok: false, text: j.error || `${r.status}` });
    } catch (e) { setResult({ ok: false, text: e.message }); }
    setBusy(false);
  };
  return (
    <div className="card">
      <h2>Direct message</h2>
      <div className="row small" style={{ gap: 8, flexWrap: "wrap" }}>
        <label>From <select value={pick.id} onChange={(e) => setRunner(e.target.value)}>
          {able.map((r) => <option key={r.id} value={r.id}>{r.name} ({r.tier}{r.state === "ready" ? ", connected" : ""})</option>)}
        </select></label>
        <span className="muted">to {d.name || d.uid} on #{d.server || "?"}</span>
      </div>
      <textarea style={{ width: "100%", marginTop: 6 }} rows={3} maxLength={500} placeholder="Message" value={body} onChange={(e) => setBody(e.target.value)} />
      <div className="row small" style={{ gap: 8, marginTop: 6 }}>
        <button className="primary" disabled={busy || !body.trim()} onClick={send}>{busy ? "Sending…" : "Send"}</button>
        {pick.state !== "ready" && <span className="warn-text">not connected - logs in for this one send</span>}
        {result && <span className={result.ok ? "" : "err"}>{result.text}</span>}
      </div>
    </div>
  );
}

// One line per event of a player's server story, in words.
function ServerStory({ rows }) {
  const S = ({ id }) => <ServerLink id={id} />;
  const line = (r) => {
    switch (r.kind) {
      case "created": return <>Created on <S id={r.to} /></>;
      case "early": return <>Moved <S id={r.from} /> → <S id={r.to} /> <span className="muted">before we were watching (~Nov 2025, estimated)</span></>;
      case "transfer": return <>Transferred <S id={r.from} /> → <S id={r.to} /></>;
      case "visit": return <>Visited <S id={r.to} /> <span className="muted">(home stayed <S id={r.from} />)</span></>;
      default: return r.kind;
    }
  };
  const span = (r) => r.kind === "created" ? "date unknown"
    : r.kind === "early" ? `first seen at new home ${when(r.toAt)}`
    : r.kind === "transfer" ? `between ${when(r.fromAt)} and ${when(r.toAt)}`
    : `${when(r.fromAt)} – ${when(r.toAt)}`;
  return (
    <table className="small">
      <tbody>
        {rows.map((r, i) => (
          <tr key={i}>
            <td>{line(r)}</td>
            <td className="muted mono">{span(r)}</td>
            <td className="muted">{r.kind === "transfer" || r.kind === "visit" ? r.evidence : ""}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

const BOARD = { 3: "kills", 4: "total power", 5: "HQ race", 13: "hero power", 14: "PvE stage", 15: "strongest hero", 18: "Tank Battlefield", 19: "Aircraft Battlefield", 20: "Missile Battlefield" };

// Runs of identical consecutive rows collapse into one row with first..last seen: the history is
// kept whole, but only a CHANGE earns a line on screen. Alliance ids are compared when both
// sides have one - some early rows carry only the tag - otherwise the tags are.
function collapse(rows, same) {
  const out = [];
  for (const r of rows) {
    const prev = out[out.length - 1];
    if (prev && same(prev, r)) { prev.last = r.at; prev.n++; prev.latest = r; }
    else out.push({ ...r, first: r.at, last: r.at, n: 1, latest: r });
  }
  return out;
}
const sameAlliance = (a, b) => (a.allianceId && b.allianceId ? a.allianceId === b.allianceId : (a.tag || "") === (b.tag || ""));
const Span = ({ r }) => (
  <>{when(r.first)}{r.n > 1 && <span className="muted"> – {when(r.last)} · {r.n}×</span>}</>
);

export default function PlayerPage({ d }) {
  const [tab, setTab] = useHashParam("tab", "overview");
  const seen = d.sightings.length > 0 || d.powerHistory.length > 0;
  if (!seen) return <p className="muted">No uid {d.uid} anywhere in the store.</p>;
  const latestHero = [...d.boards].reverse().find((b) => b.board === 13);
  // The estimate from the profile taken nearest the board, so the two are compared at the same moment.
  const nearBoard = latestHero && (d.profiles || []).filter((p) => p.heroEst)
    .sort((a, b) => Math.abs(new Date(a.at) - new Date(latestHero.at)) - Math.abs(new Date(b.at) - new Date(latestHero.at)))[0];
  const home = d.home ?? d.server;
  const migrated = d.originServer && home && d.originServer !== home;
  const visiting = d.home && d.based && d.based !== d.home;
  const story = d.serverHistory || [];

  const profiles = collapse(d.profiles || [], (a, b) => a.home === b.home && a.based === b.based && sameAlliance(a, b)).reverse();
  const sightings = collapse(d.sightings, (a, b) => a.server === b.server && sameAlliance(a, b) && a.name === b.name && a.point === b.point).reverse();
  // Leaderboards: one row per board, the newest place, and the move since the one before.
  const boards = Object.values(d.boards.reduce((m, b) => { (m[b.board] ||= []).push(b); return m; }, {}))
    .map((list) => ({ ...list[list.length - 1], prevRank: list.length > 1 ? list[list.length - 2].rank : null, times: list.length }));
  const map = d.mapPoint;

  return (
    <div className="grid">
      <div className="card hero-card">
        <div className="row" style={{ gap: 14, flexWrap: "wrap" }}>
          <h1 style={{ margin: 0, fontSize: 20 }}>{d.name || d.uid}</h1>
          {(d.country || d.flag) && (
            <span className="small" title={`profile country: ${countryName(d.country)}${d.flag ? ` · flag shown: ${d.flag}` : ""}`}>
              <Flag code={d.country} /> {countryName(d.country)}
              {d.flag && d.flag !== d.country && <span className="muted"> · shows <Flag code={d.flag} title={`chosen flag: ${d.flag}`} /></span>}
            </span>
          )}
          <Status value={d.status} />
          <span className="muted small mono">{d.uid}</span>
          {map && (
            <a className="small" href={`#/maps/${map.server}/${map.x},${map.y},${d.uid}`} title={`as of ${when(map.at)}`}>
              last on the map at {map.x}:{map.y} (#{map.server}, {when(map.at)}) →
            </a>
          )}
          {map && <SendPosition sid={map.server} x={map.x} y={map.y} player={d.uid} label={`${d.tag ? `[${d.tag}]` : ""}${d.name || d.uid}`} text="📍 Send base to me" />}
        </div>
        <div className="row small" style={{ gap: 18, marginTop: 6, flexWrap: "wrap" }}>
          {d.home ? (
            <span title="Home server from their profile - changes on a transfer, not on a teleport to fight">
              from <ServerLink id={d.home} /> <span className="muted">as of {when(d.homeAt)}</span>
            </span>
          ) : (
            <span title="No profile yet - this is where a map last saw their base">on <ServerLink id={d.server} /></span>
          )}
          {visiting && <span className="warn-text">base currently on <ServerLink id={d.based} /> (visiting)</span>}
          <span>alliance <Tag id={d.allianceId} tag={d.tag} />{d.roster && d.roster.allianceId === d.allianceId && <span className="muted"> R{d.roster.rank}{d.roster.official ? ` ${OFFICIAL[d.roster.official]}` : ""}{d.roster.joinedAt ? `, joined ${when(d.roster.joinedAt)}` : ""}</span>}</span>
          <span title="Newest login that came with a change in power, kills or HQ in the week before it, or a donation">last in game <Seen iso={later(d.lastSeen, d.roster?.donatedAt)} /></span>
          {d.lastUpdated && d.lastUpdated !== d.lastSeen && <span className="muted" title="When the game last changed this account at all - attacks on the base and moves count, so it is not proof they played">updated {ago(d.lastUpdated)}</span>}
          {d.roster?.donatedAt && <span className="muted">donated {ago(d.roster.donatedAt)} · {int(d.roster.donateToday)} today · {big(d.roster.donateWeek)} this week</span>}
          {d.monthCard && new Date(d.monthCard) > new Date() && <span className="muted" title="Monthly card active">paying (monthly card)</span>}
          {d.detail?.dronePower > 0 && <span title={`as of ${when(d.detail.at)}`}>drone lv {d.detail.droneLevel} · {big(d.detail.dronePower)} power · chip lv {d.detail.chipLevel}</span>}
          {d.detail?.careerLevel > 0 && <span className="muted">career lv {d.detail.careerLevel}</span>}
          {(d.presidencies || []).length > 0 && <span className="warn-text">president {d.presidencies.map((p) => `#${p.server} term ${p.round}`).join(", ")}</span>}
          <span>created on <ServerLink id={d.originServer} />{migrated && <span className="muted"> — has transferred</span>}</span>
          {(d.nameChanges || []).length > 0 && (
            <span className="muted">previously {[...new Set(d.nameChanges.map((c) => c.old))].reverse().join(", ")}</span>
          )}
        </div>
      </div>

      <nav className="subnav big">
        {TABS.map(([id, label]) => (
          <a key={id} href="#" className={tab === id ? "on" : ""} onClick={(e) => { e.preventDefault(); setTab(id); }}>{label}</a>
        ))}
      </nav>

      <div className="stats">
        <Stat label="Power" value={big(d.power)} sub={d.powerHistory.length ? `${d.powerHistory.length} snapshot(s)` : "no power data"} />
        <Stat label="Last change" value={<Delta value={d.change} />} sub="against the snapshot before" />
        <Stat label="Hero power (real)" value={latestHero ? big(latestHero.value) : "–"}
          sub={latestHero ? `#${latestHero.rank} on #${latestHero.server}'s hero board, ${when(latestHero.at)}` : "not on a top-200 hero board"} />
        <Stat label="Hero power (estimated)" value={d.heroEst ? `≈${big(d.heroEst)}` : "–"}
          sub={d.heroEst ? (nearBoard
            ? `at the board's time ≈${big(nearBoard.heroEst)}: ${nearBoard.heroEst >= latestHero.value ? "+" : ""}${(((nearBoard.heroEst - latestHero.value) / latestHero.value) * 100).toFixed(1)}% vs real · now as of ${when(d.profileAt)}`
            : `0.78 × (power − troops), usually within 1–2% · ${when(d.profileAt)}`) : "no profile yet"} />
        <Stat label="Troop power" value={d.troopPower != null ? big(d.troopPower) : "–"}
          sub={d.troopPower != null && d.power ? `${Math.round((d.troopPower / d.power) * 100)}% of their power · the rest is buildings, tech and heroes` : "no profile yet"} />
        <Stat label="Kills" value={big(d.powerHistory.at(-1)?.kills)} />
        <PlayerOnline o={d.online} />
        <Stat label="Chat messages" value={int(d.chatCount)} sub={d.chat[0] ? `last ${ago(d.chat[0].at)}` : "none captured"} />
        <Stat label="Transfers" value={story.filter((r) => r.kind === "transfer").length}
          sub={`${story.filter((r) => r.kind === "visit").length} visit(s)${d.applications.length ? ` · ${d.applications.length} application(s)` : ""}`} />
      </div>

      {tab === "activity" && <ActivityTab uid={d.uid} />}
      {tab === "drone" && <DroneTab uid={d.uid} />}

      {tab === "overview" && <div className="cards">
        <div className="card">
          <h2>Power over time</h2>
          <LineChart height={150} points={d.powerHistory.map((p) => ({ x: p.at, y: p.power }))} label="power per snapshot" />
        </div>
        <div className="card">
          <h2>Hero and troop power over time</h2>
          <MultiLineChart height={170} label="board = real hero power; ≈ = estimated from the profile"
            series={[
              { key: "board", label: "hero (board)", color: "var(--series-1)", points: d.boards.filter((b) => b.board === 13).map((b) => ({ x: b.at, y: b.value })) },
              { key: "est", label: "hero (≈ estimated)", color: "var(--series-3)", points: (d.profiles || []).filter((p) => p.heroEst).map((p) => ({ x: p.at, y: p.heroEst })) },
              { key: "troops", label: "troops", color: "var(--series-2)", points: (d.profiles || []).filter((p) => p.troops != null).map((p) => ({ x: p.at, y: p.troops })) },
            ].filter((s) => s.points.length > 0)} />
        </div>
        {story.length > 0 && (
          <div className="card">
            <h2>Server history</h2>
            <ServerStory rows={story} />
            <p className="muted small" style={{ marginBottom: 0 }}>Home only changes on a transfer; a visit is a base seen on another
              server while home stayed put. A transfer is bracketed by the last sighting at the old home and the first at the new.</p>
          </div>
        )}
        <div className="card">
          <h2>Leaderboards - newest place</h2>
          <SortTable rows={boards} initial={{ key: "rank", dir: 1 }} empty="Never on a saved board." columns={[
            { key: "board", label: "board", render: (b) => BOARD[b.board] || `type ${b.board}` },
            { key: "rank", label: "rank", num: true, render: (b) => <>#{b.rank}{b.prevRank && b.prevRank !== b.rank &&
              <span className={b.rank < b.prevRank ? "up" : "down"}> {b.rank < b.prevRank ? "▲" : "▼"}{Math.abs(b.prevRank - b.rank)}</span>}</> },
            { key: "value", label: "value", num: true, render: (b) => (b.value ? big(b.value) : "–") },
            { key: "server", label: "on", render: (b) => <ServerLink id={b.server} /> },
            { key: "at", label: "saved", render: (b) => <span className="muted">{when(b.at)}</span> },
          ]} />
        </div>
        {profiles.length > 0 && (
          <div className="card">
            <h2>Home, base and alliance - changes only</h2>
            <SortTable rows={profiles} initial={{ key: "first", dir: -1 }} columns={[
              { key: "first", label: "seen", render: (p) => <Span r={p} /> },
              { key: "home", label: "from", render: (p) => <ServerLink id={p.home} /> },
              { key: "based", label: "base on", render: (p) => (p.based && p.based !== p.home
                ? <span className="warn-text"><ServerLink id={p.based} /> visiting</span> : <ServerLink id={p.based} />) },
              { key: "tag", label: "alliance", render: (p) => <Tag id={p.allianceId} tag={p.tag} /> },
              { key: "power", label: "power", num: true, render: (p) => big(p.latest.power) },
            ]} />
          </div>
        )}
        {(d.nameChanges || []).length > 0 && (
          <div className="card">
            <h2>Name history</h2>
            <NameChanges rows={[...d.nameChanges].reverse()} />
          </div>
        )}
        <SendMessage d={d} />
        {(d.vipHistory || []).length > 0 && (
          <div className="card">
            <h2>VIP history - changes only</h2>
            <p className="small muted">
              VIP {d.svipLevel}{d.vipUntil ? `, VIP ${new Date(d.vipUntil) > new Date() ? "active until" : "lapsed"} ${when(d.vipUntil)}` : ""}.
              The game shows no VIP points for other players, so progress inside a level is unknown. A change happened between "last reading before" and "detected".
            </p>
            <table className="small">
              <thead><tr><th>detected</th><th>change</th><th>VIP active until</th><th>last reading before</th></tr></thead>
              <tbody>
                {[...d.vipHistory].reverse().map((v) => (
                  <tr key={v.at}>
                    <td className="mono">{when(v.at)}</td>
                    <td>{v.kind === "first" ? <span className="muted">first reading: VIP {v.level}</span>
                      : v.kind === "level" ? <>VIP {v.from} → <b>{v.level}</b></> : <span className="muted">VIP time added (VIP {v.level})</span>}</td>
                    <td className="mono muted">{v.until ? when(v.until) : "–"}</td>
                    <td className="mono muted">{v.prevAt ? when(v.prevAt) : "–"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <div className="card">
          <h2>Map sightings - changes only</h2>
          <SortTable rows={sightings} initial={{ key: "first", dir: -1 }} columns={[
            { key: "first", label: "mapped", render: (s) => <Span r={s} /> },
            { key: "server", label: "server", render: (s) => <ServerLink id={s.server} /> },
            { key: "point", label: "at", render: (s) => (s.point ? <a className="mono" href={`#/maps/${s.server}/${(s.point - 1) % 1000},${Math.floor((s.point - 1) / 1000)},${d.uid}`}>
              {(s.point - 1) % 1000}:{Math.floor((s.point - 1) / 1000)}</a> : "–") },
            { key: "tag", label: "alliance", render: (s) => <Tag id={s.allianceId} tag={s.tag} /> },
            { key: "hq", label: "HQ", num: true, render: (s) => s.latest.hq },
          ]} />
        </div>
        {d.applications.length > 0 && (
          <div className="card">
            <h2>Transfer applications</h2>
            <SortTable rows={d.applications} initial={{ key: "at", dir: -1 }} columns={[
              { key: "at", label: "captured", render: (a) => when(a.at) },
              { key: "fromServer", label: "from", render: (a) => <ServerLink id={a.fromServer} /> },
              { key: "toServer", label: "to", render: (a) => <ServerLink id={a.toServer} /> },
              { key: "power", label: "power", num: true, render: (a) => big(a.power) },
              { key: "heroPower", label: "hero", num: true, render: (a) => big(a.heroPower) },
              { key: "message", label: "message", render: (a) => <span className="small">{a.message}</span> },
            ]} />
          </div>
        )}
        {d.chat.length > 0 && (
          <div className="card">
            <h2>Recent chat</h2>
            <div className="chat">
              {d.chat.map((m, i) => (
                <div key={i} className="chat-line">
                  <span className="muted small mono">{when(m.at)}</span>
                  {m.tag && <span className="tag">{m.tag}</span>}
                  <span>{m.msg}</span>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>}
    </div>
  );
}
