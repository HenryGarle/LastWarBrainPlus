import { useEffect, useMemo, useRef, useState } from "react";
import { api } from "../api.js";
import RunnerPicker from "./RunnerPicker.jsx";
import { sectionArgs } from "../nav.js";

// The catalogue records each parameter as "Type:name", which is enough to offer a
// skeleton payload instead of making the user remember the shape.
const SAMPLE = {
  int: 0, long: 0, short: 0, byte: 0, float: 0, double: 0,
  bool: false, boolean: false, string: "", utfstring: "",
};

function skeleton(params) {
  if (!params) return "{}";
  const body = {};
  for (const part of params.split(",")) {
    const [type, name] = part.includes(":") ? part.split(":") : ["string", part];
    if (!name) continue;
    const t = type.trim().toLowerCase();
    body[name.trim()] = t.endsWith("[]") || t.endsWith("array") ? [] : SAMPLE[t] ?? "";
  }
  return JSON.stringify(body, null, 2);
}

export default function Commands({ runner, runners, selected, setSelected, refresh, events }) {
  // #/commands/<command> opens that command (searched for, then picked when the list arrives).
  const wanted = useRef(sectionArgs()[0] || null);
  const [q, setQ] = useState(() => wanted.current || "");
  const [list, setList] = useState([]);
  const [total, setTotal] = useState(0);
  const [pick, setPick] = useState(null);
  const [body, setBody] = useState("{}");
  const [timeout_, setTimeout_] = useState(8);
  const [result, setResult] = useState(null);
  const [busy, setBusy] = useState(false);
  const [confirmed, setConfirmed] = useState(false);
  const privileged = runner?.tier === "privileged";

  useEffect(() => { api.commandCount().then((r) => setTotal(r.total)).catch(() => {}); }, []);

  useEffect(() => {
    let live = true;
    const t = setTimeout(
      () => api.commands(q).then((r) => {
        if (!live) return;
        setList(r);
        const hit = wanted.current && r.find((e) => e.command === wanted.current);
        if (hit) { wanted.current = null; choose(hit); }
      }).catch(() => {}),
      150
    );
    return () => { live = false; clearTimeout(t); };
  }, [q]);

  function choose(e) {
    window.history.replaceState(null, "", `#/commands/${encodeURIComponent(e.command)}`);
    setPick(e);
    setBody(skeleton(e.params));
    setResult(null);
  }

  const run = async () => {
    if (!pick || !selected) return;
    setBusy(true);
    setResult(null);
    try {
      let params = null;
      const text = body.trim();
      if (text && text !== "{}") params = JSON.parse(text);
      setResult(await api.call(selected, pick.command, params, Number(timeout_) || 8, privileged ? confirmed : undefined));
      setConfirmed(false);   // one confirmation per call on a privileged account
    } catch (e) {
      setResult({ ok: false, error: e.message });
    } finally {
      setBusy(false);
    }
  };

  const history = useMemo(
    () => events.filter((e) => e.kind === "reply" || (e.kind === "error" && e.data?.command)).slice(-10).reverse(),
    [events]
  );

  const ready = runner?.state === "ready";

  return (
    <>
    <RunnerPicker runners={runners} selected={selected} setSelected={setSelected} refresh={refresh} controls />
    <div className="cols">
      <div className="card">
        <h2>Commands {total > 0 && <span className="muted">({total})</span>}</h2>
        <input placeholder="filter by command, message or module" value={q} onChange={(e) => setQ(e.target.value)} />
        <div className="list" style={{ marginTop: 8 }}>
          {list.map((e) => (
            <button
              key={e.command}
              className={pick?.command === e.command ? "on" : ""}
              onClick={() => choose(e)}
            >
              <div className="mono">{e.command}</div>
              <div className="muted small">
                {e.msgName}
                {e.nParams > 0 ? ` - ${e.nParams} param${e.nParams > 1 ? "s" : ""}` : ""}
              </div>
            </button>
          ))}
          {list.length === 0 && <p className="muted small">No match.</p>}
        </div>
      </div>

      <div className="grid">
        <div className="card">
          <h2>Trigger</h2>
          {!pick && <p className="muted">Pick a command on the left.</p>}
          {pick && (
            <>
              <div className="mono" style={{ fontSize: 14 }}>{pick.command}</div>
              <div className="muted small" style={{ marginBottom: 10 }}>
                {pick.module || "no module"}
                {pick.params ? ` - ${pick.params}` : " - no parameters"}
              </div>
              <textarea rows={8} value={body} onChange={(e) => setBody(e.target.value)} spellCheck={false} />
              <div className="row" style={{ marginTop: 10 }}>
                <button className="primary" disabled={!ready || busy || (privileged && !confirmed)} onClick={run}>
                  {busy ? "Waiting..." : "Send"}
                </button>
                <label className="row small muted" style={{ gap: 6, width: "auto" }}>
                  timeout
                  <input
                    type="number" min="1" max="120" value={timeout_}
                    onChange={(e) => setTimeout_(e.target.value)} style={{ width: 70 }}
                  />
                  s
                </label>
                {!ready && <span className="small err">runner not running</span>}
              </div>
              {privileged && (
                <label className="row small err" style={{ gap: 6, width: "auto", marginTop: 8 }}>
                  <input type="checkbox" style={{ width: "auto" }} checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
                  This is a privileged (real) account - send this one command on it
                </label>
              )}
            </>
          )}
        </div>

        {result && (
          <div className="card">
            <h2>
              Reply{" "}
              {result.ok ? (
                <span className="muted">{result.ms} ms</span>
              ) : (
                <span className="err">failed</span>
              )}
            </h2>
            <div className="scroll">
              <pre>{result.ok ? JSON.stringify(result.result, null, 2) : result.error}</pre>
            </div>
          </div>
        )}

        <div className="card">
          <h2>Recent calls</h2>
          <table>
            <tbody>
              {history.map((e) => (
                <tr key={e.seq}>
                  <td className="mono muted" style={{ width: 90 }}>
                    {new Date(e.at).toLocaleTimeString()}
                  </td>
                  <td className="mono">{e.data?.command}</td>
                  <td className={e.kind === "error" ? "err" : "muted"}>
                    {e.kind === "error" ? e.data?.message : `${e.data?.ms} ms, ${e.data?.keys} keys`}
                  </td>
                </tr>
              ))}
              {history.length === 0 && (
                <tr><td className="muted">Nothing sent yet.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
    </>
  );
}
