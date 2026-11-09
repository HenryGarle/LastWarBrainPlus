import { useEffect, useState } from "react";
import { useSearch } from "../explorer/favs.jsx";
import { useJson } from "../explorer/PlayerTabs.jsx";
import { href } from "../explorer/format.js";

// ---- per-password settings (/api/me/prefs, admin only): myUid = "my player", sendFrom = the account
// messages go out from. One shared copy for every "send to me" button on the page.
let cache = null;
const subs = new Set();
const load = () => fetch("/api/me/prefs").then((r) => (r.ok ? r.json() : { denied: true })).catch(() => ({ denied: true }))
  .then((p) => { cache = p; subs.forEach((f) => f(p)); return p; });
export function usePrefs() {
  const [p, setP] = useState(cache);
  useEffect(() => { subs.add(setP); if (!cache) load(); return () => { subs.delete(setP); }; }, []);
  return p;
}
const savePrefs = (prefs) => fetch("/api/me/prefs", { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(prefs) }).then(load);

/**
 * Runs chat.share.point from the chosen SERVICE account to my player. Guests do not work for this: their
 * DMs never reached Henry in game (2026-10-02), so there is no guest option.
 */
async function sharePoint(prefs, runners, args) {
  const from = runners.find((r) => r.id === prefs.sendFrom);
  if (!from) throw new Error("pick the account to send from on the Account page");
  if (from.state !== "ready" && !window.confirm(`${from.name} is not connected: it is logged in just for this send, which signs out anyone playing it on a phone right now. Send?`)) return null;
  const r = await fetch(`/api/runners/${encodeURIComponent(from.id)}/operation`, {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ operation: "chat.share.point", args: { uid: prefs.myUid, ...Object.fromEntries(Object.entries(args).map(([k, v]) => [k, String(v ?? "")])) } }),
  });
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(j.error || `${r.status}`);
  return j.summary || "sent";
}

/**
 * "📍 Send to me": a tappable map point in a private message to my player. Admins only - hidden when
 * /api/me/prefs is refused. sid/x/y = the point; player = a uid to send it as that player's base.
 */
export function SendPosition({ sid, x, y, label, player, text = "📍 Send to me", runners: given }) {
  const prefs = usePrefs();
  const fetched = useJson(given ? null : "/api/runners");   // pass runners in when rendering many of these
  const runners = given || fetched;
  const [state, setState] = useState(null);
  if (!prefs || prefs.denied || !Array.isArray(runners) || sid == null || x == null || y == null) return null;
  if (!prefs.myUid || !prefs.sendFrom || prefs.sendFrom === "guest") return <a className="action" href="#/account" title="Pick your player and the account to send from">📍 Set up “send to me”</a>;
  const go = async () => {
    setState({ busy: true });
    try {
      const s = await sharePoint(prefs, runners, { sid, x, y, label, player });
      setState(s ? { ok: / ok$/.test(s), text: s } : null);
    } catch (e) { setState({ ok: false, text: e.message }); }
  };
  return (
    <span className="row small" style={{ gap: 6, display: "inline-flex" }}>
      <button className="action" disabled={state?.busy} onClick={go} title={`Sends #${sid} ${x}:${y} (${label}) to your player as a map link`}>{state?.busy ? "Sending…" : text}</button>
      {state?.text && <span className={state.ok ? "muted" : "err"}>{state.ok ? "sent" : state.text}</span>}
    </span>
  );
}

function PlayerPicker({ value, onPick }) {
  const [q, setQ] = useState("");
  const [focused, setFocused] = useState(false);
  const [hits] = useSearch(q, focused);
  const players = hits?.players || [];
  return (
    <div className="search" style={{ maxWidth: 420 }} onFocus={() => setFocused(true)} onBlur={() => setTimeout(() => setFocused(false), 150)}>
      <input value={q} onChange={(e) => setQ(e.target.value)} placeholder={value ? "change: name or uid" : "your player: name or uid"} />
      {focused && players.length > 0 && (
        <div className="search-hits" onMouseDown={(e) => e.preventDefault()}>
          {players.map((p) => (
            <a key={p.uid} href="#" onClick={(e) => { e.preventDefault(); setQ(""); onPick(p.uid); }}>
              {p.fav ? "★ " : ""}{p.name} <span className="muted small">{p.tag ? `[${p.tag}] ` : ""}#{p.server} · {p.uid}</span>
            </a>
          ))}
        </div>
      )}
    </div>
  );
}

/** The Account tab (admin only for now). */
export default function Account() {
  const prefs = usePrefs();
  const runners = useJson("/api/runners");
  const me = useJson(prefs?.myUid ? `/api/x/player/${prefs.myUid}` : "/api/x/player/none");
  const [sid, setSid] = useState("");
  const [x, setX] = useState("500");
  const [y, setY] = useState("500");
  const [label, setLabel] = useState("");
  if (!prefs) return <p className="muted">Loading…</p>;
  if (prefs.denied) return <p className="muted">The Account page is for admins for now.</p>;
  const able = Array.isArray(runners) ? runners.filter((r) => r.tier === "service") : [];
  const n = Number(sid);
  return (
    <div className="grid" style={{ maxWidth: 900 }}>
      <div className="card">
        <h2>My player</h2>
        <p className="small muted">Where “📍 Send to me” sends positions: on player, alliance and server pages.</p>
        {prefs.myUid ? (
          <p>{me?.name ? <a href={href.player(prefs.myUid)}>{me.name}</a> : prefs.myUid} <span className="muted small">{me?.tag ? `[${me.tag}] ` : ""}{me?.server ? `#${me.server}` : ""} · {prefs.myUid}</span>{" "}
            <button onClick={() => savePrefs({ myUid: "" })}>clear</button></p>
        ) : <p className="muted small">Not set.</p>}
        <PlayerPicker value={prefs.myUid} onPick={(uid) => savePrefs({ myUid: uid })} />
      </div>
      <div className="card">
        <h2>Send from</h2>
        <p className="small muted">The service account messages and positions go out from (guest DMs do not reach real players). A stopped one is logged in just for each send (and signs out a phone playing it).</p>
        <select value={prefs.sendFrom === "guest" ? "" : prefs.sendFrom || ""} onChange={(e) => savePrefs({ sendFrom: e.target.value })}>
          <option value="">- pick an account -</option>
          {able.map((r) => <option key={r.id} value={r.id}>{r.name} ({r.tier}{r.state === "ready" ? ", connected" : ""})</option>)}
        </select>
      </div>
      <div className="card">
        <h2>Send a position</h2>
        <p className="small muted">Any server, scanned or not: the number alone sends its centre (500:500) as a link that opens that map in the game.</p>
        <div className="row small" style={{ gap: 8, flexWrap: "wrap", alignItems: "center" }}>
          <label>server <input value={sid} onChange={(e) => setSid(e.target.value.replace(/\D/g, ""))} style={{ width: 80 }} placeholder="1001" /></label>
          <label>x <input value={x} onChange={(e) => setX(e.target.value.replace(/\D/g, ""))} style={{ width: 60 }} /></label>
          <label>y <input value={y} onChange={(e) => setY(e.target.value.replace(/\D/g, ""))} style={{ width: 60 }} /></label>
          <label>label <input value={label} onChange={(e) => setLabel(e.target.value)} style={{ width: 180 }} placeholder={n ? `#${n} ${x}:${y}` : ""} /></label>
          {n > 0 && <SendPosition sid={n} x={Number(x) || 0} y={Number(y) || 0} label={label.trim() || `#${n} ${x}:${y}`} />}
          {n > 0 && <a href={`#/maps/${n}`}>our map of #{n} →</a>}
        </div>
      </div>
    </div>
  );
}
