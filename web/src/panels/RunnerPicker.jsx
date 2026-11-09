import { useState } from "react";
import { api } from "../api.js";

// Which runner a page works with. Only the pages that talk to one live session show this
// (Commands, Raw TCP, Chat, and Operations that need a session) - it used to sit in the top bar.
// `controls` adds Start/Stop for pages that need the runner up the whole time.
// `allowed`: the account tiers the page's action accepts (guest / service / privileged); other
// runners stay listed but cannot be picked, so it is clear WHY an account is not offered.
export default function RunnerPicker({ runners, selected, setSelected, refresh, controls = false, note, allowed }) {
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState(null);
  const runner = runners.find((r) => r.id === selected) || null;
  const act = async (fn) => {
    setBusy(true); setErr(null);
    try { await fn(); } catch (e) { setErr(e.message); } finally { setBusy(false); await refresh?.(); }
  };
  const up = runner && (runner.state === "ready" || runner.state === "connecting");

  return (
    <div className="runner-pick row small" style={{ gap: 8, flexWrap: "wrap", marginBottom: 10 }}>
      <span className="muted">runner</span>
      <select value={selected || ""} onChange={(e) => setSelected(e.target.value)} style={{ width: "auto" }}>
        {runners.map((r) => (
          <option key={r.id} value={r.id} disabled={allowed && !allowed.includes(r.tier)}>
            {r.name} ({r.tier}) - {r.state}{allowed && !allowed.includes(r.tier) ? " - not allowed here" : ""}
          </option>
        ))}
      </select>
      {runner && <span><span className={`dot ${runner.state}`} /> {runner.state}{runner.serverId ? ` on ${runner.serverId}` : ""}</span>}
      {runner && <span className={`pill tier-${runner.tier}`}>{runner.tier}</span>}
      {runner && allowed && !allowed.includes(runner.tier) && <span className="err">a {runner.tier} account cannot run this - needs {allowed.join(" or ")}</span>}
      {controls && runner && (up
        ? <button disabled={busy} onClick={() => act(() => api.stop(runner.id))}>Stop</button>
        : <button className="primary" disabled={busy} onClick={() => act(() => api.start(runner.id))}>Start</button>)}
      {note && <span className="muted">{note}</span>}
      {err && <span className="err">{err}</span>}
    </div>
  );
}
