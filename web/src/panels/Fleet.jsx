import { useState } from "react";
import { api } from "../api.js";

const fmt = (t) => (t ? new Date(t).toLocaleTimeString() : "-");

export default function Fleet({ runners, selected, setSelected, refresh, events }) {
  const [busy, setBusy] = useState(null);
  const [error, setError] = useState(null);

  const act = async (id, fn) => {
    setBusy(id);
    setError(null);
    try {
      await fn(id);
    } catch (e) {
      setError(`${id}: ${e.message}`);
    } finally {
      setBusy(null);
      await refresh();
    }
  };

  const recent = events.filter((e) => e.kind === "state" || e.kind === "error").slice(-12).reverse();

  return (
    <div className="grid">
      {error && <p className="err">{error}</p>}
      <div className="card">
        <h2>Fleet</h2>
        <table>
          <thead>
            <tr>
              <th>runner</th>
              <th>state</th>
              <th>server</th>
              <th>account</th>
              <th className="num">calls</th>
              <th className="num">frames</th>
              <th>started</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {runners.map((r) => (
              <tr
                key={r.id}
                onClick={() => setSelected(r.id)}
                style={{ cursor: "pointer", background: r.id === selected ? "#1a212b" : undefined }}
              >
                <td>
                  <strong>{r.name}</strong>
                  {r.note && <div className="muted small">{r.note}</div>}
                  {r.error && <div className="err small">{r.error}</div>}
                </td>
                <td>
                  <span className={`dot ${r.state}`} /> {r.state}
                </td>
                <td className="mono">{r.serverId || "-"}</td>
                <td className="mono">{r.uid || "-"}</td>
                <td className="num">{r.calls}</td>
                <td className="num">{r.frames}</td>
                <td className="small muted">{fmt(r.startedAt)}</td>
                <td>
                  <div className="row" style={{ gap: 6 }}>
                    <button
                      className="primary"
                      disabled={busy === r.id || r.state === "ready" || r.state === "connecting"}
                      onClick={(e) => {
                        e.stopPropagation();
                        act(r.id, api.start);
                      }}
                    >
                      Start
                    </button>
                    <button
                      className="danger"
                      disabled={busy === r.id || r.state === "stopped"}
                      onClick={(e) => {
                        e.stopPropagation();
                        act(r.id, api.stop);
                      }}
                    >
                      Stop
                    </button>
                  </div>
                </td>
              </tr>
            ))}
            {runners.length === 0 && (
              <tr>
                <td colSpan={8} className="muted">
                  No runners. Drop a JSON file in data\runners\ and press Reload.
                </td>
              </tr>
            )}
          </tbody>
        </table>
        <div className="row" style={{ marginTop: 10 }}>
          <button onClick={() => act("reload", api.reload)}>Reload from disk</button>
          <span className="muted small">
            One session per account: a second login on the same session file evicts the first,
            so only one runner per file can run at a time.
          </span>
        </div>
      </div>

      <div className="card">
        <h2>Recent lifecycle events</h2>
        <table>
          <tbody>
            {recent.map((e) => (
              <tr key={e.seq}>
                <td className="mono muted" style={{ width: 90 }}>
                  {new Date(e.at).toLocaleTimeString()}
                </td>
                <td className={e.kind === "error" ? "err" : ""}>{e.kind}</td>
                <td className="mono">{JSON.stringify(e.data)}</td>
              </tr>
            ))}
            {recent.length === 0 && (
              <tr>
                <td className="muted">Nothing yet for this runner.</td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
