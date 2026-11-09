import { useEffect, useMemo, useRef, useState } from "react";
import RunnerPicker from "./RunnerPicker.jsx";

// Classic hex + ASCII dump, 16 bytes to a line.
function dump(hex) {
  const bytes = hex.match(/../g) || [];
  const lines = [];
  for (let i = 0; i < bytes.length; i += 16) {
    const chunk = bytes.slice(i, i + 16);
    const cols = chunk.join(" ").padEnd(47, " ");
    const text = chunk
      .map((b) => parseInt(b, 16))
      .map((c) => (c >= 32 && c < 127 ? String.fromCharCode(c) : "."))
      .join("");
    lines.push(`${i.toString(16).padStart(6, "0")}  ${cols}  ${text}`);
  }
  return lines.join("\n");
}

export default function Wire({ events, runner, runners, selected, setSelected, refresh }) {
  const [view, setView] = useState("json");
  const [dir, setDir] = useState("all");
  const [filter, setFilter] = useState("");
  const [paused, setPaused] = useState(false);
  const [held, setHeld] = useState([]);
  const [pick, setPick] = useState(null);
  const bottom = useRef(null);

  // Pausing freezes the list without dropping the stream: events keep arriving in
  // App, this just stops taking new snapshots of them.
  useEffect(() => { if (!paused) setHeld(events); }, [events, paused]);

  const rows = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return held
      .filter((e) => e.kind === "frame" || e.kind === "push")
      .filter((e) => dir === "all" || e.data?.dir === dir || (dir === "rx" && e.kind === "push"))
      .filter((e) => !f || (e.data?.command || "").toLowerCase().includes(f))
      .slice(-800);
  }, [held, dir, filter]);

  useEffect(() => {
    if (!paused) bottom.current?.scrollIntoView({ block: "end" });
  }, [rows.length, paused]);

  const tx = rows.filter((r) => r.data?.dir === "tx").length;

  return (
    <>
    <RunnerPicker runners={runners} selected={selected} setSelected={setSelected} refresh={refresh} controls />
    <div className="cols" style={{ gridTemplateColumns: "1fr 420px" }}>
      <div className="card">
        <h2>
          Raw TCP - {rows.length} frames <span className="muted">({tx} out, {rows.length - tx} in)</span>
        </h2>
        <div className="row" style={{ marginBottom: 10 }}>
          <select value={dir} onChange={(e) => setDir(e.target.value)} style={{ width: 130 }}>
            <option value="all">both ways</option>
            <option value="tx">outbound</option>
            <option value="rx">inbound</option>
          </select>
          <input placeholder="filter by command" value={filter} onChange={(e) => setFilter(e.target.value)} />
          <button onClick={() => setPaused(!paused)} className={paused ? "primary" : ""}>
            {paused ? "Resume" : "Pause"}
          </button>
        </div>
        {runner?.state !== "ready" && (
          <p className="muted small">Runner is {runner?.state || "not selected"} - frames appear once it is running.</p>
        )}
        <div className="scroll tall">
          <table>
            <thead>
              <tr>
                <th style={{ width: 92 }}>time</th>
                <th style={{ width: 44 }}>dir</th>
                <th>command</th>
                <th style={{ width: 70 }}>c / a</th>
                <th className="num" style={{ width: 64 }}>bytes</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((e) => (
                <tr key={e.seq} onClick={() => setPick(e)} style={{ cursor: "pointer" }}>
                  <td className="mono muted">{new Date(e.at).toLocaleTimeString()}</td>
                  <td>
                    <span className={`pill ${e.data?.dir || "rx"}`}>{e.data?.dir || "push"}</span>
                  </td>
                  <td className="mono">{e.data?.command || <span className="muted">system</span>}</td>
                  <td className="mono muted">{e.data?.controller}/{e.data?.action}</td>
                  <td className="num">{e.data?.bytes ?? ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <div ref={bottom} />
        </div>
      </div>

      <div className="card">
        <div className="row" style={{ justifyContent: "space-between" }}>
          <h2 style={{ margin: 0 }}>Frame</h2>
          <div className="row" style={{ gap: 4 }}>
            <button className={view === "json" ? "primary" : ""} onClick={() => setView("json")}>
              Decoded
            </button>
            <button className={view === "hex" ? "primary" : ""} onClick={() => setView("hex")}>
              Bytes
            </button>
          </div>
        </div>
        {!pick && <p className="muted">Click a frame to see what it carried.</p>}
        {pick && (
          <>
            <div className="mono small muted" style={{ marginBottom: 8 }}>
              {new Date(pick.at).toISOString()} - {pick.data?.dir || "push"} -{" "}
              controller {pick.data?.controller}, action {pick.data?.action}
              {pick.data?.truncated && " - truncated at 4 KB"}
            </div>
            <div className="scroll tall">
              <pre>
                {view === "hex"
                  ? dump(pick.data?.hex || "")
                  : pick.data?.params
                    ? JSON.stringify(pick.data.params, null, 2)
                    : "(no payload - a system frame such as the heartbeat)"}
              </pre>
            </div>
          </>
        )}
      </div>
    </div>
    </>
  );
}
