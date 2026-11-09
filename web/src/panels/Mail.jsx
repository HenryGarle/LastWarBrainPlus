import { useEffect, useMemo, useState } from "react";
import { api } from "../api.js";
import { ago } from "../explorer/format.js";
import { useHashParam, setTitle } from "../nav.js";
import RunnerPicker from "./RunnerPicker.jsx";

// The picked runner's mailbox: titles and bodies resolved through the game's own strings,
// attachments listed, one Claim button per mail. #/mail?type=<type name> filters.
export default function Mail({ runners, runner, selected, setSelected, refresh }) {
  const [data, setData] = useState(null);
  const [err, setErr] = useState(null);
  const [busy, setBusy] = useState(false);
  const [open, setOpen] = useState({});
  const [done, setDone] = useState({}); // uid -> claim/read outcome text
  const [type, setType] = useHashParam("type", "");
  const ready = runner?.state === "ready";

  useEffect(() => { setTitle("Mail"); }, []);
  const load = async () => {
    if (!runner) return;
    setBusy(true); setErr(null);
    try { setData(await api.mail(runner.id)); } catch (e) { setErr(e.message); } finally { setBusy(false); }
  };
  useEffect(() => { setData(null); setOpen({}); setDone({}); if (ready) load(); }, [runner?.id, ready]);

  const mail = data?.mail || [];
  const types = useMemo(() => [...new Set(mail.map((m) => m.typeName))].sort(), [mail]);
  const shown = type ? mail.filter((m) => m.typeName === type) : mail;
  const unclaimed = mail.filter((m) => m.rewardState === "unclaimed" && !done[m.uid]).length;

  const privileged = runner?.tier === "privileged";
  const claim = async (m) => {
    if (privileged && !window.confirm(`Claim "${m.title}" on ${runner.name || runner.id} (privileged account)?`)) return;
    setDone((d) => ({ ...d, [m.uid]: "claiming..." }));
    try { const r = await api.mailClaim(runner.id, m.uid); const o = r.outcome === "ok" ? "claimed" : /^E100147/.test(r.outcome) ? "already claimed" : r.outcome; setDone((d) => ({ ...d, [m.uid]: o })); }
    catch (e) { setDone((d) => ({ ...d, [m.uid]: `failed: ${e.message}` })); }
  };
  const markRead = async (m) => {
    try { await api.mailRead(runner.id, [m.uid]); m.read = true; setDone((d) => ({ ...d })); }
    catch (e) { setDone((d) => ({ ...d, [m.uid]: `failed: ${e.message}` })); }
  };

  return (
    <div>
      <RunnerPicker runners={runners} selected={selected} setSelected={setSelected} refresh={refresh} controls />
      <div className="row small" style={{ gap: 8, marginBottom: 10, alignItems: "center" }}>
        <button onClick={load} disabled={!ready || busy}>{busy ? "Loading..." : "Reload"}</button>
        <select value={type} onChange={(e) => setType(e.target.value)}>
          <option value="">All types ({mail.length})</option>
          {types.map((t) => <option key={t} value={t}>{t} ({mail.filter((m) => m.typeName === t).length})</option>)}
        </select>
        {data && <span className="muted">{unclaimed} unclaimed{data.truncated ? " · more mail than fetched" : ""}{!data.textAvailable ? " · game text not extracted (run assets.extract)" : ""}</span>}
        {!ready && <span className="muted">Start the runner to read its mail.</span>}
        {ready && <span className="muted" title="The list the server sends keeps showing a claimed reward as unclaimed until the next login; Claim says so if it was already taken.">Claim state can lag a claim until next login.</span>}
      </div>
      {err && <p className="err">{err}</p>}
      {data && (
        <table className="mail">
          <thead><tr><th></th><th>Type</th><th>Title</th><th>From</th><th>Sent</th><th>Attachments</th><th></th></tr></thead>
          <tbody>
            {shown.map((m) => {
              const isOpen = open[m.uid];
              return [
                <tr key={m.uid} className={m.read ? "" : "unread"} onClick={() => setOpen((o) => ({ ...o, [m.uid]: !o[m.uid] }))} style={{ cursor: "pointer" }}>
                  <td>{isOpen ? "▾" : "▸"}</td>
                  <td className="small" title={`mail type ${m.type}`}>{m.typeName}</td>
                  <td>{m.title}</td>
                  <td>{m.from}</td>
                  <td className="small" title={m.sent}>{m.sent ? ago(m.sent) : ""}</td>
                  <td className="small">{m.rewards.length ? m.rewards.join(", ") : <span className="muted">none</span>}</td>
                  <td onClick={(e) => e.stopPropagation()}>
                    {done[m.uid] ? <span className="small muted">{done[m.uid]}</span>
                      : m.rewardState === "unclaimed" ? <button className="small" onClick={() => claim(m)}>Claim</button>
                      : m.rewardState === "claimed" ? <span className="small muted">claimed</span> : null}
                  </td>
                </tr>,
                isOpen && (
                  <tr key={m.uid + ":body"} className="mail-body">
                    <td></td>
                    <td colSpan={6}>
                      <div style={{ whiteSpace: "pre-wrap" }}>{m.content || <span className="muted">(no text)</span>}</div>
                      {m.rewards.length > 0 && <div style={{ marginTop: 6 }}><b>Attached:</b> {m.rewards.join(" · ")}</div>}
                      <div className="small muted" style={{ marginTop: 6 }}>
                        uid {m.uid}{m.expires ? ` · expires ${new Date(m.expires).toLocaleString()}` : ""}
                        {!m.read && <> · <a href="#" onClick={(e) => { e.preventDefault(); markRead(m); }}>mark read</a></>}
                      </div>
                      <details><summary className="small muted">raw</summary><pre className="small">{JSON.stringify(m.raw, null, 2)}</pre></details>
                    </td>
                  </tr>
                ),
              ];
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}
