import { useCallback, useEffect, useRef, useState } from "react";
import { when, ago } from "../explorer/format.js";
import { PlayerLink } from "../explorer/bits.jsx";
import { useHashParam } from "../nav.js";

// Admin: one of our accounts as a chat client. Its private rooms and messages come live from the
// game (they are never stored with public chat); replies go out as that account. Battle reports
// shared into these rooms are collected for the Reports tab when you press Collect (manual only since 2026-10-02 - nothing runs on a timer).
const j = async (url, opts) => {
  const r = await fetch(url, opts);
  const body = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(body.error || `${r.status}`);
  return body;
};

export default function Inbox({ runners = [], refresh }) {
  const accounts = runners.filter((r) => r.kind !== "guest");
  const [runner, setRunner] = useHashParam("as", "");
  const [room, setRoom] = useHashParam("room", "");
  const [status, setStatus] = useState(null);
  const [rooms, setRooms] = useState(null);
  const [err, setErr] = useState("");
  const [msg, setMsg] = useState("");
  const current = accounts.find((r) => r.id === runner) || accounts.find((r) => r.state === "ready") || accounts[0];
  const id = current?.id;
  const loadStatus = useCallback(() => j("/api/chat/status").then(setStatus).catch(() => {}), []);
  useEffect(() => { loadStatus(); }, [loadStatus, id]);
  const chatOpen = !!status?.listeners?.some((l) => l.runner === id && l.alive);
  const loadRooms = useCallback(() => {
    if (!id || !chatOpen) return;
    j(`/api/inbox/${id}/rooms`).then((r) => { setRooms(r); setErr(""); }).catch((e) => setErr(e.message));
  }, [id, chatOpen]);
  useEffect(() => { setRooms(null); loadRooms(); }, [loadRooms]);
  useEffect(() => { const t = setInterval(loadRooms, 20000); return () => clearInterval(t); }, [loadRooms]);

  const start = async () => { setMsg("Starting…"); try { await j(`/api/runners/${id}/start`, { method: "POST" }); refresh?.(); setMsg("Started - open its chat next."); } catch (e) { setMsg(e.message); } };
  const openChat = async () => { setMsg("Opening chat…"); try { await j(`/api/runners/${id}/chat/start`, { method: "POST" }); await loadStatus(); setMsg(""); } catch (e) { setMsg(e.message); } };
  const collect = async () => {
    setMsg("Collecting shared battle reports…");
    try { const r = await j(`/api/inbox/${id}/collect`, { method: "POST" }); setMsg(`${r.shares} new shares, ${r.fetched} reports fetched${r.failed ? `, ${r.failed} failed` : ""}.`); }
    catch (e) { setMsg(e.message); }
  };

  return (
    <div className="grid">
      <div className="page-head">
        <div>
          <h1 className="page-title">Inbox</h1>
          <p className="muted">Read and answer an account&apos;s private messages. Battle reports shared here are collected for the Reports tab.</p>
        </div>
      </div>
      <div className="card row small" style={{ gap: 8, flexWrap: "wrap", alignItems: "center" }}>
        <b>Account</b>
        <select value={id || ""} onChange={(e) => { setRunner(e.target.value); setRoom(""); }}>
          {accounts.map((r) => <option key={r.id} value={r.id}>{r.name || r.id} ({r.tier || r.kind}, {r.state})</option>)}
        </select>
        {current && current.state !== "ready" && <button className="primary" onClick={start}>Sign in</button>}
        {current?.state === "ready" && !chatOpen && <button className="primary" onClick={openChat}>Open its chat</button>}
        {chatOpen && <button onClick={collect}>Collect battle reports</button>}
        {chatOpen && <button className="linkish" onClick={loadRooms}>refresh</button>}
        {msg && <span>{msg}</span>}
        {current?.tier === "privileged" && <span className="muted">Chat never runs on a privileged (real) account.</span>}
      </div>
      {err && <p className="err">{err}</p>}
      {chatOpen && (
        <div className="inbox">
          <div className="card inbox-rooms">
            {!rooms ? <p className="muted small">Loading rooms…</p> : rooms.length === 0 ? <p className="muted small">No private messages.</p> : rooms.map((r) => (
              <button key={r.room} className={`inbox-room ${room === r.room ? "on" : ""}`} onClick={() => setRoom(r.room)}>
                <div className="row" style={{ justifyContent: "space-between", gap: 6 }}>
                  <b>{r.otherTag ? `[${r.otherTag}] ` : ""}{r.otherName || r.other || r.name}</b>
                  {r.lastSeq > (r.readSeq ?? r.lastSeq) && <span className="chip on small">{r.lastSeq - r.readSeq}</span>}
                </div>
                <div className="muted small">{r.lastText || ""}</div>
                <div className="muted small">{r.lastAt ? ago(r.lastAt) : ""}</div>
              </button>
            ))}
          </div>
          {room ? <Room runner={id} room={room} info={rooms?.find((r) => r.room === room)} /> : <div className="card muted">Pick a conversation.</div>}
        </div>
      )}
    </div>
  );
}

function Room({ runner, room, info }) {
  const [msgs, setMsgs] = useState([]);
  const [err, setErr] = useState("");
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const end = useRef(null);
  const merge = (older, newer) => { const m = new Map([...older, ...newer].map((x) => [x.seq, x])); return [...m.values()].sort((a, b) => a.seq - b.seq); };
  const load = useCallback((before) =>
    j(`/api/inbox/${runner}/rooms/${encodeURIComponent(room)}/messages${before ? `?before=${before}` : ""}`)
      .then((page) => { setMsgs((m) => (before ? merge(page, m) : merge(m, page))); setErr(""); return page; })
      .catch((e) => setErr(e.message)), [runner, room]);
  useEffect(() => { setMsgs([]); load().then(() => setTimeout(() => end.current?.scrollIntoView({ block: "end" }), 50)); }, [load]);
  useEffect(() => { const t = setInterval(() => load(), 10000); return () => clearInterval(t); }, [load]);
  const send = async (e) => {
    e.preventDefault();
    if (!text.trim()) return;
    setBusy(true);
    try {
      const r = await j(`/api/inbox/${runner}/rooms/${encodeURIComponent(room)}/send`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ text }) });
      if (r.ok === false) throw new Error(r.error || "not sent");
      setText("");
      setTimeout(() => load().then(() => end.current?.scrollIntoView({ block: "end" })), 1200);
    } catch (e2) { setErr(e2.message); }
    setBusy(false);
  };
  const oldest = msgs[0]?.seq;
  return (
    <div className="card inbox-room-view">
      <div className="row small" style={{ justifyContent: "space-between" }}>
        <b>{info?.otherName ? <PlayerLink uid={info.other} name={`${info.otherTag ? `[${info.otherTag}] ` : ""}${info.otherName}`} /> : room}</b>
        {oldest > 1 && <button className="linkish" onClick={() => load(oldest)}>older messages</button>}
      </div>
      {err && <p className="err small">{err}</p>}
      <div className="inbox-msgs">
        {msgs.map((m) => <Message key={m.seq} m={m} />)}
        <div ref={end} />
      </div>
      <form className="row" style={{ gap: 6 }} onSubmit={send}>
        <input value={text} onChange={(e) => setText(e.target.value)} placeholder="Reply…" maxLength={500} style={{ flex: 1 }} />
        <button className="primary" disabled={busy || !text.trim()}>Send</button>
      </form>
    </div>
  );
}

function Message({ m }) {
  const a = m.attachment;
  let body = m.text;
  if (m.post === 13 && a) body = <a href={`#/x/map/${a.sid}/${a.x},${a.y}`}>📍 {a.oname || `#${a.sid}`} · #{a.sid} {a.x}:{a.y}</a>;
  if (m.post === 4 && m.report) body = (
    <span className="inbox-report">
      ⚔ Battle report <span className="muted">· {m.report.mailType}{m.opponent?.name ? ` · vs ${m.opponent.tag ? `[${m.opponent.tag}] ` : ""}${m.opponent.name}` : ""}</span>{" "}
      {m.report.fetched ? <a href={`#/reports/${m.report.reportUid}`}>open</a>
        : m.report.error ? <span className="err small" title={m.report.error}>could not fetch</span>
        : <span className="muted small">not collected yet</span>}
    </span>
  );
  return (
    <div className={`inbox-msg ${m.mine ? "mine" : ""}`}>
      <div className="small muted">{m.mine ? "you" : `${m.tag ? `[${m.tag}] ` : ""}${m.name}`} · <span title={when(m.at)}>{ago(m.at)}</span></div>
      <div>{body}</div>
    </div>
  );
}
