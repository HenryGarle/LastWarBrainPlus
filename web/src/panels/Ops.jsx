import { useEffect, useMemo, useState } from "react";
import { href } from "../explorer/format.js";
import RunnerPicker from "./RunnerPicker.jsx";
import { hashQuery, replaceQuery, sectionArgs, setTitle } from "../nav.js";

const post = async (url, body) => {
  const res = await fetch(url, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || res.statusText);
  return data;
};

// A parameter renders from its declared type. Nothing here knows what any operation is.
function Field({ param, value, onChange }) {
  if (param.type === "bool")
    return (
      <label className="row small" style={{ gap: 8, width: "auto" }}>
        <input
          type="checkbox" style={{ width: "auto" }}
          checked={value === "true"}
          onChange={(e) => onChange(String(e.target.checked))}
        />
        {param.label}
      </label>
    );
  return (
    <div>
      <label className="small muted">
        {param.label}
        {param.required && <span className="err"> *</span>}
      </label>
      <input
        type={param.type === "number" ? "number" : "text"}
        value={value ?? ""}
        onChange={(e) => onChange(e.target.value)}
      />
      {param.help && <div className="muted small">{param.help}</div>}
    </div>
  );
}

const esc = (v) => (v == null ? "" : /[",\n]/.test(v) ? `"${String(v).replace(/"/g, '""')}"` : v);
const toCsv = (columns, rows) => [columns.join(","), ...rows.map((r) => r.map(esc).join(","))].join("\n");
// Every column, whatever is showing. The BOM makes Excel read names in other scripts correctly.
const download = (table) => {
  const url = URL.createObjectURL(new Blob(["\ufeff" + toCsv(table.columns, table.rows)], { type: "text/csv;charset=utf-8" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = `${table.title.replace(/[^\w#-]+/g, "_")}_${new Date().toISOString().slice(0, 16).replace(/[:T]/g, "")}.csv`;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
};

// A titled table from an operation, drawn from its own hints: which columns to show until the
// reader asks for all of them, and which cells link to an explorer page ("player:uid").
function ResultTable({ table }) {
  const [all, setAll] = useState(false);
  const brief = table.brief?.length ? table.brief : table.columns;
  const shown = (all ? table.columns : brief).map((c) => table.columns.indexOf(c)).filter((i) => i >= 0);
  // Summed columns: shown with thousands separators, and totalled under the table. A blank cell
  // (e.g. no hero power known) is left out of the sum, and the footer says how many were known.
  const summed = new Set((table.totals || []).map((c) => table.columns.indexOf(c)).filter((i) => i >= 0));
  const total = (i) => {
    const vals = table.rows.map((r) => r[i]).filter((v) => v !== "" && v != null && !isNaN(v));
    return { sum: vals.reduce((s, v) => s + Number(v), 0), known: vals.length };
  };
  const cell = (row, i) => {
    const v = summed.has(i) && row[i] !== "" && row[i] != null && !isNaN(row[i]) ? Number(row[i]).toLocaleString() : row[i];
    const link = table.links?.[table.columns[i]];
    if (!link || v === "" || v == null) return v;
    const [kind, idCol] = link.split(":");
    const id = idCol ? row[table.columns.indexOf(idCol)] : v;
    return href[kind] && id ? <a href={href[kind](id)}>{v}</a> : v;
  };
  return (
    <div style={{ marginTop: 14 }}>
      <div className="row" style={{ marginBottom: 6, gap: 10 }}>
        <strong>{table.title}</strong><span className="muted small">{table.rows.length}</span>
        <span style={{ flex: 1 }} />
        {table.brief?.length > 0 && table.brief.length < table.columns.length && (
          <label className="row small" style={{ gap: 6, width: "auto" }}>
            <input type="checkbox" style={{ width: "auto" }} checked={all} onChange={(e) => setAll(e.target.checked)} />
            show all columns
          </label>
        )}
        {table.rows.length > 0 && <button onClick={() => navigator.clipboard.writeText(toCsv(table.columns, table.rows))}>Copy as CSV</button>}
        {table.rows.length > 0 && <button onClick={() => download(table)}>Download CSV</button>}
      </div>
      {table.rows.length === 0 ? <p className="muted small">None.</p> : (
        <div className="scroll">
          <table>
            <thead><tr>{shown.map((i) => <th key={i}>{table.columns[i]}</th>)}</tr></thead>
            <tbody>
              {table.rows.map((r, n) => (
                <tr key={n}>
                  {shown.map((i) => (
                    <td key={i} className={r[i] !== "" && r[i] !== null && !isNaN(r[i]) ? "num" : ""}>{cell(r, i)}</td>
                  ))}
                </tr>
              ))}
            </tbody>
            {summed.size > 0 && (
              <tfoot>
                <tr>
                  {shown.map((i, n) => {
                    if (summed.has(i)) {
                      const t = total(i);
                      return (
                        <td key={i} className="num">
                          <strong>{t.sum.toLocaleString()}</strong>
                          {t.known < table.rows.length && <div className="muted small">{t.known} of {table.rows.length} known</div>}
                        </td>
                      );
                    }
                    return <td key={i}>{n === 0 ? <strong>Total ({table.rows.length})</strong> : ""}</td>;
                  })}
                </tr>
              </tfoot>
            )}
          </table>
        </div>
      )}
    </div>
  );
}
export default function Ops({ runner, runners, selected, setSelected, refresh, events }) {
  const [ops, setOps] = useState([]);
  const [pick, setPick] = useState(null);
  const [args, setArgs] = useState({});
  const [result, setResult] = useState(null);
  const [busy, setBusy] = useState(false);
  const [filter, setFilter] = useState("");

  useEffect(() => {
    fetch("/api/operations").then((r) => r.json()).then(setOps).catch(() => {});
  }, []);

  const groups = useMemo(() => {
    const f = filter.trim().toLowerCase();
    const shown = ops.filter(
      (o) => !f || o.title.toLowerCase().includes(f) || o.id.includes(f) || o.group.toLowerCase().includes(f)
    );
    // The refresh operations are what gets run day to day, so they lead.
    return [...new Map(shown.map((o) => [o.group, shown.filter((x) => x.group === o.group)]))]
      .sort(([a], [b]) => (b === "Refresh") - (a === "Refresh"));
  }, [ops, filter]);

  // #/ops/<operation>?<arg>=<value>: the operation is in the path (Back returns to the last one),
  // arguments that differ from their defaults ride in the query, so a run can be bookmarked.
  const [opId, setOpId] = useState(() => sectionArgs()[0] || null);
  useEffect(() => {
    const on = () => { if (window.location.hash.startsWith("#/ops")) setOpId(sectionArgs()[0] || null); };
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);
  useEffect(() => {
    const op = ops.find((o) => o.id === opId) || null;
    if (op?.id === pick?.id) return;
    setPick(op);
    setResult(null);
    const q = hashQuery();
    setArgs(op ? Object.fromEntries(op.params.map((p) => [p.name, q.get(p.name) ?? p.default ?? ""])) : {});
  }, [ops, opId]);   // eslint-disable-line react-hooks/exhaustive-deps
  const choose = (op) => { if (op.id !== opId) window.location.hash = `#/ops/${op.id}`; };
  useEffect(() => { setTitle(pick?.title, "Operations"); }, [pick]);
  // Offer an account that may actually run it (nothing starts until Fetch is pressed).
  useEffect(() => {
    if (!pick?.needsSession || (runner && pick.accountNames.includes(runner.tier))) return;
    const ok = runners.find((r) => pick.accountNames.includes(r.tier));
    if (ok) setSelected(ok.id);
  }, [pick?.id]);   // eslint-disable-line react-hooks/exhaustive-deps
  const setArg = (param, v) => {
    setArgs((a) => ({ ...a, [param.name]: v }));
    replaceQuery({ [param.name]: v === (param.default ?? "") ? undefined : v });
  };

  const run = async () => {
    setBusy(true);
    setResult(null);
    try {
      setResult(await post(`/api/runners/${selected}/operation`, { operation: pick.id, args }));
    } catch (e) {
      setResult({ ok: false, error: e.message });
    } finally {
      setBusy(false);
      refresh?.();
    }
  };

  // Long sweeps report as they go; show the tail while one is running.
  const progress = useMemo(
    () => events.filter((e) => e.kind === "progress").slice(-6).reverse(),
    [events]
  );

  const csv = () => { if (result?.rows) navigator.clipboard.writeText(toCsv(result.columns, result.rows)); };

  // A stopped runner is fine: the server starts it for this one run and stops it again after.
  const blocked = pick?.needsSession && (!runner || !pick.accountNames.includes(runner.tier));

  return (
    <div className="cols">
      <div className="card">
        <h2>Operations</h2>
        <input placeholder="filter" value={filter} onChange={(e) => setFilter(e.target.value)} />
        <div className="list" style={{ marginTop: 8 }}>
          {groups.map(([group, items]) => (
            <div key={group}>
              <div className="muted small" style={{ padding: "8px 8px 2px", letterSpacing: ".6px" }}>
                {group.toUpperCase()}
              </div>
              {items.map((o) => (
                <button key={o.id} className={pick?.id === o.id ? "on" : ""} onClick={() => choose(o)}>
                  <div>
                    {o.title}
                    {o.writes && <span className="pill tx" style={{ marginLeft: 6 }}>changes</span>}
                    {!o.needsSession && <span className="pill" style={{ marginLeft: 6 }} title="Runs without a runner - reads the store, or uses its own throwaway guests">no runner</span>}
                    {o.needsSession && <span className="pill" style={{ marginLeft: 6 }} title="Which accounts may run it">{o.accountNames.join(" · ")}</span>}
                  </div>
                  <div className="muted small mono">{o.id}</div>
                </button>
              ))}
            </div>
          ))}
        </div>
      </div>

      <div className="grid">
        <div className="card">
          <h2>{pick ? pick.title : "Pick an operation"}</h2>
          {!pick && <p className="muted">Everything the tooling can do is on the left.</p>}
          {pick && (
            <>
              <p className="muted small" style={{ marginTop: 0 }}>{pick.description}</p>
              {pick.params.length > 0 && (
                <div className="grid" style={{ gap: 8, marginBottom: 10 }}>
                  {pick.params.map((p) => (
                    <Field
                      key={p.name} param={p} value={args[p.name]}
                      onChange={(v) => setArg(p, v)}
                    />
                  ))}
                </div>
              )}
              {pick.needsSession && (
                <RunnerPicker runners={runners} selected={selected} setSelected={setSelected} refresh={refresh} allowed={pick.accountNames}
                  note={runner && runner.state !== "ready" ? "stopped - it will be started for this run and stopped after" : null} />
              )}
              <div className="row">
                <button className="primary" disabled={busy || blocked} onClick={run}>
                  {busy ? "Running..." : pick.writes ? "Run it" : "Fetch"}
                </button>
                {blocked && !runner && <span className="small err">pick a runner</span>}
                {pick.writes && !blocked && (
                  <span className="small muted">
                    This changes the account {runner?.uid ? `(${runner.uid})` : ""}.
                  </span>
                )}
              </div>
              {busy && progress.length > 0 && (
                <div className="mono small muted" style={{ marginTop: 10 }}>
                  {progress.map((e) => (
                    <div key={e.seq}>{e.data?.message}</div>
                  ))}
                </div>
              )}
            </>
          )}
        </div>

        {result && (
          <div className="card">
            <h2>
              {result.ok ? (
                <>
                  Result <span className="muted">{result.ms} ms</span>
                </>
              ) : (
                <span className="err">Failed</span>
              )}
            </h2>
            {!result.ok && <pre>{result.error}</pre>}
            {result.ok && (
              <>
                <div className="row" style={{ marginBottom: 10 }}>
                  <strong>{result.summary}</strong>
                  {result.rows?.length > 0 && <button onClick={csv}>Copy as CSV</button>}
                </div>
                {result.rows?.length > 0 && (
                  <div className="scroll">
                    <table>
                      <thead>
                        <tr>{result.columns.map((c) => <th key={c}>{c}</th>)}</tr>
                      </thead>
                      <tbody>
                        {result.rows.map((r, i) => (
                          <tr key={i}>
                            {r.map((v, j) => (
                              <td key={j} className={v !== "" && v !== null && !isNaN(v) ? "num" : ""}>{v}</td>
                            ))}
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
                {(result.tables || []).map((t) => <ResultTable key={t.title} table={t} />)}
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
