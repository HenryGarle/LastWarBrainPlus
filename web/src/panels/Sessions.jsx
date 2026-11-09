import { useState } from "react";
import { api } from "../api.js";

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

export default function Sessions({ runners, refresh, setSelected }) {
  const [guestName, setGuestName] = useState("");
  const [guestZone, setGuestZone] = useState("");
  const [busy, setBusy] = useState("");
  const [note, setNote] = useState(null);
  const [error, setError] = useState(null);

  // email sign-in walks: form -> code -> role
  const [step, setStep] = useState("form");
  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  const [ios, setIos] = useState(true);   // must match the phone the account was made on
  const [code, setCode] = useState("");
  const [pendingId, setPendingId] = useState(null);
  const [roles, setRoles] = useState([]);
  const [sweep, setSweep] = useState(null);   // cleanup preview: what would go, before anything does
  const [moving, setMoving] = useState("");     // export / import JSON, to carry a session between copies of the site

  const exportSession = (id) =>
    guard("exp" + id, async () => {
      const res = await fetch(`/api/sessions/${encodeURIComponent(id)}/export`);
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(data.error || res.statusText);
      const text = JSON.stringify(data, null, 2);
      setMoving(text);
      try { await navigator.clipboard.writeText(text); setNote(`${id} exported and copied - paste it into Import on the other site`); }
      catch { setNote(`${id} exported - copy it from the box below`); }
    });

  const importSession = () =>
    guard("imp", async () => {
      let body;
      try { body = JSON.parse(moving); } catch { throw new Error("that is not valid JSON - paste the whole export"); }
      const r = await post("/api/sessions/import", body);
      setNote(`imported ${r.id} - anything already stored under that name was kept as .bak files`);
      setMoving("");
      setSelected(r.id);
    });

  const [lastStep, setLastStep] = useState("");
  const guard = async (tag, fn) => {
    setLastStep(tag);
    setBusy(tag);
    setError(null);
    setNote(null);
    try {
      await fn();
    } catch (e) {
      setError(e.message);
    } finally {
      setBusy("");
      await refresh();
    }
  };

  const makeGuest = () =>
    guard("guest", async () => {
      const r = await post("/api/sessions/guest", {
        name: guestName || `guest_${Date.now().toString(36)}`,
        zone: guestZone || null,
      });
      setNote(`guest session "${r.id}" created - ${r.note}`);
      setGuestName("");
      setSelected(r.id);
    });

  const sendCode = () =>
    guard("send", async () => {
      const r = await post("/api/sessions/email/start", { name: name || email.split("@")[0], email, ios });
      setPendingId(r.pendingId);
      setStep("code");
      setNote(`code sent to ${email}. It expires in about ${r.resendAfter || 60}s.`);
    });

  const verify = () =>
    guard("verify", async () => {
      const r = await post("/api/sessions/email/verify", { pendingId, code });
      setRoles(r.roles);
      setStep("role");
      setNote(`${r.roles.length} role${r.roles.length === 1 ? "" : "s"} on this account.`);
    });

  const saveRole = (index) =>
    guard("save", async () => {
      const r = await post("/api/sessions/email/save", { pendingId, roleIndex: index, name: name || null });
      setNote(`session "${r.id}" stored - ${r.zone}, uid ${r.gameUid}`);
      setSelected(r.id);
      setStep("form");
      setCode("");
      setPendingId(null);
      setRoles([]);
    });

  const remove = (id, files) =>
    guard("del" + id, async () => {
      const res = await fetch(`/api/sessions/${id}?files=${files}`, { method: "DELETE" });
      if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error || res.statusText);
      setNote(`removed ${id}`);
    });

  // Two steps on purpose: list first, delete only what was shown.
  const previewCleanup = () =>
    guard("sweep", async () => setSweep(await post("/api/sessions/cleanup?apply=false", {})));
  const applyCleanup = () =>
    guard("sweep", async () => {
      const r = await post("/api/sessions/cleanup?apply=true", {});
      setSweep(null);
      setNote(`removed ${r.items.length} unused guest session${r.items.length === 1 ? "" : "s"}`);
    });

  return (
    <div className="cols sessions-cols">
      <div className="grid">
        <div className="card">
          <h2>Stored sessions</h2>
          {error && <p className="err small">{error}</p>}
          {note && <p className="small" style={{ color: "var(--ok)" }}>{note}</p>}
          <div style={{ overflowX: "auto" }}>
          <table>
            <thead>
              <tr><th>session</th><th>kind</th><th title="What this account may be used for">tier</th><th>state</th><th /></tr>
            </thead>
            <tbody>
              {runners.map((r) => (
                <tr key={r.id}>
                  <td>
                    <button
                      style={{ background: "none", border: "none", padding: 0, textAlign: "left" }}
                      onClick={() => setSelected(r.id)}
                    >
                      <strong>{r.name}</strong>
                    </button>
                    <div className="muted small">{r.note}</div>
                  </td>
                  <td className="small">{r.kind}</td>
                  <td className="small">
                    {r.kind === "guest" ? <span className="pill tier-guest">guest</span> : (
                      <select value={r.tier} style={{ width: "auto" }} disabled={busy === "tier" + r.id}
                        onChange={(e) => guard("tier" + r.id, async () => { await api.setTier(r.id, e.target.value); setNote(`${r.name} is now ${e.target.value}`); })}>
                        <option value="service">service</option>
                        <option value="privileged">privileged</option>
                      </select>
                    )}
                  </td>
                  <td className="small">
                    <span className={`dot ${r.state}`} /> {r.state}
                  </td>
                  <td style={{ whiteSpace: "nowrap" }}>
                    {r.kind !== "guest" && (
                      <button disabled={busy === "exp" + r.id} onClick={() => exportSession(r.id)} title="Copies this session as JSON, to import on the other copy of the site" style={{ marginRight: 6 }}>
                        Export
                      </button>
                    )}
                    <button
                      className="danger"
                      disabled={busy === "del" + r.id || r.state !== "stopped"}
                      onClick={() => remove(r.id, r.kind === "guest")}
                      title={r.kind === "guest" ? "removes the guest and its files" : "forgets it, keeps the files"}
                    >
                      Forget
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          </div>
          <p className="muted small" style={{ marginBottom: 0 }}>
            Tiers: <b>guest</b> - throwaway, cross-server reads; <b>service</b> - a work account (maps,
            applicants, the ministry queue, chores); <b>privileged</b> - your own main, used only by
            operations marked for it (its own dailies and alliance duties), never by scans or chat, and
            every raw command on it needs a tick first. Selecting a session switches every panel to it. Guests are single-use (a second login is
            refused), so a guest is deleted automatically when it is stopped, and an hourly sweep removes
            any unused guest over an hour old. Real accounts are never removed that way.
          </p>
          <div className="row" style={{ gap: 8, marginTop: 10, flexWrap: "wrap" }}>
            <button disabled={busy === "sweep"} onClick={previewCleanup}
              title="Lists guest sessions nothing is using. Real accounts (anything with a game uid) are never included.">
              Clean up unused guests…
            </button>
            {sweep && sweep.items.length > 0 && (
              <>
                <button className="danger" disabled={busy === "sweep"} onClick={applyCleanup}>
                  Delete these {sweep.items.length}
                </button>
                <button onClick={() => setSweep(null)}>Cancel</button>
              </>
            )}
          </div>
          {sweep && (
            sweep.items.length === 0
              ? <p className="muted small">Nothing to clean up.</p>
              : (
                <div className="scroll" style={{ maxHeight: 260, marginTop: 8 }}>
                  <table className="small">
                    <thead><tr><th>session</th><th>why it qualifies</th></tr></thead>
                    <tbody>
                      {sweep.items.map((i) => <tr key={i.folder}><td className="mono">{i.id}</td><td className="muted">{i.reason}</td></tr>)}
                    </tbody>
                  </table>
                  {sweep.kept.length > 0 && <p className="muted small">Kept because in use: {sweep.kept.join(", ")}</p>}
                </div>
              )
          )}
        </div>

        <div className="card">
          <h2>Move a session between sites</h2>
          <p className="muted small" style={{ marginTop: 0 }}>
            Export on the site where the account works, paste here on the other one, Import. The JSON holds the login token and device id -
            treat it like a password. An existing session with the same name is kept as .bak files, never overwritten. Log in on one site at a time:
            each login can sign the other out.
          </p>
          <textarea rows={6} style={{ width: "100%", fontFamily: "var(--mono, monospace)", fontSize: 11 }} value={moving}
            onChange={(e) => setMoving(e.target.value)} placeholder='{"format": "lastwar-session/1", ...}' />
          <div className="row" style={{ gap: 8, marginTop: 6 }}>
            <button className="primary" disabled={busy === "imp" || !moving.trim()} onClick={importSession}>Import</button>
            {moving && <button onClick={() => setMoving("")}>Clear</button>}
          </div>
        </div>

        <div className="card">
          <h2>New guest</h2>
          <div className="grid" style={{ gap: 8 }}>
            <input placeholder="name (optional)" value={guestName} onChange={(e) => setGuestName(e.target.value)} />
            <input placeholder="zone, e.g. APS1967 (optional)" value={guestZone} onChange={(e) => setGuestZone(e.target.value)} />
            <div className="row">
              <button className="primary" disabled={busy === "guest"} onClick={makeGuest}>
                {busy === "guest" ? "Creating..." : "Create guest"}
              </button>
              <span className="muted small">Fresh device identity, its own folder - never touches a real account.</span>
            </div>
          </div>
        </div>
      </div>

      <div className="card">
        <h2>Sign in with email</h2>
        {error && busy === "" && ["send", "verify", "save"].includes(lastStep) && <p className="err small">{error}</p>}

        {step === "form" && (
          <div className="grid" style={{ gap: 8 }}>
            <label className="small muted">session name</label>
            <input placeholder="what to call this account" value={name} onChange={(e) => setName(e.target.value)} />
            <label className="small muted">email address</label>
            <input
              type="email" placeholder="you@example.com" value={email}
              onChange={(e) => setEmail(e.target.value)} autoComplete="off"
            />
            <label className="small muted">account made on</label>
            <select value={ios ? "ios" : "android"} onChange={(e) => setIos(e.target.value === "ios")}>
              <option value="ios">iPhone / iPad</option>
              <option value="android">Android</option>
            </select>
            <div className="row">
              <button className="primary" disabled={busy === "send" || !email.includes("@")} onClick={sendCode}>
                {busy === "send" ? "Sending..." : "Email me a code"}
              </button>
            </div>
            <p className="muted small" style={{ margin: 0 }}>
              A throwaway guest connection carries the request, then the account binds to that
              new device. The code goes to your inbox - nothing is stored until you pick a role.
            </p>
          </div>
        )}

        {step === "code" && (
          <div className="grid" style={{ gap: 8 }}>
            <label className="small muted">the six-character code sent to {email}</label>
            <input
              value={code} onChange={(e) => setCode(e.target.value)} autoComplete="off"
              placeholder="000000" maxLength={12}
            />
            <div className="row">
              <button className="primary" disabled={busy === "verify" || code.trim().length < 6} onClick={verify}>
                {busy === "verify" ? "Checking..." : "Verify"}
              </button>
              <button onClick={() => { setStep("form"); setPendingId(null); }}>Cancel</button>
            </div>
          </div>
        )}

        {step === "role" && (
          <div className="grid" style={{ gap: 8 }}>
            <p className="muted small" style={{ margin: 0 }}>Pick the role to store a session for.</p>
            <table>
              <thead>
                <tr><th>name</th><th>server</th><th>alliance</th><th className="num">power</th><th /></tr>
              </thead>
              <tbody>
                {roles.map((r) => (
                  <tr key={r.index}>
                    <td>{r.name || <span className="muted">unnamed</span>}</td>
                    <td className="mono">{r.serverId || r.zone}</td>
                    <td>{r.alliance}</td>
                    <td className="num">{Number(r.power).toLocaleString()}</td>
                    <td>
                      <button className="primary" disabled={busy === "save"} onClick={() => saveRole(r.index)}>
                        Store
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <div className="row">
              <button onClick={() => { setStep("form"); setPendingId(null); setRoles([]); }}>Cancel</button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
