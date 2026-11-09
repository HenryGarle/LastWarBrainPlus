import { useState } from "react";
import { int } from "../explorer/format.js";
import { useJson } from "../explorer/PlayerTabs.jsx";
import { Level, ViewPicker, inView, defaultView } from "../explorer/ServersPage.jsx";

// Admin: how much each server is tracked, in bulk. Levels drive the scheduled jobs through their
// "servers" argument: @full (activity pulse, rosters, map refresh), @basic (season zones); every
// server is in the registry job regardless. "Shown" = listed by default on the viewer pages.
const put = (servers, level, visible) => fetch("/api/servers/track", {
  method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ servers, level, visible }),
}).then((r) => r.json());

const compact = (ids) => {
  const s = [...ids].sort((a, b) => a - b);
  const out = [];
  for (let i = 0; i < s.length;) { let j = i; while (j + 1 < s.length && s[j + 1] === s[j] + 1) j++; out.push(j === i ? `${s[i]}` : `${s[i]}-${s[j]}`); i = j + 1; }
  return out.join(",");
};

export default function Tracking() {
  const [nonce, setNonce] = useState(0);
  const d = useJson(`/api/x/servers?n=${nonce}`);
  const [view, setView] = useState("");
  const [spec, setSpec] = useState("");
  const [msg, setMsg] = useState("");
  if (!d) return <p className="muted">Loading…</p>;
  if (d.error) return <p className="err">{d.error}</p>;
  const v = view || defaultView(d);
  const servers = d.servers.filter((s) => s.type === 0);
  const rows = servers.filter((s) => inView(v, s, d));
  const apply = async (target, level, visible) => {
    if (!target) return;
    const r = await put(target, level, visible);
    setMsg(r.changed != null ? `${r.changed} servers updated` : r.error || "failed");
    setNonce((n) => n + 1);
  };
  const count = (lvl) => servers.filter((s) => s.level === lvl).length;
  // War groups (fight together, 32 for us) then the 64-server event groups around them.
  const blocks = [
    ...(d.warGroups || []).filter((g) => g.size >= 8).map((g) => ({ ...g, kind: "war", key: `war:${g.id}`, target: compact(g.servers), seasons: [], pairedWith: [] })),
    ...d.blocks.filter((b) => b.hi - b.lo < 400).map((b) => ({ ...b, kind: "event", key: `event:${b.id}`, target: b.id })),
  ].sort((a, b) => a.lo - b.lo || (a.kind === "war" ? -1 : 1));
  return (
    <div className="grid" style={{ maxWidth: 1100 }}>
      <div className="card">
        <h2>Tracking levels</h2>
        <p className="small">
          <Level v="full" /> {int(count("full"))} servers - everything: map scans, hourly profile checks, alliance rosters (jobs using <span className="mono">@full</span>).<br />
          <Level v="basic" /> {int(count("basic"))} - season zone standing and war side daily (<span className="mono">@basic</span> covers basic and full).<br />
          <Level v="off" /> the rest - registry only: in the server list, open date, king and badge, read daily for every server.
        </p>
        <p className="small muted">Shown = listed by default on the viewer pages (Servers). Change the job periods on the Scheduled page.</p>
      </div>

      <div className="card">
        <h2>Set many at once</h2>
        <div className="row small" style={{ gap: 8, flexWrap: "wrap" }}>
          <input value={spec} onChange={(e) => setSpec(e.target.value)} placeholder="servers: 1957-2020 or 1967,1970" style={{ width: 240 }} />
          {["full", "basic", "off"].map((l) => <button key={l} onClick={() => apply(spec, l, null)}>set {l === "off" ? "registry" : l}</button>)}
          <button onClick={() => apply(spec, null, true)}>show</button>
          <button onClick={() => apply(spec, null, false)}>hide</button>
          {msg && <span className="muted">{msg}</span>}
        </div>
      </div>

      <div className="card">
        <h2>Game groups</h2>
        <table className="small">
          <thead><tr><th>group</th><th>kind</th><th>servers</th><th>full</th><th>basic</th><th>shown</th><th>seasons</th><th>matched against</th><th>set whole group</th></tr></thead>
          <tbody>
            {blocks.map((b) => {
              const inB = servers.filter((s) => inView(b.key, s, d));
              return (
                <tr key={b.key}>
                  <td><a href="#" onClick={(e) => { e.preventDefault(); setView(b.key); }}>{b.id}</a></td>
                  <td className="muted">{b.kind === "war" ? "war group" : "event group"}</td>
                  <td className="mono">{inB.length}</td>
                  <td className="mono">{inB.filter((s) => s.level === "full").length || ""}</td>
                  <td className="mono">{inB.filter((s) => s.level === "basic").length || ""}</td>
                  <td className="mono">{inB.filter((s) => s.visible).length || ""}</td>
                  <td>{b.seasons.map((s) => `S${s}`).join(" ")}</td>
                  <td className="muted">{b.pairedWith.join(", ")}</td>
                  <td style={{ whiteSpace: "nowrap" }}>
                    {["full", "basic", "off"].map((l) => <button key={l} className="small" onClick={() => apply(b.target, l, null)}>{l === "off" ? "registry" : l}</button>)}
                    <button className="small" onClick={() => apply(b.target, null, true)}>show</button>
                    <button className="small" onClick={() => apply(b.target, null, false)}>hide</button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <div className="card">
        <div className="row small" style={{ gap: 8 }}>
          <h2 style={{ margin: 0 }}>Servers</h2>
          <ViewPicker d={d} view={v} setView={setView} />
          <span className="muted">{rows.length} servers</span>
          {rows.length > 0 && <>
            {["full", "basic", "off"].map((l) => <button key={l} onClick={() => apply(compact(rows.map((r) => r.id)), l, null)}>all {l === "off" ? "registry" : l}</button>)}
          </>}
        </div>
        <table className="small">
          <thead><tr><th>server</th><th>level</th><th>shown</th><th>president</th></tr></thead>
          <tbody>
            {rows.slice(0, 500).map((s) => (
              <tr key={s.id}>
                <td><a href={`#/x/server/${s.id}`}>#{s.id}</a></td>
                <td>
                  <select value={s.level} onChange={(e) => apply(String(s.id), e.target.value, null)}>
                    <option value="full">full</option><option value="basic">basic</option><option value="off">registry</option>
                  </select>
                </td>
                <td><input type="checkbox" style={{ width: "auto" }} checked={s.visible} onChange={(e) => apply(String(s.id), null, e.target.checked)} /></td>
                <td className="muted">{s.kingTag ? `[${s.kingTag}] ` : ""}{s.kingName}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
