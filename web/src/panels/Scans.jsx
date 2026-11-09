import { useEffect, useMemo, useState } from "react";
import { big } from "../explorer/format.js";
import { PlayerLink, ServerLink } from "../explorer/bits.jsx";
import { SortTable } from "../explorer/charts.jsx";
import { sectionArgs } from "../nav.js";
import { SendPosition, usePrefs } from "./Account.jsx";
import MapPage from "../explorer/MapPage.jsx";
import { useJson } from "../explorer/PlayerTabs.jsx";

// Where a task is: the server's newest map scan, centred and zoomed on the task with a marker - to find it in the game
// by hand (Henry 2026-10-30: chat links are risky). Esc or the backdrop closes it.
function TaskMap({ server, x, y, label, onClose }) {
  const d = useJson(`/api/x/map/${server}`);
  useEffect(() => { const k = (e) => e.key === "Escape" && onClose(); window.addEventListener("keydown", k); return () => window.removeEventListener("keydown", k); }, [onClose]);
  return (
    <div onClick={onClose} style={{ position: "fixed", inset: 0, background: "rgba(0,0,0,0.6)", zIndex: 1000, display: "flex", alignItems: "center", justifyContent: "center" }}>
      <div className="card" onClick={(e) => e.stopPropagation()} style={{ width: "94vw", height: "88vh", display: "flex", flexDirection: "column", gap: 8, margin: 0 }}>
        <div className="row" style={{ justifyContent: "space-between", alignItems: "center", gap: 8 }}>
          <span><b style={{ fontSize: "1.4em" }} className="mono">#{server} X:{x} Y:{y}</b> <span className="muted small">{label}</span></span>
          <button onClick={onClose}>Close</button>
        </div>
        {!d ? <p className="muted">Loading map…</p>
          : d.error || !d.scans?.length ? <p className="muted">No map scan of #{server} yet - go to X:{x} Y:{y} in the game.</p>
          : <div className="task-map" style={{ flex: 1, minHeight: 0 }}><MapPage d={d} route={{ kind: "map", id: String(server), extra: `${x},${y}` }} base="#/x/map" /></div>}
      </div>
    </div>
  );
}

// Admin: live scans of a server's map for things worth acting on now. Nothing here is stored - each
// scan asks the game again (operation secret.tasks on the chosen account).

const RARITY_COLOUR = { SR: "#4c9aff", SSR: "#a371f7", UR: "#d4a72c" };
const keep = (k, v) => { try { if (v === undefined) return localStorage.getItem(k); localStorage.setItem(k, v); } catch { /* private window */ } return null; };
const dur = (s) => {
  const a = Math.abs(s), h = Math.floor(a / 3600), m = Math.floor((a % 3600) / 60);
  const t = h ? `${h}h ${m}m` : `${m}m`;
  return s >= 0 ? t : `done ${t} ago`;
};

// Runs an operation on one account. A stopped real account is logged in just for the run - ask first.
async function runOp(acct, operation, args) {
  if (acct.state !== "ready" && acct.kind !== "guest"
    && !window.confirm(`${acct.name} is not connected: it will be logged in (and stays connected about 8 minutes for the next scan or send), which signs out anyone playing it on a phone. Go?`)) return null;
  const r = await fetch(`/api/runners/${encodeURIComponent(acct.id)}/operation`, {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ operation, args }),
  });
  const j = await r.json().catch(() => ({}));
  if (!r.ok || j.ok === false) throw new Error(j.error || `${r.status}`);
  return j;
}
const rowsOf = (j) => (j.rows || []).map((cells) => Object.fromEntries(j.columns.map((c, i) => [c, cells[i]])));
// Debug: save what the game / server sent as a file, to compare with the game.
const download = (name, data) => {
  const a = document.createElement("a");
  a.href = URL.createObjectURL(new Blob([typeof data === "string" ? data : JSON.stringify(data, null, 2)], { type: "application/json" }));
  a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 1000);
};

export default function Scans({ runners = [] }) {
  const [args, setArgs] = useState(sectionArgs);   // follow the hash, so the sub-tabs switch
  useEffect(() => { const on = () => setArgs(sectionArgs()); window.addEventListener("hashchange", on); return () => window.removeEventListener("hashchange", on); }, []);
  const page = args[0] || "secret";
  return (
    <div className="grid">
      <nav className="subnav big">
        <a href="#/scans/secret" className={page === "secret" ? "on" : ""}>Secret Tasks</a>
        <a href="#/scans/trucks" className={page === "trucks" ? "on" : ""}>Trucks</a>
      </nav>
      {page === "trucks" ? <Trucks runners={runners} /> : <SecretTasks runners={runners} />}
    </div>
  );
}

function SecretTasks({ runners: all }) {
  // A real (service) account only: guests get nothing useful back (Henry, 2026-10-06). Never privileged.
  const runners = all.filter((r) => r.tier === "service");
  const prefs = usePrefs();
  const [account, setAccount] = useState(() => keep("scans.account") || "");
  const [server, setServer] = useState(() => keep("scans.server") || "1967");
  const [result, setResult] = useState(null);   // { at, server, summary, rows }
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState("");
  const [rarity, setRarity] = useState({ SR: true, SSR: true, UR: true });
  const [minStars, setMinStars] = useState(1);
  const [status, setStatus] = useState("running");
  const [open, setOpen] = useState(true);
  const [q, setQ] = useState("");
  const [now, setNow] = useState(Date.now());
  useEffect(() => { const t = setInterval(() => setNow(Date.now()), 15000); return () => clearInterval(t); }, []);
  const [rewards, setRewards] = useState({});   // pointId -> { busy, items, raw, err }
  const [debug, setDebug] = useState(false);
  const [mapAt, setMapAt] = useState(null);   // { x, y, label } - the task map dialog

  const picked = runners.find((r) => r.id === account) || runners.find((r) => r.state === "ready") || runners[0];

  const scan = async () => {
    if (!picked) return;
    setBusy(true); setErr("");
    keep("scans.account", picked.id); keep("scans.server", String(server));
    try {
      const j = await runOp(picked, "secret.tasks", { server: String(server) });
      if (!j) { setBusy(false); return; }
      const rows = rowsOf(j)
        .map((t) => ({ ...t, x: +t.x, y: +t.y, rank: +t.rank, pointId: +t.pointId, stars: +t.stars, secondsLeft: t.secondsLeft === "" ? null : +t.secondsLeft, heroPower: t.heroPower ? +t.heroPower : null,
          steals: +t.plundered.split("/")[0], maxSteals: +t.plundered.split("/")[1] }));
      setRewards({});   // point ids repeat across servers: old checks would show on the wrong tasks
      setResult({ at: Date.now(), server: +server, summary: j.summary, rows, raw: j });
    } catch (e) { setErr(e.message); }
    setBusy(false);
  };

  // One task's rewards: one request, only on a click (Henry 2026-10-30 - no sweeping the details).
  const loadRewards = async (t) => {
    if (!picked || !result) return;
    setRewards((r) => ({ ...r, [t.pointId]: { busy: true } }));
    try {
      const j = await runOp(picked, "secret.task.rewards", { server: String(result.server), point: String(t.pointId), completes: t.completes || "" });
      const all = j ? rowsOf(j) : [];
      setRewards((r) => ({ ...r, [t.pointId]: j ? { items: all.filter((i) => i.kind !== "raw"), raw: all.find((i) => i.kind === "raw")?.count, summary: j.summary } : undefined }));
    } catch (e) { setRewards((r) => ({ ...r, [t.pointId]: { err: e.message } })); }
  };

  const rows = useMemo(() => {
    if (!result) return [];
    const gone = (now - result.at) / 1000;
    const text = q.trim().toLowerCase();
    return result.rows.map((t) => ({ ...t, left: t.secondsLeft == null ? null : t.secondsLeft - gone }))
      .filter((t) => rarity[t.rarity] !== false && t.stars >= minStars)
      .filter((t) => status === "all" || (status === "running" ? t.left > 0 : t.left != null && t.left <= 0))
      .filter((t) => !open || (t.steals < t.maxSteals && t.rewarded !== "yes"))
      .filter((t) => !text || `${t.owner} ${t.tag} ${t.task}`.toLowerCase().includes(text));
  }, [result, now, rarity, minStars, status, open, q]);

  return (
    <>
      <div className="card">
        <p className="small muted">
          Every Secret Task on one server, read live off its map. Plunder others&apos; tasks (5 a day, each task 3 times at most) or assist allies. <b>Rank</b> 1-28, higher is better: SR 1-7, SSR 8-14, UR 15-21, red UR 22-28. <b>🎁 check</b> asks the game for one task&apos;s rewards (one request, finished tasks only - the game will not show an unfinished one); tasks of the same type have the same rewards, so every scan after shows them without asking.
          Scans run on a service account (lw_1, HQ 13 - needed to read another server&apos;s map).
        </p>
        <div className="row" style={{ gap: 8, flexWrap: "wrap", alignItems: "center" }}>
          <label className="small">Account{" "}
            <select value={picked?.id || ""} onChange={(e) => setAccount(e.target.value)}>
              {!runners.length && <option value="">no service account</option>}
              {runners.map((r) => <option key={r.id} value={r.id}>{r.name} · {r.tier || r.kind}{r.note ? ` · ${r.note}` : ""}{r.state === "ready" ? " · connected" : ""}</option>)}
            </select>
          </label>
          <label className="small">Server <input type="number" value={server} onChange={(e) => setServer(e.target.value)} style={{ width: 90 }} /></label>
          <button className="action" disabled={busy || !picked || !server} onClick={scan}>{busy ? "Scanning…" : "Scan for tasks"}</button>
          {err && <span className="err small">{err}</span>}
          {result && !busy && <span className="small muted">{result.summary} · scanned {Math.round((now - result.at) / 60000)} min ago · <ServerLink id={result.server} /></span>}
        </div>
      </div>
      {mapAt && result && <TaskMap server={result.server} {...mapAt} onClose={() => setMapAt(null)} />}
      {result && (
        <div className="card">
          <div className="row small" style={{ gap: 12, flexWrap: "wrap", alignItems: "center", marginBottom: 8 }}>
            {Object.keys(RARITY_COLOUR).map((k) => (
              <label key={k}><input type="checkbox" checked={rarity[k]} onChange={(e) => setRarity({ ...rarity, [k]: e.target.checked })} /> <b style={{ color: RARITY_COLOUR[k] }}>{k}</b></label>
            ))}
            <label>min stars <select value={minStars} onChange={(e) => setMinStars(+e.target.value)}>{[1, 2, 3, 4, 5, 6, 7].map((n) => <option key={n} value={n}>{n}★</option>)}</select></label>
            <label>show <select value={status} onChange={(e) => setStatus(e.target.value)}>
              <option value="running">in progress</option><option value="done">finished, not collected</option><option value="all">all</option>
            </select></label>
            <label><input type="checkbox" checked={open} onChange={(e) => setOpen(e.target.checked)} /> can still be plundered</label>
            <input placeholder="owner, tag or task" value={q} onChange={(e) => setQ(e.target.value)} style={{ width: 170 }} />
            <span className="muted" title="Nothing is re-scanned: time left counts down from the scan every 15 s, so tasks move from in progress to finished on their own">{rows.length} of {result.rows.length} · live countdown, not re-scanned</span>
            <label><input type="checkbox" checked={debug} onChange={(e) => setDebug(e.target.checked)} /> debug</label>
            {debug && <button className="small" onClick={() => download(`secret_tasks_${result.server}_${new Date(result.at).toISOString().slice(0, 16).replace(":", "")}.json`, result.raw)}>⬇ scan reply</button>}
          </div>
          <SortTable rows={rows} rowKey={(t) => t.pointId} initial={{ key: "rank", dir: -1 }} empty="No tasks match." columns={[
            { key: "rank", label: "rank", num: true, sort: (t) => t.rank * 1e8 - Math.max(-9e7, Math.min(9e7, t.left ?? 9e7)), render: (t) => <b title="1-28, higher is better: SR 1-7, SSR 8-14, UR 15-21, red UR 22-28 (stars within each)">{t.rank}</b> },
            { key: "left", label: "time left", num: true, sort: (t) => t.left ?? Infinity, render: (t) => (t.left == null ? "–" : <span className={t.left <= 0 ? "muted" : ""}>{dur(t.left)}</span>) },
            { key: "rarity", label: "task", sort: (t) => ({ SR: 1, SSR: 2, UR: 3 }[t.rarity] || 0) * 10 + t.stars,
              render: (t) => <span><b style={{ color: RARITY_COLOUR[t.rarity] }}>{t.rarity}</b> <span style={{ color: t.red ? "#e5534b" : "var(--muted)" }} title={t.red ? "red stars" : ""}>{"★".repeat(t.stars)}</span> <span className="small">{t.task}</span></span> },
            { key: "owner", label: "owner", render: (t) => (t.uid ? <span>{t.tag ? `[${t.tag}] ` : ""}<PlayerLink uid={t.uid} name={t.owner || undefined} /></span> : <span className="muted">–</span>) },
            { key: "heroPower", label: "hero power", num: true, render: (t) => (t.heroPower ? big(t.heroPower) : "") },
            { key: "steals", label: "plundered", num: true, render: (t) => `${t.steals}/${t.maxSteals}` },
            { key: "pointId", label: "where", sort: (t) => t.pointId, render: (t) => <a href="#" className="mono small" title="Show it on the map" onClick={(e) => { e.preventDefault(); setMapAt({ x: t.x, y: t.y, label: `${t.rarity} rank ${t.rank} ${t.task}${t.owner ? ` - ${t.owner}` : ""}` }); }}>{t.x}:{t.y} 🗺</a> },
            { key: "rewards", label: "rewards", sort: () => 0, render: (t) => {
              const r = rewards[t.pointId];
              if (!r && t.contents) return <span className="small" title="known for this task type - same type, same rewards">{t.contents.split("; ").map((x, k) => <div key={k}>{x}</div>)}</span>;
              if (!r && t.left > 0) return <span className="muted small" title="The game only shows a task's contents once it has finished">when finished</span>;
              // Guests only until a guest proves the exact request (lw_2 banned 2026-11-02); real accounts never ask.
              if (!r && picked?.kind !== "guest") return <span className="muted small" title="Reward checks are off on real accounts until a guest has proven the request. Pick a guest account to check.">guest only</span>;
              if (!r) return <button className="small" onClick={() => loadRewards(t)} title="Asks the game for this one task (one request). Every task of the same type shows it from then on.">🎁 check</button>;
              if (r.busy) return <span className="muted small">checking…</span>;
              if (r.err) return <span className="err small" title={r.err}>failed <button className="small" onClick={() => loadRewards(t)}>retry</button></span>;
              return <span className="small">{r.items.length ? r.items.map((i, k) => <div key={k} title={i.reward === "(raw reply)" ? String(i.count) : undefined}>{i.kind && i.kind !== "reward" ? <span className="muted">{i.kind}: </span> : null}{i.reward}</div>) : <span className="muted" title={r.items[0]?.count}>{r.summary}</span>}</span>;
            } },
            { key: "send", label: "", sort: () => 0, render: (t) => <SendPosition runners={all} sid={result.server} x={t.x} y={t.y} label={`${t.rarity} ${t.stars}★ ${t.task}${t.owner ? ` - ${t.owner}` : ""}`} text="📍 Send" /> },
            ...(debug ? [
              { key: "cfgId", label: "cfg / point", render: (t) => <span className="mono small">{t.cfgId}<br />{t.pointId}</span> },
              { key: "cfgRow", label: "task table row", sort: () => 0, render: (t) => <code className="small" style={{ whiteSpace: "pre-wrap", wordBreak: "break-all", display: "block", maxWidth: 360 }}>{t.cfgRow}</code> },
              { key: "rawReward", label: "reward reply", sort: () => 0, render: (t) => (rewards[t.pointId]?.raw
                ? <span><button className="small" onClick={() => download(`task_${result.server}_${t.pointId}.json`, rewards[t.pointId].raw)}>⬇</button>
                    <details><summary className="small">view</summary><code className="small" style={{ whiteSpace: "pre-wrap", wordBreak: "break-all", display: "block", maxWidth: 420 }}>{rewards[t.pointId].raw}</code></details></span>
                : <span className="muted small">check first</span>) },
            ] : []),
          ]} />
        </div>
      )}
    </>
  );
}

// Quality names as the game's own truck shares label them (onameParam2): 1 N, 2 R, 3 SR, 4 SSR, 5 UR; 10 is the top UR tier.
const TRUCK_Q = { 1: ["N", "#8b97a6"], 2: ["R", "#3fb950"], 3: ["SR", "#4c9aff"], 4: ["SSR", "#a371f7"], 5: ["UR", "#d4a72c"], 10: ["UR+", "#e5534b"] };
// A rough worth per item (about diamonds), to rank trucks - my estimate, not the game's. Unknown items count 5.
const ITEM_VALUE = {
  2270000: 300, 560006: 400, 230006: 100, 500105: 150, 500103: 50, 500101: 15, 700000: 100, 700001: 100,
  1520002: 50, 400502: 2, 520016: 3, 200363: 20, 200364: 20, 600001: 1, 600002: 10, 600003: 50,
};
const RES_VALUE = { Iron: 1, Food: 1, Coin: 2 };   // per 100,000
const truckValue = (c) => Math.round((c.items || []).reduce((s, i) => s + (ITEM_VALUE[i.id] ?? 5) * i.n, 0)
  + Object.entries(c.res || {}).reduce((s, [k, v]) => s + (RES_VALUE[k] ?? 1) * v / 100000, 0));

// Intercity trade trucks the chosen account can see: its own server and the servers matched with it (the
// game's "Others' Trucks" list, train.list). So the account decides WHICH servers - a guest on a server in
// the right group, or lw_1 for its own group. Send looks up where the truck is at that moment.
function Trucks({ runners: all }) {
  const runners = all.filter((r) => r.kind === "guest" || r.tier === "service");
  const prefs = usePrefs();
  const [account, setAccount] = useState(() => keep("trucks.account") || "");
  const [source, setSource] = useState(() => keep("trucks.source") || "map");
  const [target, setTarget] = useState(() => keep("trucks.server") || "1967");
  const [result, setResult] = useState(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState("");
  const [minQ, setMinQ] = useState(1);
  const [minLv, setMinLv] = useState(0);
  const [lootable, setLootable] = useState(true);
  const [server, setServer] = useState("");
  const [q, setQ] = useState("");
  const [now, setNow] = useState(Date.now());
  const [sent, setSent] = useState({});
  const [minValue, setMinValue] = useState(0);
  const [wanted, setWanted] = useState([]);   // item names a truck must carry (any of them)
  useEffect(() => { const t = setInterval(() => setNow(Date.now()), 15000); return () => clearInterval(t); }, []);
  const picked = runners.find((r) => r.id === account) || runners.find((r) => r.state === "ready") || runners[0];
  const sender = all.find((r) => r.id === prefs?.sendFrom && r.tier === "service");

  const scan = async () => {
    if (!picked) return;
    setBusy(true); setErr(""); keep("trucks.account", picked.id); keep("trucks.source", source); keep("trucks.server", String(target));
    try {
      const j = source === "map"
        ? await runOp(picked, "trucks.map", { server: String(target), positions: "true" })
        : await runOp(picked, "trucks.list", { refreshes: "6", positions: "true" });
      if (j) setResult({ at: Date.now(), summary: j.summary, rows: rowsOf(j).map((t) => ({ ...t, server: +t.server, quality: +t.quality, level: +t.level,
        completeness: +t.completeness, robbed: +t.robbed, power: +t.power, arrivesIn: +t.arrivesIn, x: t.x === "" ? null : +t.x, y: t.y === "" ? null : +t.y }))
        .map((t) => { let cargo = {}; try { cargo = JSON.parse(t.cargo || "{}"); } catch { cargo = {}; } return { ...t, cargo, value: truckValue(cargo) }; }) });
    } catch (e) { setErr(e.message); }
    setBusy(false);
  };

  const send = async (t) => {
    if (!sender || !prefs?.myUid) { setErr("set your player and a service account to send from on the Account tab"); return; }
    setSent((s) => ({ ...s, [t.uuid]: "sending…" }));
    try {
      const j = await runOp(sender, "truck.share", { uid: prefs.myUid, server: String(t.server), marchUid: t.marchUid, x: String(t.x ?? ""), y: String(t.y ?? ""),
        quality: String(t.quality), owner: `#${t.server} ${t.tag ? `[${t.tag}]` : ""}${t.owner}` });
      setSent((s) => ({ ...s, [t.uuid]: j ? "sent" : "" }));
    } catch (e) { setSent((s) => ({ ...s, [t.uuid]: e.message })); }
  };

  const servers = result ? [...new Set(result.rows.map((t) => t.server))].sort((a, b) => a - b) : [];
  // Every item seen in this scan, most common first - the content filter's choices.
  const itemNames = result ? Object.entries(result.rows.flatMap((t) => (t.cargo.items || []).map((i) => i.name))
    .reduce((m, n) => ({ ...m, [n]: (m[n] || 0) + 1 }), {})).sort((a, b) => b[1] - a[1]) : [];
  // One task's rewards: one request, only on a click (Henry 2026-10-30 - no sweeping the details).
  const loadRewards = async (t) => {
    if (!picked || !result) return;
    setRewards((r) => ({ ...r, [t.pointId]: { busy: true } }));
    try {
      const j = await runOp(picked, "secret.task.rewards", { server: String(result.server), point: String(t.pointId), completes: t.completes || "" });
      const all = j ? rowsOf(j) : [];
      setRewards((r) => ({ ...r, [t.pointId]: j ? { items: all.filter((i) => i.kind !== "raw"), raw: all.find((i) => i.kind === "raw")?.count, summary: j.summary } : undefined }));
    } catch (e) { setRewards((r) => ({ ...r, [t.pointId]: { err: e.message } })); }
  };

  const rows = useMemo(() => {
    if (!result) return [];
    const gone = (now - result.at) / 1000;
    const text = q.trim().toLowerCase();
    return result.rows.map((t) => ({ ...t, left: t.arrivesIn - gone }))
      .filter((t) => t.quality >= minQ && t.level >= minLv && t.left > 0)
      .filter((t) => !lootable || t.completeness > 0.5)
      .filter((t) => !server || t.server === +server)
      .filter((t) => t.value >= minValue)
      .filter((t) => !wanted.length || (t.cargo.items || []).some((i) => wanted.includes(i.name)))
      .filter((t) => !text || `${t.owner} ${t.tag} ${t.goods} ${t.resources}`.toLowerCase().includes(text));
  }, [result, now, minQ, minLv, lootable, server, q, minValue, wanted]);

  return (
    <>
      <div className="card">
        <p className="small muted">
          <b>Map of a server</b>: sweeps that server&apos;s map the way the game does at closest zoom and lists every truck driving on it (another server&apos;s map needs lw_1, HQ 13; a guest can only do its own).{" "}
          <b>Trade list</b>: the game&apos;s &quot;Others&apos; Trucks&quot; list - the account&apos;s own server and the ones matched with it.
          A truck can be looted until it is down to 50% (usually 2 loots). 📍 Send DMs you the game&apos;s own truck link - it follows the truck wherever it goes - from your service account.
          Value is a rough worth of the cargo (about diamonds: UR shard 300, SSR gear chest 150, recruit ticket 100, decoration chest 100...), to rank trucks.
        </p>
        <div className="row" style={{ gap: 8, flexWrap: "wrap", alignItems: "center" }}>
          <label className="small">Account{" "}
            <select value={picked?.id || ""} onChange={(e) => setAccount(e.target.value)}>
              {runners.map((r) => <option key={r.id} value={r.id}>{r.name} · {r.tier || r.kind}{r.note ? ` · ${r.note}` : ""}{r.state === "ready" ? " · connected" : ""}</option>)}
            </select>
          </label>
          <label className="small">Source{" "}
            <select value={source} onChange={(e) => setSource(e.target.value)}>
              <option value="map">map of a server</option>
              <option value="list">trade list (account&apos;s group)</option>
            </select>
          </label>
          {source === "map" && <label className="small">Server <input type="number" value={target} onChange={(e) => setTarget(e.target.value)} style={{ width: 90 }} /></label>}
          <button className="action" disabled={busy || !picked} onClick={scan}>{busy ? "Scanning… (up to a minute)" : "Scan for trucks"}</button>
          {err && <span className="err small">{err}</span>}
          {result && !busy && <span className="small muted">{result.summary} · scanned {Math.round((now - result.at) / 60000)} min ago</span>}
        </div>
      </div>
      {mapAt && result && <TaskMap server={result.server} {...mapAt} onClose={() => setMapAt(null)} />}
      {result && (
        <div className="card">
          <div className="row small" style={{ gap: 12, flexWrap: "wrap", alignItems: "center", marginBottom: 8 }}>
            <label>min quality <select value={minQ} onChange={(e) => setMinQ(+e.target.value)}>{Object.entries(TRUCK_Q).map(([k, [n]]) => <option key={k} value={k}>{n}</option>)}</select></label>
            <label>min level <input type="number" value={minLv} onChange={(e) => setMinLv(+e.target.value || 0)} style={{ width: 60 }} /></label>
            <label>server <select value={server} onChange={(e) => setServer(e.target.value)}><option value="">all</option>{servers.map((s) => <option key={s} value={s}>#{s}</option>)}</select></label>
            <label><input type="checkbox" checked={lootable} onChange={(e) => setLootable(e.target.checked)} /> can still be looted</label>
            <label>min value <input type="number" value={minValue} onChange={(e) => setMinValue(+e.target.value || 0)} style={{ width: 70 }} /></label>
            <input placeholder="owner, tag or cargo" value={q} onChange={(e) => setQ(e.target.value)} style={{ width: 170 }} />
            <span className="muted" title="Nothing is re-scanned: time left counts down from the scan every 15 s, so tasks move from in progress to finished on their own">{rows.length} of {result.rows.length} · live countdown, not re-scanned</span>
            <label><input type="checkbox" checked={debug} onChange={(e) => setDebug(e.target.checked)} /> debug</label>
            {debug && <button className="small" onClick={() => download(`secret_tasks_${result.server}_${new Date(result.at).toISOString().slice(0, 16).replace(":", "")}.json`, result.raw)}>⬇ scan reply</button>}
          </div>
          <div className="row small" style={{ gap: 6, flexWrap: "wrap", marginBottom: 8 }}>
            <span className="muted">carrying:</span>
            {itemNames.map(([n, count]) => (
              <button key={n} className={wanted.includes(n) ? "chip on" : "chip"} style={{ opacity: wanted.includes(n) ? 1 : 0.7, fontWeight: wanted.includes(n) ? 700 : 400 }}
                onClick={() => setWanted(wanted.includes(n) ? wanted.filter((w) => w !== n) : [...wanted, n])}>{n} <span className="muted">{count}</span></button>
            ))}
            {wanted.length > 0 && <a style={{ cursor: "pointer" }} onClick={() => setWanted([])}>clear</a>}
          </div>
          <SortTable rows={rows} rowKey={(t) => t.uuid || `${t.server}:${t.marchUid}`} initial={{ key: "value", dir: -1 }} empty="No trucks match." columns={[
            { key: "value", label: "value", num: true, render: (t) => <b title="rough worth of the cargo, about diamonds (my estimate)">{t.value.toLocaleString()}</b> },
            { key: "quality", label: "truck", num: true, sort: (t) => t.quality * 100 + t.level,
              render: (t) => <span><b style={{ color: TRUCK_Q[t.quality]?.[1] }}>{TRUCK_Q[t.quality]?.[0] || `q${t.quality}`}</b> <span className="muted">lv {t.level}</span></span> },
            { key: "owner", label: "owner", render: (t) => <span>{t.tag ? `[${t.tag}] ` : ""}<PlayerLink uid={t.uid} name={t.owner} /> <span className="muted small">#{t.server}</span></span> },
            { key: "power", label: "power", num: true, render: (t) => big(t.power) },
            { key: "goods", label: "cargo", render: (t) => <span className="small">{t.goods}<div className="muted">{t.resources}</div></span> },
            { key: "completeness", label: "left", num: true, render: (t) => <span title={`looted ${t.robbed}x`}>{Math.round(t.completeness * 100)}%</span> },
            { key: "left", label: "arrives in", num: true, render: (t) => dur(t.left) },
            { key: "where", label: "where", sort: (t) => t.x, render: (t) => (t.x == null ? "–" : <span className="mono small">{t.x}:{t.y}</span>) },
            { key: "rewards", label: "rewards", sort: () => 0, render: (t) => {
              const r = rewards[t.pointId];
              if (!r && t.contents) return <span className="small" title="known for this task type - same type, same rewards">{t.contents.split("; ").map((x, k) => <div key={k}>{x}</div>)}</span>;
              if (!r && t.left > 0) return <span className="muted small" title="The game only shows a task's contents once it has finished">when finished</span>;
              // Guests only until a guest proves the exact request (lw_2 banned 2026-11-02); real accounts never ask.
              if (!r && picked?.kind !== "guest") return <span className="muted small" title="Reward checks are off on real accounts until a guest has proven the request. Pick a guest account to check.">guest only</span>;
              if (!r) return <button className="small" onClick={() => loadRewards(t)} title="Asks the game for this one task (one request). Every task of the same type shows it from then on.">🎁 check</button>;
              if (r.busy) return <span className="muted small">checking…</span>;
              if (r.err) return <span className="err small" title={r.err}>failed <button className="small" onClick={() => loadRewards(t)}>retry</button></span>;
              return <span className="small">{r.items.length ? r.items.map((i, k) => <div key={k} title={i.reward === "(raw reply)" ? String(i.count) : undefined}>{i.kind && i.kind !== "reward" ? <span className="muted">{i.kind}: </span> : null}{i.reward}</div>) : <span className="muted" title={r.items[0]?.count}>{r.summary}</span>}</span>;
            } },
            { key: "send", label: "", sort: () => 0, render: (t) => <span className="small"><button className="action" onClick={() => send(t)} disabled={sent[t.uuid] === "sending…"}>📍 Send</button> {sent[t.uuid] && <span className={sent[t.uuid] === "sent" ? "muted" : "err"}>{sent[t.uuid]}</span>}</span> },
          ]} />
        </div>
      )}
    </>
  );
}
