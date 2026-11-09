import { useEffect, useState } from "react";
import { api } from "../api.js";

export default function Data() {
  const [tables, setTables] = useState([]);
  const [presets, setPresets] = useState([]);
  const [sql, setSql] = useState("SELECT room_id, COUNT(*) AS messages FROM chat_message GROUP BY room_id");
  const [limit, setLimit] = useState(200);
  const [res, setRes] = useState(null);
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.tables().then(setTables).catch((e) => setError(e.message));
    api.presets().then(setPresets).catch(() => {});
  }, []);

  const run = async (text = sql) => {
    setBusy(true);
    setError(null);
    try {
      setRes(await api.query(text, Number(limit) || 200));
    } catch (e) {
      setRes(null);
      setError(e.message);
    } finally {
      setBusy(false);
    }
  };

  const csv = () => {
    if (!res) return;
    const esc = (v) => (v == null ? "" : /[",\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v);
    const text = [res.columns.join(","), ...res.rows.map((r) => r.map(esc).join(","))].join("\n");
    navigator.clipboard.writeText(text);
  };

  return (
    <div className="cols">
      <div className="grid">
        <div className="card">
          <h2>Saved queries</h2>
          <div className="list">
            {presets.map((p) => (
              <button key={p.name} onClick={() => { setSql(p.sql); run(p.sql); }}>
                <div>{p.name}</div>
                <div className="muted small">{p.description}</div>
              </button>
            ))}
          </div>
        </div>
        <div className="card">
          <h2>Tables</h2>
          <div className="list">
            {tables.map((t) => (
              <button key={t.name} onClick={() => { const s = `SELECT * FROM ${t.name}`; setSql(s); run(s); }}>
                <div className="mono">{t.name}</div>
                <div className="muted small">{t.columns}</div>
              </button>
            ))}
          </div>
        </div>
      </div>

      <div className="grid">
        <div className="card">
          <h2>Query</h2>
          <textarea rows={8} value={sql} onChange={(e) => setSql(e.target.value)} spellCheck={false} />
          <div className="row" style={{ marginTop: 10 }}>
            <button className="primary" disabled={busy} onClick={() => run()}>
              {busy ? "Running..." : "Run"}
            </button>
            <label className="row small muted" style={{ gap: 6, width: "auto" }}>
              limit
              <input type="number" min="1" max="2000" value={limit}
                onChange={(e) => setLimit(e.target.value)} style={{ width: 80 }} />
            </label>
            <button disabled={!res} onClick={csv}>Copy as CSV</button>
            <span className="muted small">read-only connection - SELECT and WITH only</span>
          </div>
          {error && <p className="err small">{error}</p>}
        </div>

        {res && (
          <div className="card">
            <h2>
              Result <span className="muted">{res.count} row{res.count === 1 ? "" : "s"}</span>
            </h2>
            <div className="scroll">
              <table>
                <thead>
                  <tr>{res.columns.map((c) => <th key={c}>{c}</th>)}</tr>
                </thead>
                <tbody>
                  {res.rows.map((r, i) => (
                    <tr key={i}>
                      {r.map((v, j) => (
                        <td key={j} className={v !== null && v !== "" && !isNaN(v) ? "num" : ""}>{v}</td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
