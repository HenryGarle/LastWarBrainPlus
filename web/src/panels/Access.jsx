import { Fragment, useCallback, useEffect, useState } from "react";
import { when, ago } from "../explorer/format.js";

const send = (url, method = "GET", body) =>
  fetch(url, { method, headers: { "content-type": "application/json" }, body: body ? JSON.stringify(body) : undefined })
    .then(async (r) => { const j = await r.json().catch(() => ({})); if (!r.ok) throw new Error(j.error || r.statusText); return j; });

/** The whole-page sign-in: one password field. Shown whenever /api/auth/me says to sign in. */
export function SignIn({ onDone }) {
  const [password, setPassword] = useState("");
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);
  const submit = async (e) => {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await send("/api/auth/login", "POST", { password: password.trim() }); onDone(); }
    catch (x) { setError(x.message); }
    finally { setBusy(false); }
  };
  return (
    <div className="signin">
      <div className="signin-grid" aria-hidden="true" />
      <div className="signin-glow" aria-hidden="true" />
      <form className="signin-box" onSubmit={submit}>
        <div className="signin-mark" aria-hidden="true">◆</div>
        <h1>Last War <b>Brain</b></h1>
        <input id="pw" type="password" aria-label="Password" placeholder="Password" autoFocus autoComplete="current-password"
          value={password} onChange={(e) => setPassword(e.target.value)} />
        {error && <p className="err small">{error}</p>}
        <button className="primary" disabled={busy || !password.trim()}>{busy ? "Signing in…" : "Sign in"}</button>
      </form>
    </div>
  );
}

// A new or reset password is shown once, here, and never again.
function Shown({ shown, onClose }) {
  if (!shown) return null;
  return (
    <div className="card hero-card">
      <h2>Password for {shown.name}</h2>
      <p className="mono" style={{ fontSize: 18, userSelect: "all" }}>{shown.password}</p>
      <p className="muted small">Copy it now - it is not stored anywhere readable and will not be shown again.</p>
      <div className="row" style={{ gap: 8 }}>
        <button onClick={() => navigator.clipboard?.writeText(shown.password)}>Copy</button>
        <button onClick={onClose}>Done</button>
      </div>
    </div>
  );
}

/** Admin tab: every password (key), who uses it and how, and create / reset / disable / delete. */
export default function Access() {
  const [keys, setKeys] = useState(null);
  const [error, setError] = useState(null);
  const [name, setName] = useState("");
  const [role, setRole] = useState("viewer");
  const [shown, setShown] = useState(null);
  const [open, setOpen] = useState(null);
  const load = useCallback(() => send("/api/access").then(setKeys).catch((e) => setError(e.message)), []);
  useEffect(() => { load(); }, [load]);
  const act = (p) => p.then(load).catch((e) => setError(e.message));

  const create = (e) => {
    e.preventDefault();
    act(send("/api/access", "POST", { name, role }).then((r) => { setShown({ name, password: r.password }); setName(""); }));
  };
  const reset = (k) => window.confirm(`New password for ${k.name}? Everyone signed in with the old one is signed out.`)
    && act(send(`/api/access/${k.id}/reset`, "POST").then((r) => setShown({ name: k.name, password: r.password })));
  const remove = (k) => window.confirm(`Delete ${k.name}? Anyone using it is signed out for good.`) && act(send(`/api/access/${k.id}`, "DELETE"));

  return (
    <div className="grid">
      <Shown shown={shown} onClose={() => setShown(null)} />
      <div className="card">
        <h2>New password</h2>
        <form className="row" style={{ gap: 8, flexWrap: "wrap" }} onSubmit={create}>
          <input placeholder="who it is for, e.g. SaFa R4s" value={name} onChange={(e) => setName(e.target.value)} style={{ maxWidth: 280 }} />
          <select value={role} onChange={(e) => setRole(e.target.value)} style={{ width: "auto" }}>
            <option value="viewer">viewer - Explorer and chat only</option>
            <option value="viewerplus">viewer plus - also Tracking and battle reports (read only)</option>
            <option value="admin">admin - everything</option>
          </select>
          <button className="primary" disabled={!name.trim()}>Create</button>
        </form>
      </div>
      {error && <p className="err">{error}</p>}
      <div className="card">
        <h2>Passwords</h2>
        {!keys ? <p className="muted">Loading…</p> : (
          <div className="scroll">
            <table>
              <thead>
                <tr><th>name</th><th>role</th><th className="num">sign-ins</th><th className="num">requests</th><th>last seen</th><th>last address</th><th>browsers</th><th></th></tr>
              </thead>
              <tbody>
                {keys.map((k) => (
                  <Fragment key={k.id}>
                    <tr className={k.disabled ? "muted" : ""}>
                      <td><a href="#" onClick={(e) => { e.preventDefault(); setOpen(open === k.id ? null : k.id); }}>{k.name}</a>{k.disabled && <span className="pill">disabled</span>}</td>
                      <td>
                        {k.passwordless ? k.role : (
                          <select value={k.role} style={{ width: "auto" }} onChange={(e) => act(send(`/api/access/${k.id}`, "PUT", { role: e.target.value }))}>
                            <option value="viewer">viewer</option><option value="viewerplus">viewer plus</option><option value="admin">admin</option>
                          </select>
                        )}
                      </td>
                      <td className="num">{k.logins}</td>
                      <td className="num">{k.requests}</td>
                      <td title={k.lastSeenAt ? when(k.lastSeenAt) : ""}>{k.lastSeenAt ? ago(k.lastSeenAt) : <span className="muted">never</span>}</td>
                      <td className="mono small">{k.lastIp}</td>
                      <td className="num">{k.sessions?.length || 0}</td>
                      <td className="row" style={{ gap: 4 }}>
                        {!k.passwordless && <>
                          <button onClick={() => reset(k)}>New password</button>
                          <button onClick={() => act(send(`/api/access/${k.id}`, "PUT", { disabled: !k.disabled }))}>{k.disabled ? "Enable" : "Disable"}</button>
                          <button className="danger" onClick={() => remove(k)}>Delete</button>
                        </>}
                      </td>
                    </tr>
                    {open === k.id && (
                      <tr>
                        <td colSpan={8}>
                          <div className="two">
                            <div>
                              <h3 className="small muted">Used for (all time · last 7 days)</h3>
                              {(k.areas || []).map((a) => <div key={a.area} className="small row" style={{ justifyContent: "space-between" }}><span>{a.area}</span><span>{a.n} · {a.week}</span></div>)}
                              {!(k.areas || []).length && <p className="muted small">Nothing yet.</p>}
                            </div>
                            <div>
                              <h3 className="small muted">Signed-in browsers</h3>
                              {(k.sessions || []).map((s, i) => <div key={i} className="small">{ago(s.lastSeenAt)} · <span className="mono">{s.ip}</span> · <span className="muted">{String(s.agent).slice(0, 70)}</span></div>)}
                              {!(k.sessions || []).length && <p className="muted small">None.</p>}
                            </div>
                          </div>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="muted small">"Local development" is this machine in dev mode - it has no password and cannot be used from anywhere else.</p>
      </div>
    </div>
  );
}
