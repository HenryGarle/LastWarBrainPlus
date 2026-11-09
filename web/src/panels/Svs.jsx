import { useMemo, useState } from "react";
import { big, int, when } from "../explorer/format.js";
import { ServerLink } from "../explorer/bits.jsx";
import { useJson } from "../explorer/PlayerTabs.jsx";

// Warzone Duel ("SvS"): 8 servers, two groups of 4. Three weekly rounds of 1 v 1 points races (Monday-Friday; Sunday's Arms Race and truck plunders count on Monday);
// the side with more points that week wins invasion rights and attacks the other's Capitol on Saturday.
// Pairings: round 1 = A1 v A2, A3 v A4, B1 v B2, B3 v B4;
// round 2 stays inside each group (the two winners meet, the two runners-up meet); round 3 crosses the groups: the two group winners meet in the
// final, the rest play for places (Henry, 2026-10-08: in setup 1, 1967 meets 1980 in week 2).
// Everything here works without an account: line-ups come from the game's own table (fetched daily), results
// from capitals taken (a president from the other server, Saturday). An admin read adds live points.

const SCORE_TYPES = {
  1: "Alliance Duel victory (30k/day)", 2: "Alliance Duel MVP (6k)", 3: "Apex Arena No.1", 4: "Desert Storm victory (50k)",
  5: "Wanted Boss - warzone damage", 6: "Wanted Boss 1st (individual)", 7: "Truck plunders (100 each)", 8: "Arms Race 1st",
  9: "Truck plunders", 10: "Arms Race 1st", 11: "Elite Storm Arena 1st", 12: "Desert Brawl top 2",
};
const DAY = 86400000;
const keep = (k, v) => { try { if (v === undefined) return localStorage.getItem(k); localStorage.setItem(k, v); } catch { /* private */ } return null; };
const day = (s) => (s || "").slice(0, 10);
// Which week of the event a capture fell in.
const roundOf = (open, at) => Math.floor((Date.parse(at) - Date.parse(open.replace(" ", "T") + "Z")) / (7 * DAY)) + 1;
// Which bracket each match belongs to: round 2 is inside each group, round 3 crosses the groups for places.
const ROUTE = {
  1: ["group A", "group A", "group B", "group B"],
  2: ["group A winners' route", "group B winners' route", "group A runner-up route", "group B runner-up route"],
  3: ["final - 1st / 2nd", "winners' route - 3rd / 4th", "runner-up route - 5th / 6th", "runner-up route - 7th / 8th"],
};
const pairKey = (round, a, b) => `${round}:${Math.min(a, b)}-${Math.max(a, b)}`;
const groupsOf = (lineup) => (lineup || "").split("|").map((g) => g.split(";").filter(Boolean).map(Number));

function GameTable({ admin, initial }) {   // the duels in the game's own event table, by server or range
  const [text, setText] = useState(() => initial || keep("svs.servers") || "1957-1988");
  const [servers, setServers] = useState(text);
  const [cfg, setCfg] = useState(null);
  const [nonce, setNonce] = useState(0);
  const d = useJson(servers || cfg ? `/api/svs?servers=${encodeURIComponent(servers)}${cfg ? `&cfg=${cfg}` : ""}&n=${nonce}` : null);
  const go = () => { keep("svs.servers", text); setCfg(null); setServers(text); };

  return (
    <div className="grid">
      <div className="card">
        <p className="small muted">
          Warzone Duel: 8 servers in two groups of 4, three weekly rounds. Monday-Friday each pair races for points (Alliance Duel wins 30k a day,
          Desert Storm 50k, Wanted Boss, Arms Race, truck plunders 100 each...); the side with more points wins <b>invasion rights</b> and attacks the
          other&apos;s Capitol on Saturday. Line-ups come from the game&apos;s own event table (refreshed daily); a capital taken shows who won.
          Enter a server, a range (1957-1988) or 8 servers (group A then B).
        </p>
        <div className="row" style={{ gap: 8, flexWrap: "wrap", alignItems: "center" }}>
          <input value={text} onChange={(e) => setText(e.target.value)} onKeyDown={(e) => e.key === "Enter" && go()} placeholder="1959 or 1957-1988" style={{ width: 300 }} />
          <button className="action" onClick={go}>Show</button>
        </div>
      </div>
      {d?.events?.length > 0 && <Running events={d.events} current={d.event?.id} pick={setCfg} />}
      {d && !d.error && d.servers?.length > 0 && <Duel d={d} />}
      {d?.events?.length > 0 && <History events={d.events} current={d.event?.id} pick={setCfg} />}
    </div>
  );
}

// The duels running now among these servers, one button each - the page opens one, these switch between them.
function Running({ events, current, pick }) {
  const now = new Date().toISOString().replace("T", " ").slice(0, 19);
  const live = events.filter((e) => e.openTime <= now && e.endTime > now);
  if (!live.length) return null;
  // Servers a live read placed in another duel cannot be in a "?" one: struck through.
  const placed = new Set(live.filter((e) => e.read).flatMap((e) => groupsOf(e.serverIds).flat()));
  const show = (e, g) => (g || []).map((x, i) => <span key={x}>{i ? " " : ""}{!e.read && placed.has(x) ? <s>{x}</s> : x}</span>);
  return (
    <div className="card">
      <h3>Running now ({live.length})</h3>
      <div className="row" style={{ gap: 8, flexWrap: "wrap" }}>
        {live.map((e) => {
          const [a, b] = groupsOf(e.serverIds);
          return (
            <button key={e.id} className={e.id === current ? "action" : ""} onClick={() => pick(e.id)} title={e.redrawn ? "Groups from the game's table - this pool was re-drawn, so they may be wrong" : ""}>
              {e.id}{e.read ? " ✓" : e.redrawn ? " ?" : ""}: {show(e, a)} | {show(e, b)}
            </button>
          );
        })}
      </div>
      <p className="small muted">✓ groups as the game showed them · ? groups from the game&apos;s table, but the game re-drew this pool when it started, so they may be wrong until a server in it is read. Struck-through servers were read in another duel.</p>
    </div>
  );
}

// Every Warzone Duel these servers were in, with the capitals taken.
function History({ events, current, pick }) {
  return (
    <div className="card">
      <h3>Warzone Duels ({events.length})</h3>
      <p className="small muted">From the game&apos;s event table; the database keeps every one it has seen. Click one to open it. ✓ = line-up as the game showed it (live read); ? = the game re-drew this pool, so the table&apos;s groups may be wrong.
        Capitals: a president from the other server, so the attacker won invasion rights and took the Capitol - a defended Capitol leaves no trace.</p>
      <div className="scroll">
        <table className="small">
          <thead><tr><th>event</th><th>from</th><th>to</th><th>group A</th><th>group B</th><th>capitals taken</th><th className="num">live reads</th></tr></thead>
          <tbody>{events.map((e) => {
            const [a, b] = groupsOf(e.serverIds);
            return (
              <tr key={e.id} onClick={() => pick(e.id)} style={{ cursor: "pointer", fontWeight: e.id === current ? 700 : 400 }}>
                <td>{e.id}{e.read ? " ✓" : ""}{e.redrawn ? " ?" : ""}</td><td>{day(e.openTime)}</td><td>{day(e.endTime)}</td>
                <td className="mono">{a?.join(" ")}</td><td className="mono">{b?.join(" ")}</td>
                <td>{(e.captures || []).map((c, i) => <span key={i} style={{ marginRight: 8 }}>R{roundOf(e.openTime, c.at)} #{c.by}→#{c.server}</span>)}</td>
                <td className="num">{e.reads || ""}</td>
              </tr>
            );
          })}</tbody>
        </table>
      </div>
    </div>
  );
}

function Duel({ d, manual = {}, onPick, start }) {
  const snap = d.snapshot;
  const ev = d.event;
  const groups = useMemo(() => groupsOf(d.lineup), [d.lineup]);
  const strength = useMemo(() => scoreStrength(d.strength, d.servers), [d.strength, d.servers]);
  const rounds = snap?.rounds?.allRoundInfo || [];
  const live = snap?.schedule?.curVsRound || [];
  const curRound = snap?.schedule?.curRound || (ev ? Math.min(3, Math.max(0, roundOf(ev.openTime, new Date().toISOString()))) : 0);
  const captures = useMemo(() => (ev?.captures || []).map((c) => ({ ...c, round: roundOf(ev.openTime, c.at) })), [ev]);
  const bracket = useMemo(() => predict(groups, rounds, live, curRound, captures, strength, manual), [groups, rounds, live, curRound, captures, strength, manual]);
  const src = { game: "as the game showed an account in the event", table: "from the game's event table", typed: "as typed", entered: "as you entered it" }[d.source];

  return (
    <>
      <div className="card">
        <h3>{ev ? <>Event {ev.id} · {day(ev.openTime)} → {day(ev.endTime)}</> : "The servers"}</h3>
        <p className="small muted">Line-up {src}{snap && <> (read {when(snap.takenAt)} via #{snap.seenFrom})</>}. {curRound > 0 && curRound <= 3 && ev && day(ev.endTime) > new Date().toISOString().slice(0, 10) ? `Round ${curRound} of 3 now.` : ""}
          {d.source === "table" && (ev?.redrawn ? <b> The game re-drew this pool of servers when the event started (a live read of another event in it showed different groups), so these groups are probably wrong - a read from a server in it fixes them.</b> : " The game can re-draw the groups when the event starts - a live read shows the final line-up.")}</p>
        <div className="scroll">
          <table className="small">
            <thead><tr><th>group</th><th>server</th><th className="num">players</th><th className="num">active (3 d)</th><th className="num">top 20 hero power</th><th className="num">top 100 hero power</th><th className="num">strength</th><th className="num">capitals taken</th><th className="num">lost</th></tr></thead>
            <tbody>{groups.flatMap((g, gi) => g.map((s) => {
              const st = strength[s] || {};
              return (
                <tr key={s}><td>{gi ? "B" : "A"}</td><td><ServerLink id={s} /></td><td className="num">{int(st.players || 0)}</td><td className="num">{int(st.active || 0)}</td>
                  <td className="num">{big(st.top20Hero || 0)}</td><td className="num">{big(st.top100Hero || 0)}</td>
                  <td className="num"><b>{st.score ? Math.round(st.score * 100) : "–"}</b></td>
                  <td className="num">{captures.filter((c) => c.by === s).length || ""}</td>
                  <td className="num">{captures.filter((c) => c.server === s).length || ""}</td></tr>
              );
            }))}</tbody>
          </table>
        </div>
        <p className="small muted">Strength (0-100) is our read today: 50% top-100 hero power, 30% players active in the last 3 days, 20% top-20 hero power,
          each against the strongest of the 8. Servers we do not track show 0 - set them to basic or full on the Tracking page.</p>
      </div>
      <div className="card">
        <h3>Bracket</h3>
        <BracketView bracket={bracket} />
      </div>
      {start && <Forecast start={start} bracket={bracket} manual={manual} />}
      <div className="card">
        <h3>Every match</h3>
        <p className="small muted">{d.source === "entered" ? "Results ticked in here; pairs not played yet are predicted from strength." : "Results the game showed us, then capitals taken, then the live round's points, then a prediction from strength."}
          The winner of each pair invades the loser on Saturday.</p>
        {bracket.map((r) => (
          <div key={r.round} style={{ marginBottom: 10 }}>
            <b>Round {r.round}</b> <span className="muted small">{r.kind}</span>
            <table className="small"><tbody>{r.pairs.map((p, i) => (
              <tr key={i} style={ROUTE[r.round][i].startsWith("runner") ? { opacity: 0.75 } : undefined}>
                <td className="small" style={{ whiteSpace: "nowrap" }}>{ROUTE[r.round][i] && <span className={ROUTE[r.round][i].startsWith("runner") ? "muted" : ""}>{ROUTE[r.round][i]}</span>}</td>
                <td style={{ fontWeight: p.winner === p.a ? 700 : 400 }}><Side p={p} x={p.a} r={r.round} onPick={onPick} /></td>
                <td className="num">{p.scoreA != null ? big(p.scoreA) : ""}</td>
                <td className="muted">v</td>
                <td className="num">{p.scoreB != null ? big(p.scoreB) : ""}</td>
                <td style={{ fontWeight: p.winner === p.b ? 700 : 400 }}><Side p={p} x={p.b} r={r.round} onPick={onPick} /></td>
                <td className="small">#{p.winner} {{ settled: "won", entered: "won - invades", captured: "took the Capitol of", live: "leading - would invade", predicted: "likely invades" }[p.source]}{p.source === "settled" ? ` - invaded #${p.winner === p.a ? p.b : p.a}` : ` #${p.winner === p.a ? p.b : p.a}`}</td>
                <td className="small muted">{p.note || ""}</td>
              </tr>
            ))}</tbody></table>
          </div>
        ))}
      </div>
      {snap?.detail?.vsScoreCompare && <Live detail={snap.detail} live={live} />}
    </>
  );
}

function Live({ detail, live }) {
  const ids = [...new Set(live.map((r) => r.serverId))];
  return (
    <div className="card">
      <h3>Live round: where the points come from</h3>
      <table className="small">
        <thead><tr><th>source</th>{ids.map((s) => <th key={s} className="num">#{s}</th>)}</tr></thead>
        <tbody>
          {detail.vsScoreCompare.map((t) => (
            <tr key={t.type}><td>{SCORE_TYPES[t.type] || `type ${t.type}`}</td>{ids.map((s) => <td key={s} className="num">{big(t.vs.find((v) => v.serverId === s)?.score || 0)}</td>)}</tr>
          ))}
          <tr><td><b>total</b></td>{ids.map((s) => <td key={s} className="num"><b>{big(live.find((r) => r.serverId === s)?.score || 0)}</b></td>)}</tr>
        </tbody>
      </table>
      {detail.vsScoreMVP && (
        <p className="small muted">MVPs: {Object.values(detail.vsScoreMVP.user || {}).map((u) => `#${u.serverId} ${u.name} [${u.abbr}] ${big(u.score)}`).join(" · ")}
          {" · "}alliances: {Object.values(detail.vsScoreMVP.alliance || {}).map((a) => `#${a.server} [${a.abbr}] ${big(a.score)}`).join(" · ")}</p>
      )}
    </div>
  );
}

function scoreStrength(rows, servers) {
  const by = Object.fromEntries((rows || []).map((r) => [r.server, r]));
  const max = (k) => Math.max(1, ...servers.map((s) => Number(by[s]?.[k] || 0)));
  const m = { h: max("top100Hero"), a: max("active"), t: max("top20Hero") };
  return Object.fromEntries(servers.map((s) => {
    const r = Object.fromEntries(Object.entries(by[s] || {}).map(([k, v]) => [k, Number(v) || 0]));   // bigint sums arrive as strings
    return [s, { ...r, score: 0.5 * (r.top100Hero || 0) / m.h + 0.3 * (r.active || 0) / m.a + 0.2 * (r.top20Hero || 0) / m.t }];
  }));
}

// The bracket: results the game showed, then capitals taken, then the live round by points so far, the rest by strength.
function predict(groups, rounds, live, curRound, captures, strength, manual = {}) {
  const [A, B] = groups;
  if (A?.length !== 4 || B?.length !== 4) return [];
  const s = (x) => strength[x]?.score || 0;
  const play = (round, a, b) => {
    const ra = rounds.find((r) => r.round === round && r.serverId === a && r.vsServerId === b);
    const rb = rounds.find((r) => r.round === round && r.serverId === b && r.vsServerId === a);
    const won = manual[pairKey(round, a, b)];
    if (won === a || won === b) return { a, b, winner: won, source: "entered" };
    if (ra && ra.scoreSettled) return { a, b, scoreA: ra.score, scoreB: rb?.score, winner: ra.win === 1 ? a : b, source: "settled" };
    const cap = captures.find((c) => c.round === round && ((c.by === a && c.server === b) || (c.by === b && c.server === a)));
    if (cap) return { a, b, winner: cap.by, source: "captured", note: `${day(cap.at)} - president ${cap.tag ? `[${cap.tag}]` : ""}${cap.name}` };
    const la = round === curRound && live.find((r) => r.serverId === a);
    const lb = round === curRound && live.find((r) => r.serverId === b);
    if (la && lb) return { a, b, scoreA: la.score, scoreB: lb.score, winner: la.score >= lb.score ? a : b, source: "live" };
    return { a, b, winner: s(a) >= s(b) ? a : b, source: "predicted", note: `strength ${Math.round(s(a) * 100)} v ${Math.round(s(b) * 100)}` };
  };
  const lose = (p) => (p.winner === p.a ? p.b : p.a);
  const r1 = [play(1, A[0], A[1]), play(1, A[2], A[3]), play(1, B[0], B[1]), play(1, B[2], B[3])];
  // r2 = [group A winners, group B winners, group A runners-up, group B runners-up]
  const r2 = [play(2, r1[0].winner, r1[1].winner), play(2, r1[2].winner, r1[3].winner), play(2, lose(r1[0]), lose(r1[1])), play(2, lose(r1[2]), lose(r1[3]))];
  const r3 = [play(3, r2[0].winner, r2[1].winner), play(3, lose(r2[0]), lose(r2[1])), play(3, r2[2].winner, r2[3].winner), play(3, lose(r2[2]), lose(r2[3]))];
  return [
    { round: 1, kind: "within each group", pairs: r1 },
    { round: 2, kind: "within each group: the two winners meet, the two runners-up meet", pairs: r2 },
    { round: 3, kind: "across the groups: the group winners meet in the final, the rest play for places", pairs: r3 },
  ];
}

// A server in a bracket pair; with onPick (admin, a set-up duel) a tick records it as that week's winner.
function Side({ p, x, r, onPick }) {
  const won = p.source === "entered" && p.winner === x;
  return (
    <>
      <ServerLink id={x} />
      {onPick && (
        <button className={won ? "action" : ""} style={{ marginLeft: 6, padding: "0 6px" }} title={won ? "Clear this result" : `#${x} won this week`}
          onClick={() => onPick(r, p, x)}>{won ? "✓ won" : "won?"}</button>
      )}
    </>
  );
}

// SvS set up by hand (Henry, 2026-10-08): the 8 servers as the four round-1 pairs; rounds 2 and 3 follow from who wins,
// and each week's result is ticked in as it happens. Reading events from the game is parked until guests can.
export default function Svs({ admin, initial }) {
  const [nonce, setNonce] = useState(0);
  const setups = useJson(`/api/svs/setups?n=${nonce}`);
  const [pick, setPick] = useState(null);
  const [editing, setEditing] = useState(false);
  const list = Array.isArray(setups) ? setups : [];
  const cur = pick === "new" ? null : list.find((x) => x.id === pick) || list[0] || null;
  const reload = (id) => { if (id) setPick(id); setEditing(false); setNonce((n) => n + 1); };

  return (
    <div className="grid">
      <div className="card">
        <h3>Warzone Duel (SvS)</h3>
        <p className="small muted">8 servers, two groups of 4, three weekly rounds. Monday to Friday each pair races for points (Monday also counts Sunday's Arms Race and truck plunders; each day's Alliance Duel and MVP are scored the next day); at the end of Friday the side with more points wins
          <b> invasion rights</b> and attacks the other&apos;s Capitol on Saturday. Round 1 is inside each group (A1 v A2, A3 v A4, B1 v B2, B3 v B4);
          round 2 stays inside each group (the two winners meet, the two runners-up meet); round 3 crosses the groups: the group winners meet in the final, the rest play for places. Enter the round-1 pairs and the
          rest of the bracket maps itself out; tick who won each week and the next round follows. Unplayed pairs are predicted from our strength read.</p>
        <div className="row" style={{ gap: 8, flexWrap: "wrap" }}>
          {list.map((x) => (
            <button key={x.id} className={cur?.id === x.id ? "action" : ""} onClick={() => { setPick(x.id); setEditing(false); }}>
              {x.name || "SvS"}{x.start ? ` · ${x.start}` : ""}
            </button>
          ))}
          {admin && <button onClick={() => { setPick("new"); setEditing(true); }}>+ New SvS</button>}
          {admin && cur && !editing && <button onClick={() => setEditing(true)}>Edit servers</button>}
        </div>
        {!list.length && pick !== "new" && <p className="muted small">No SvS set up yet.{admin ? " Use + New SvS." : ""}</p>}
      </div>
      {admin && editing && <Editor key={pick === "new" ? "new" : cur?.id} setup={pick === "new" ? null : cur} onSaved={reload} onCancel={() => setEditing(false)} />}
      {cur && !editing && pick !== "new" && <Setup s={cur} admin={admin} onSaved={() => reload()} />}
      <Observations admin={admin} start={cur?.start} />
      <details open={!!initial}>
        <summary className="small muted">From the game&apos;s own event table (line-ups the game planned; it can re-draw them, so treat with care)</summary>
        <GameTable admin={admin} initial={initial} />
      </details>
    </div>
  );
}

async function saveSetup(body) {
  const r = await fetch("/api/svs/setups", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(j.error || `${r.status}`);
  return j.id;
}

function Editor({ setup, onSaved, onCancel }) {
  const start = setup ? groupsOf(setup.lineup).flat() : [];
  const [name, setName] = useState(setup?.name || "");
  const [date, setDate] = useState(setup?.start || "");
  const [ids, setIds] = useState(() => Array.from({ length: 8 }, (_, i) => (start[i] ? String(start[i]) : "")));
  const [msg, setMsg] = useState("");
  const set = (i, v) => setIds((old) => old.map((o, j) => (j === i ? v.replace(/\D/g, "") : o)));
  const box = (i) => <input value={ids[i]} onChange={(e) => set(i, e.target.value)} placeholder={`${i < 4 ? "A" : "B"}${(i % 4) + 1}`} style={{ width: 70 }} inputMode="numeric" />;
  const save = async () => {
    try {
      const lineup = ids.slice(0, 4).join(";") + "|" + ids.slice(4).join(";");
      // Changing servers keeps only results that still name two of them - the bracket recomputes the rest.
      const id = await saveSetup({ id: setup?.id, name, start: date, lineup, results: setup ? JSON.parse(setup.results || "{}") : {} });
      onSaved(id);
    } catch (e) { setMsg(e.message); }
  };
  const del = async () => {
    if (!setup || !window.confirm(`Delete "${setup.name || "this SvS"}" and its results?`)) return;
    await fetch(`/api/svs/setups/${setup.id}`, { method: "DELETE" });
    onSaved(null);
  };
  return (
    <div className="card">
      <h3>{setup ? "Edit SvS" : "New SvS"}</h3>
      <div className="row" style={{ gap: 8, flexWrap: "wrap", alignItems: "center" }}>
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Name, e.g. October SvS" style={{ width: 220 }} />
        <label className="small muted">first day <input type="date" value={date} onChange={(e) => setDate(e.target.value)} /></label>
      </div>
      <table className="small" style={{ marginTop: 8 }}><tbody>
        <tr><td><b>Group A</b></td><td>{box(0)} v {box(1)}</td><td>{box(2)} v {box(3)}</td></tr>
        <tr><td><b>Group B</b></td><td>{box(4)} v {box(5)}</td><td>{box(6)} v {box(7)}</td></tr>
      </tbody></table>
      <p className="small muted">Each box pair is one round-1 match-up.</p>
      <div className="row" style={{ gap: 8 }}>
        <button className="action" onClick={save}>Save</button>
        <button onClick={onCancel}>Cancel</button>
        {setup && <button onClick={del}>Delete</button>}
        <span className="small muted">{msg}</span>
      </div>
    </div>
  );
}

function Setup({ s, admin, onSaved }) {
  const servers = groupsOf(s.lineup).flat();
  const f = useJson(`/api/svs?servers=${servers.join(",")}`);
  const manual = useMemo(() => { try { return JSON.parse(s.results || "{}"); } catch { return {}; } }, [s.results]);
  const onPick = admin ? async (round, p, x) => {
    const next = { ...manual };
    const k = pairKey(round, p.a, p.b);
    if (next[k] === x) delete next[k]; else next[k] = x;
    await saveSetup({ id: s.id, name: s.name, start: s.start, lineup: s.lineup, results: next });
    onSaved();
  } : null;
  // Only our strength read comes from the server; the line-up and results are the ones entered here.
  const d = { servers, lineup: s.lineup, source: "entered", strength: f?.strength || [], event: null, snapshot: null };
  return <Duel d={d} manual={manual} onPick={onPick} start={s.start} />;
}

// The game's own picture of the duel (Match Info): group A on the left, A1 v A2 at the top and A3 v A4 at the bottom,
// group B on the right. Week 2 stays in each column: the top winner meets the bottom winner (left column = group A,
// right column = group B); the two column winners meet in the middle for 1st place in week 3.
// Gold = won (ticked in or seen); dashed = our likely winner. The runner-up matches are in the table below.
function BracketView({ bracket }) {
  if (bracket.length < 3) return null;
  const [r1, r2, r3] = bracket.map((r) => r.pairs);
  const done = (p) => p.source !== "predicted" && p.source !== "live";
  const Box = ({ x, p, big: large }) => {
    const won = p && p.winner === x;
    const sure = p && done(p);
    return (
      <div style={{
        minWidth: large ? 92 : 74, padding: "6px 8px", textAlign: "center", borderRadius: 6, fontWeight: won ? 700 : 400,
        border: `2px ${won && !sure ? "dashed" : "solid"} ${won ? "#e3b341" : "var(--line, #444)"}`,
        background: won && sure ? "rgba(227,179,65,0.18)" : "transparent",
      }} title={p ? `${won ? (sure ? "won" : "likely to win") : (sure ? "lost" : "likely to lose")} ${p.note || ""}` : ""}>
        {won ? (sure ? "👑 " : "") : ""}{x ? <ServerLink id={x} /> : "?"}
      </div>
    );
  };
  const row = { display: "flex", justifyContent: "space-between", alignItems: "center", gap: 8, margin: "10px 0" };
  const pair = (p) => <div style={{ display: "flex", gap: 6 }}><Box x={p.a} p={p} /><Box x={p.b} p={p} /></div>;
  const wk = (t) => <span className="small muted">{t}</span>;
  const fin = r3[0];
  return (
    <div style={{ maxWidth: 560 }}>
      <div style={row}>{wk("group A")}{wk("group B")}</div>
      <div style={row}>{pair(r1[0])}{wk("week 1")}{pair(r1[2])}</div>
      <div style={row}><Box x={r2[0].a} p={r2[0]} />{wk("week 2 - each column's winners meet")}<Box x={r2[1].a} p={r2[1]} /></div>
      <div style={{ ...row, justifyContent: "center" }}>
        <Box x={fin.a} p={fin} big />
        <b style={{ color: "#e3b341" }}>VS</b>
        <Box x={fin.b} p={fin} big />
        {wk(`week 3 final · ${done(fin) ? "1st" : "likely 1st"}: #${fin.winner}`)}
      </div>
      <div style={row}><Box x={r2[0].b} p={r2[0]} />{wk("vs the one above")}<Box x={r2[1].b} p={r2[1]} /></div>
      <div style={row}>{pair(r1[1])}{wk("week 1")}{pair(r1[3])}</div>
      <p className="small muted">Gold = won (👑 once it has happened, dashed = our prediction). Week 2: in each group the top winner meets the
        bottom winner; the two group winners meet in the middle in week 3. Runner-up matches (the week-1 and week-2 losers) are in the table below.</p>
    </div>
  );
}

// "720K", "1.02M", "485.2k", "3,479,200" -> a number.
const parsePts = (t) => {
  const m = String(t || "").trim().replace(/,/g, "").match(/^(-?[\d.]+)\s*([kKmM]?)/);
  if (!m) return 0;
  return Math.round(Number(m[1]) * (m[2].toLowerCase() === "m" ? 1e6 : m[2].toLowerCase() === "k" ? 1e3 : 1));
};
const WEEKDAY = ["Mon", "Tue", "Wed", "Thu", "Fri"];
const METRICS = ["Alliance Duel victory", "Alliance Duel MVP", "Wanted Boss warzone damage", "Wanted Boss 1st place", "Truck plunders", "Arms Race 1st", "Desert Storm victory (Friday)"];
// Older logs and predictions have six metrics (no Desert Storm): pad to seven.
const pad7 = (x) => [...(x || []), 0, 0, 0, 0, 0, 0, 0].slice(0, METRICS.length);
const sum = (xs) => (xs || []).reduce((a, b) => a + Number(b || 0), 0);

// This week's pairs with the model's points by metric: pick a race day to see what we expect by the end of it (and what was
// logged that day), always beside the end-of-race estimate. Every day's estimate is kept, so once a result is ticked in we
// can see how close each one was.
function Forecast({ start, bracket, manual = {} }) {
  const week = Math.min(3, Math.max(1, Math.floor((Date.now() - Date.parse(start + "T00:00:00Z")) / (7 * DAY)) + 1));
  const weekStart = new Date(Date.parse(start + "T00:00:00Z") + (week - 1) * 7 * DAY).toISOString().slice(0, 10);
  const today = Math.min(5, Math.max(1, Math.ceil((Date.now() - Date.parse(weekStart + "T00:00:00Z")) / DAY)));
  const [day, setDay] = useState(today);
  const pairs = bracket[week - 1]?.pairs || [];
  return (
    <div className="card">
      <h3>Week {week} forecast by metric</h3>
      <p className="small muted">Points: Alliance Duel win 30K and MVP 6K (scored the next day - Monday's on Tuesday ... Thursday's on Friday), Wanted Boss
        warzone damage 250K and 1st place 150K for each day won, truck plunder 100 each (Sunday on), Arms Race 1st 2,000 each (scored the next day -
        Sunday's on Monday), Desert Storm win 50K (Friday only, up to two per alliance). Race: Monday to the end of Friday (week started {weekStart}).
        Days are counted by when things happen: Monday includes Monday's duel, MVP and bosses, plus Sunday's Arms Race and trucks
        (Sunday's trucks at half - often a lax day); Friday's duel, MVP and Arms Race would be scored after the decision, so Friday adds bosses,
        trucks and Desert Storm. Model {"v1.5"} (fitted on the logged Weekly Results, never corrected by them): duel and MVP match-ups = alliances with 10+ active members (mean of the two servers); boss damage days to the bigger top-100 hero power; boss 1st place as a chance between each side's 5 strongest active players; trucks shared by active hero power to the power 1.3, Arms Race to the power 0.6;
        Alliance Duel, MVP and Desert Storm split evenly - who wins duels does not follow strength. ✓ = the side we expect to come out ahead.</p>
      <div className="row small" style={{ gap: 6, alignItems: "center", flexWrap: "wrap" }}>
        <span className="muted">points earned by the end of</span>
        {[1, 2, 3, 4, 5].map((n) => (
          <button key={n} className={n === day ? "action" : ""} onClick={() => setDay(n)}>{WEEKDAY[n - 1]}{n === today ? " (today)" : ""}</button>
        ))}
      </div>
      {pairs.map((p) => (
        <PairForecast key={`${p.a}-${p.b}`} a={p.a} b={p.b} start={weekStart} day={day} won={manual[pairKey(week, p.a, p.b)]} />
      ))}
    </div>
  );
}

function PairForecast({ a, b, start, day, won }) {
  const f = useJson(`/api/svs/predict?a=${a}&b=${b}&start=${start}`);
  if (!f || f.error || !f.curveA) return null;
  const [da, db] = [f.curveA[day - 1], f.curveB[day - 1]];
  // What was logged during that day (the newest of the day), if anything.
  const t0 = Date.parse(start + "T00:00:00Z");
  const seen0 = (f.seen || []).filter((o) => { const d = (Date.parse(o.at) - t0) / DAY; return d > day - 1 && d <= day; }).slice(-1)[0];
  const seen = seen0 && { ...seen0, scoresA: pad7(seen0.scoresA), scoresB: pad7(seen0.scoresB) };
  const tick = (x, y) => (x > y ? " ✓" : "");
  const endA = sum(f.endA), endB = sum(f.endB);
  return (
    <div style={{ margin: "12px 0 18px" }}>
      <b>#{a} v #{b}</b> <span className="small muted">now day {Math.max(0, f.days).toFixed(1)} of 5</span>
      {won && <span className="small" style={{ marginLeft: 8 }}>result: <b>#{won} won</b> - we expected #{endA >= endB ? a : b}
        {(endA >= endB ? a : b) === won ? <span style={{ color: "#3fb950" }}> (right)</span> : <span style={{ color: "#ff7b72" }}> (wrong)</span>}</span>}
      <table className="small"><thead><tr><th>metric</th>
        <th className="num">#{a} {day < 5 ? `by end of ${WEEKDAY[day - 1]}` : "final"}</th><th className="num">#{b} {day < 5 ? `by end of ${WEEKDAY[day - 1]}` : "final"}</th>
        {seen && <><th className="num">#{a} seen</th><th className="num">#{b} seen</th></>}
        <th className="num">#{a} end</th><th className="num">#{b} end</th></tr></thead>
        <tbody>
          {METRICS.map((m, i) => (
            <tr key={m}><td>{m}</td>
              <td className="num">{big(da[i])}</td><td className="num">{big(db[i])}</td>
              {seen && <><td className="num muted">{big(seen.scoresA?.[i])}</td><td className="num muted">{big(seen.scoresB?.[i])}</td></>}
              <td className="num">{big(f.endA[i])}{tick(f.endA[i], f.endB[i])}</td><td className="num">{big(f.endB[i])}{tick(f.endB[i], f.endA[i])}</td></tr>
          ))}
          <tr><td><b>total</b></td><td className="num"><b>{big(sum(da))}</b></td><td className="num"><b>{big(sum(db))}</b></td>
            {seen && <><td className="num muted"><b>{big(sum(seen.scoresA))}</b></td><td className="num muted"><b>{big(sum(seen.scoresB))}</b></td></>}
            <td className="num"><b>{big(endA)}{tick(endA, endB)}</b></td><td className="num"><b>{big(endB)}{tick(endB, endA)}</b></td></tr>
        </tbody></table>
      {seen && <p className="small muted">"seen" = the game's board when you logged it on {WEEKDAY[day - 1]} ({when(seen.at)}) - the board shows a day's duel, MVP and Arms Race only the next day, so mid-week it trails the "by end of" column on those. The log below compares each entry with what the board should have shown at that moment.</p>}
      <EstimateHistory h={f.history || []} a={a} b={b} won={won} last={(f.seen || []).slice(-1)[0]} />
    </div>
  );
}

// Every day's end-of-race estimate for the pair, against the newest logged scores and the result once ticked in.
function EstimateHistory({ h, a, b, won, last }) {
  if (!h.length) return null;
  const pct = (est, real) => (real ? `${est >= real ? "+" : ""}${Math.round(((est - real) / real) * 100)}%` : "");
  return (
    <details>
      <summary className="small muted">our end-of-race estimate, day by day ({h.length})</summary>
      <table className="small"><thead><tr><th>estimated on</th><th className="num">#{a} end</th><th className="num">#{b} end</th><th>expected winner</th>
        {last && <><th className="num">v newest #{a}</th><th className="num">v newest #{b}</th></>}{won && <th>right?</th>}</tr></thead>
        <tbody>{h.map((x) => {
          const ea = sum(x.curve?.a?.[4]), eb = sum(x.curve?.b?.[4]);
          const exp = ea >= eb ? a : b;
          return (
            <tr key={x.madeOn}><td>{x.madeOn}</td><td className="num">{big(ea)}</td><td className="num">{big(eb)}</td><td>#{exp}</td>
              {last && <><td className="num muted">{pct(ea, sum(last.scoresA))}</td><td className="num muted">{pct(eb, sum(last.scoresB))}</td></>}
              {won && <td style={{ color: exp === won ? "#3fb950" : "#ff7b72" }}>{exp === won ? "yes" : "no"}</td>}</tr>
          );
        })}</tbody></table>
      {last && <p className="small muted">"v newest" compares each estimate with the newest logged scores ({when(last.at)}) - once the race is over, log the final scores to see how close the end-of-race estimate was.</p>}
    </details>
  );
}
// Scores logged from the game's Weekly Results, each beside what we predicted for that moment - the record to judge
// (and later re-fit) the model with.
function Observations({ admin, start }) {
  const [nonce, setNonce] = useState(0);
  const d = useJson(`/api/svs/observations?n=${nonce}`);
  const rows = d?.rows || [];
  return (
    <div className="card">
      <h3>Logged scores v our predictions</h3>
      <p className="small muted">Log what the game&apos;s Weekly Results page shows, metric by metric (Desert Storm only shows on Friday - leave it empty before). The prediction made for that same moment
        is stored with it, so we can see how close we are and re-fit as more come in.</p>
      {admin && <LogForm start={start} onSaved={() => setNonce((n) => n + 1)} />}
      {rows.map((r) => <Observation key={r.id} r={r} admin={admin} onDeleted={() => setNonce((n) => n + 1)} />)}
      {!rows.length && <p className="muted small">Nothing logged yet.</p>}
    </div>
  );
}

function Observation({ r, admin, onDeleted }) {
  const p = r.predicted || {};
  const [sa, sb, pa, pb] = [r.scoresA, r.scoresB, p.nowA, p.nowB].map(pad7);
  const lead = (x, y) => (sum(x) === sum(y) ? 0 : sum(x) > sum(y) ? 1 : -1);
  const right = lead(sa, sb) === lead(pa, pb);
  const off = (seen, pred) => (seen + pred === 0 ? "" : `${pred >= seen ? "+" : ""}${Math.round(((pred - seen) / Math.max(seen, 1)) * 100)}%`);
  const del = async () => { if (window.confirm("Delete this logged score?")) { await fetch(`/api/svs/observations/${r.id}`, { method: "DELETE" }); onDeleted(); } };
  return (
    <div style={{ marginBottom: 14 }}>
      <b>#{r.a} v #{r.b}</b> <span className="small muted">{when(r.at)} · day {Number(p.days || 0).toFixed(1)} · week from {r.weekStart}{r.note ? ` · ${r.note}` : ""}</span>
      {" "}<span className="small" style={{ color: right ? "#3fb950" : "#ff7b72" }}>{right ? "leader called right" : "leader called wrong"}</span>
      {admin && <button className="small" style={{ marginLeft: 8 }} onClick={del}>delete</button>}
      <table className="small"><thead><tr><th>metric</th><th className="num">#{r.a} seen</th><th className="num">predicted</th><th className="num">off</th>
        <th className="num">#{r.b} seen</th><th className="num">predicted</th><th className="num">off</th></tr></thead>
        <tbody>
          {METRICS.map((m, i) => (
            <tr key={m}><td>{m}</td>
              <td className="num">{big(sa[i])}</td><td className="num muted">{big(pa[i])}</td><td className="num small muted">{off(sa[i], pa[i])}</td>
              <td className="num">{big(sb[i])}</td><td className="num muted">{big(pb[i])}</td><td className="num small muted">{off(sb[i], pb[i])}</td></tr>
          ))}
          <tr><td><b>total</b></td><td className="num"><b>{big(sum(sa))}</b></td><td className="num muted">{big(sum(pa))}</td><td className="num small muted">{off(sum(sa), sum(pa))}</td>
            <td className="num"><b>{big(sum(sb))}</b></td><td className="num muted">{big(sum(pb))}</td><td className="num small muted">{off(sum(sb), sum(pb))}</td></tr>
        </tbody></table>
    </div>
  );
}

function LogForm({ start, onSaved }) {
  const local = () => { const t = new Date(); t.setMinutes(t.getMinutes() - t.getTimezoneOffset()); return t.toISOString().slice(0, 16); };
  const [weekStart, setWeekStart] = useState(start || "");
  const [at, setAt] = useState(local);
  const [a, setA] = useState(""); const [b, setB] = useState("");
  const [va, setVa] = useState(Array(METRICS.length).fill("")); const [vb, setVb] = useState(Array(METRICS.length).fill(""));
  const [note, setNote] = useState(""); const [msg, setMsg] = useState("");
  const put = (set, i, v) => set((old) => old.map((o, j) => (j === i ? v : o)));
  const save = async () => {
    const r = await fetch("/api/svs/observations", { method: "POST", headers: { "content-type": "application/json" },
      body: JSON.stringify({ at: new Date(at).toISOString(), weekStart, a: Number(a), b: Number(b), scoresA: va.map(parsePts), scoresB: vb.map(parsePts), note }) });
    const j = await r.json().catch(() => ({}));
    if (!r.ok) { setMsg(j.error || `${r.status}`); return; }
    setMsg("saved"); setVa(Array(METRICS.length).fill("")); setVb(Array(METRICS.length).fill("")); onSaved();
  };
  const box = (v, set, i) => <input value={v[i]} onChange={(e) => put(set, i, e.target.value)} placeholder="720K" style={{ width: 80 }} />;
  return (
    <details style={{ marginBottom: 12 }}>
      <summary className="small">Log scores from the game</summary>
      <div className="row small" style={{ gap: 8, flexWrap: "wrap", alignItems: "center", marginTop: 6 }}>
        <label>week started <input type="date" value={weekStart} onChange={(e) => setWeekStart(e.target.value)} /></label>
        <label>seen at <input type="datetime-local" value={at} onChange={(e) => setAt(e.target.value)} /></label>
        <input value={a} onChange={(e) => setA(e.target.value.replace(/\D/g, ""))} placeholder="left server" style={{ width: 90 }} />
        <span>v</span>
        <input value={b} onChange={(e) => setB(e.target.value.replace(/\D/g, ""))} placeholder="right server" style={{ width: 90 }} />
      </div>
      <table className="small" style={{ marginTop: 6 }}><tbody>
        {METRICS.map((m, i) => <tr key={m}><td>{m}</td><td>{box(va, setVa, i)}</td><td>{box(vb, setVb, i)}</td></tr>)}
      </tbody></table>
      <div className="row small" style={{ gap: 8, alignItems: "center" }}>
        <input value={note} onChange={(e) => setNote(e.target.value)} placeholder="note (optional)" style={{ width: 220 }} />
        <button className="action" onClick={save}>Save</button><span className="muted">{msg}</span>
      </div>
    </details>
  );
}