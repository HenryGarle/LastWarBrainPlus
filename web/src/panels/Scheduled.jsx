import { useCallback, useEffect, useState } from "react";
import { when, ago } from "../explorer/format.js";
import { usePolling } from "../nav.js";

// Scheduled operations: what runs on a timetable, what it is doing now, and how the last runs went.
const call = (url, method = "POST", body) =>
  fetch(url, { method, headers: { "content-type": "application/json" }, body: body ? JSON.stringify(body) : undefined })
    .then(async (r) => { if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText); });

const dur = (a, b) => {
  const s = Math.max(0, (new Date(b || Date.now()) - new Date(a)) / 1000);
  return s < 90 ? `${Math.round(s)} s` : s < 5400 ? `${Math.round(s / 60)} min` : `${(s / 3600).toFixed(1)} h`;
};
const every = (m) => (m % 1440 === 0 ? `${m / 1440} day(s)` : m % 60 === 0 ? `${m / 60} hour(s)` : `${m} min`);
const STATE = { done: "ready", running: "connecting", failed: "faulted", stopped: "stopped", interrupted: "stopped" };
const PERIODS = [15, 30, 60, 120, 180, 240, 360, 480, 720, 1440];

function Job({ j, act, runners }) {
  const [editing, setEditing] = useState(false);
  const [mins, setMins] = useState(j.everyMinutes);
  const r = j.running;
  const onAccount = "runner" in (j.args || {});   // a job that runs on a chosen account (the map needs a real one)
  const runner = runners.find((x) => x.id === j.args?.runner);
  const shownArgs = Object.entries(j.args || {}).filter(([k]) => k !== "runner");
  const periods = PERIODS.includes(j.everyMinutes) ? PERIODS : [...PERIODS, j.everyMinutes].sort((a, b) => a - b);
  return (
    <div className="card">
      <div className="row" style={{ gap: 10, flexWrap: "wrap" }}>
        <h2 style={{ margin: 0 }}>{j.name}</h2>
        <span className={`pill ${r ? "" : j.enabled ? "" : "muted"}`}>
          <span className={`dot ${r ? "connecting" : j.enabled ? "ready" : "stopped"}`} /> {r ? "running" : j.enabled ? "scheduled" : "paused"}
        </span>
        <span className="spacer" />
        <label className="small muted row" style={{ gap: 6 }}>every
          <select value={j.everyMinutes} onChange={(e) => act(j.id, "every", Number(e.target.value))} style={{ width: "auto" }}>
            {periods.map((m) => <option key={m} value={m}>{every(m)}</option>)}
          </select>
        </label>
        {r ? <button onClick={() => act(j.id, "stop")}>Stop this run</button> : <button onClick={() => act(j.id, "run")} disabled={onAccount && !runner}>Run now</button>}
        {j.enabled ? <button onClick={() => act(j.id, "pause")}>Pause</button> : <button onClick={() => act(j.id, "resume")} disabled={onAccount && !runner}>Start schedule</button>}
      </div>
      {onAccount && (
        <div className="row small" style={{ gap: 8, margin: "8px 0", flexWrap: "wrap" }}>
          <label className="muted row" style={{ gap: 6 }}>Account
            <select value={j.args.runner || ""} onChange={(e) => act(j.id, "runner", e.target.value)} style={{ width: "auto" }}>
              <option value="">- pick one -</option>
              {runners.map((x) => <option key={x.id} value={x.id}>{x.name || x.id} ({x.tier}{x.note ? `, ${x.note}` : ""})</option>)}
            </select>
          </label>
          {runner && runner.kind !== "guest" && (
            <span className="muted">A real account: each run logs it in for the run only, which signs it out of the game on your phone.</span>
          )}
          {!runner && <span className="muted">Pick the account first - guests cannot see other servers&apos; maps.</span>}
        </div>
      )}
      <p className="small muted" style={{ margin: "6px 0" }}>
        <span className="mono">{j.op}</span> {shownArgs.map(([k, v]) => `${k}=${v}`).join(" ")} ·{" "}
        every {editing ? (
          <>
            <input type="number" min={5} value={mins} onChange={(e) => setMins(Number(e.target.value))} style={{ width: 70 }} /> min{" "}
            <button onClick={() => act(j.id, "every", mins).then(() => setEditing(false))}>Save</button>{" "}
            <button onClick={() => setEditing(false)}>Cancel</button>
          </>
        ) : <a href="#" onClick={(e) => { e.preventDefault(); setEditing(true); }}>{every(j.everyMinutes)}</a>}
        {" "}· next {j.enabled ? (j.nextAt ? <span title={when(j.nextAt)}>{new Date(j.nextAt) <= new Date() ? "due now" : `in ${dur(Date.now(), j.nextAt)}`} ({when(j.nextAt)})</span> : "now") : "— paused"}
      </p>
      {j.description && <p className="small muted" style={{ margin: "0 0 8px" }}>{j.description}</p>}
      {r && (
        <>
          <p className="small">Started {when(r.startedAt)} · running for {dur(r.startedAt)}</p>
          <pre className="small mono" style={{ maxHeight: 180, overflow: "auto", margin: 0 }}>{(r.lines || []).slice(-12).join("\n") || "starting…"}</pre>
        </>
      )}
      {!r && j.lastRun && (
        <p className="small">
          Last run <span className={`dot ${STATE[j.lastRun.state] || ""}`} /> {j.lastRun.state} {ago(j.lastRun.startedAt)} ({dur(j.lastRun.startedAt, j.lastRun.finishedAt)})
          {j.lastRun.summary && <span className="muted"> — {j.lastRun.summary}</span>}
        </p>
      )}
    </div>
  );
}

export default function Scheduled() {
  const [d, setD] = useState(null);
  const [err, setErr] = useState(null);
  const load = useCallback(() => fetch("/api/schedule").then((r) => (r.ok ? r.json() : Promise.reject(new Error(r.statusText))))
    .then((x) => { setD(x); setErr(null); }).catch((e) => setErr(e.message)), []);
  const running = d?.jobs?.some((j) => j.running);
  usePolling(load, running ? 3000 : 30000);
  const [runners, setRunners] = useState([]);
  useEffect(() => { fetch("/api/runners").then((r) => (r.ok ? r.json() : [])).then(setRunners).catch(() => {}); }, []);
  const act = (id, what, arg) =>
    (what === "every" ? call(`/api/schedule/${id}`, "PUT", { everyMinutes: arg })
      : what === "runner" ? call(`/api/schedule/${id}`, "PUT", { runner: arg })
        : call(`/api/schedule/${id}/${what}`))
      .then(load).catch((e) => setErr(e.message));
  if (!d) return <p className="muted">{err || "Loading…"}</p>;
  const names = Object.fromEntries(d.jobs.map((j) => [j.id, j.name]));
  return (
    <div className="grid">
      {err && <p className="err">{err}</p>}
      <p className="small muted">Scheduled operations run inside the server - on fresh guest accounts, or on the account a job names (the map needs a real one). Pausing stops new runs; the one in progress finishes unless you stop it.</p>
      {d.jobs.map((j) => <Job key={j.id} j={j} act={act} runners={runners} />)}
      <div className="card">
        <h2>Recent runs</h2>
        {d.runs.length === 0 ? <p className="muted small">Nothing has run yet.</p> : (
          <div className="scroll">
            <table className="small">
              <thead><tr><th>Job</th><th>Started</th><th>Took</th><th>How</th><th>Result</th><th>Summary</th></tr></thead>
              <tbody>
                {d.runs.map((r) => (
                  <tr key={r.id}>
                    <td>{names[r.jobId] || r.jobId}</td>
                    <td className="mono" title={r.startedAt}>{when(r.startedAt)}</td>
                    <td className="mono">{dur(r.startedAt, r.finishedAt)}</td>
                    <td className="muted">{r.trigger}</td>
                    <td><span className={`dot ${STATE[r.state] || ""}`} /> {r.state}</td>
                    <td className="muted">{r.summary || r.progress}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
