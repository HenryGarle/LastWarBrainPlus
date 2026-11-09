import { useEffect, useMemo, useState } from "react";
import MapPage from "../explorer/MapPage.jsx";
import { allianceColour, ago, big, href, int, when } from "../explorer/format.js";
import { Tag } from "../explorer/bits.jsx";
import { SortTable } from "../explorer/charts.jsx";
import { setTitle } from "../nav.js";
import { SEASONS, layout, landRanking, buffText, LAND_RULE } from "../explorer/seasons.js";
import { useFavourites, isFav } from "../explorer/favs.jsx";

// #/maps                              every server (the List tab)
// #/maps/1967[/x,y[,uid]][?scan=N&hive=allianceId]   that server's map (the Map tab)
// #/maps/hives                        every alliance's hive score, every server
// #/maps/land                         land ranking: who holds what, and the buffs that come with it
const TABS = [["list", "List"], ["map", "Map"], ["hives", "Hive scores"], ["land", "Land ranking"]];
const LAST = "map.last";
const remember = (v) => { try { if (v === undefined) return localStorage.getItem(LAST); localStorage.setItem(LAST, v); } catch { /* private */ } return null; };

const parse = () => {
  const [path, qs = ""] = (window.location.hash || "#/maps").split("?");
  const [, , first = "", extra = ""] = path.split("/");
  const q = new URLSearchParams(qs);
  const scan = q.get("scan");
  const tab = first === "" ? "list" : first === "hives" || first === "land" ? first : "map";
  return { tab, server: tab === "map" ? first : null, extra: extra + (scan ? `?scan=${scan}` : ""), scan, hive: q.get("hive") };
};

function useRoute() {
  const [r, setR] = useState(parse);
  useEffect(() => {
    const on = () => setR(parse());
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);
  return r;
}

const useJson = (url) => {
  const [d, setD] = useState(null);
  const [err, setErr] = useState(null);
  useEffect(() => {
    if (!url) return undefined;
    let live = true; setD(null); setErr(null);
    fetch(url).then((r) => (r.ok ? r.json() : Promise.reject(new Error(r.statusText)))).then((x) => live && setD(x)).catch((e) => live && setErr(e.message));
    return () => { live = false; };
  }, [url]);
  return [d, err];
};

/** Every mapped server, in server order: size, active and total hero power, its strongest alliances. */
function ServerList({ rows }) {
  const [q, setQ] = useState("");
  const shown = useMemo(() => (rows || [])
    .filter((r) => !q.trim() || String(r.server).includes(q.trim()) || (r.topAlliances || []).some((a) => (a.tag || "").toLowerCase().includes(q.trim().toLowerCase())))
    .sort((a, b) => a.server - b.server), [rows, q]);
  return (
    <>
      <div className="row" style={{ justifyContent: "space-between", gap: 8, flexWrap: "wrap" }}>
        <p className="muted small" style={{ margin: 0 }}>Every server&apos;s map from its newest scan. Pick one to explore bases, alliances and where the power sits.</p>
        <input className="page-search" placeholder="server number or alliance tag" value={q} onChange={(e) => setQ(e.target.value)} />
      </div>
      {!rows ? <p className="muted">Loading…</p> : (
        <div className="card nice-table">
          <SortTable
            initial={{ key: "server", dir: 1 }}
            rows={shown}
            limit={100}
            columns={[
              { key: "server", label: "server", render: (r) => <a className="sc-num" href={`#/maps/${r.server}`}>#{r.server}</a> },
              { key: "open", label: "", sort: () => 0, render: (r) => <a className="action small-action" href={`#/maps/${r.server}`}>◈ Open map</a> },
              { key: "players", label: "bases on the map", num: true, render: (r) => int(r.players) },
              { key: "alliances", label: "alliances", num: true },
              { key: "active", label: "active hero power", num: true, sort: (r) => r.totals?.activeHero7 ?? null, render: (r) => big(r.totals?.activeHero7) },
              { key: "hero", label: "hero power (all)", num: true, sort: (r) => r.totals?.heroEst ?? null, render: (r) => big(r.totals?.heroEst) },
              { key: "top", label: "strongest alliances", sort: () => 0, render: (r) => (
                <span className="sc-tags">
                  {[...(r.topAlliances || [])].sort((a, b) => b.activeHero7 - a.activeHero7).slice(0, 3).map((a) => (
                    <span key={a.allianceId} className="tag" style={{ "--al": allianceColour(a.allianceId) }}>{a.tag || "?"}</span>
                  ))}
                </span>
              ) },
              { key: "scanned", label: "mapped", render: (r) => <span className="muted small">{ago(r.scanned)}</span> },
            ]}
          />
        </div>
      )}
    </>
  );
}

/** Jump to another server's map: type to search (native datalist), or step with the arrows. */
function ServerSwitch({ rows, current }) {
  const list = useMemo(() => (rows || []).map((r) => r.server).sort((a, b) => a - b), [rows]);
  const favs = useFavourites();   // favourite servers first in the drop-down
  const [v, setV] = useState(String(current));
  useEffect(() => setV(String(current)), [current]);
  const go = (s) => { if (s && String(s) !== String(current)) window.location.hash = `#/maps/${s}`; };
  const i = list.indexOf(Number(current));
  return (
    <span className="row small server-switch" style={{ gap: 4 }}>
      <button disabled={i <= 0} onClick={() => go(list[i - 1])} title="previous server">‹</button>
      <input list="map-servers" value={v} style={{ width: 110 }} placeholder="server"
        onChange={(e) => { setV(e.target.value); const n = Number(e.target.value.replace(/\D/g, "")); if (list.includes(n)) go(n); }}
        onKeyDown={(e) => { if (e.key === "Enter") { const n = Number(v.replace(/\D/g, "")); if (list.includes(n)) go(n); } }} />
      <datalist id="map-servers">
        {[...(rows || [])].sort((a, b) => isFav(favs, "server", b.server) - isFav(favs, "server", a.server)).map((r) => (
          <option key={r.server} value={r.server}>{`${isFav(favs, "server", r.server) ? "★ " : ""}${int(r.players)} bases · ${(r.topAlliances || []).slice(0, 3).map((a) => a.tag).join(" ")}`}</option>))}
      </datalist>
      <button disabled={i < 0 || i >= list.length - 1} onClick={() => go(list[i + 1])} title="next server">›</button>
    </span>
  );
}

function ServerMap({ route, rows, admin }) {
  const [d, setD] = useState(null);
  const [error, setError] = useState(null);
  useEffect(() => {
    let live = true;
    setD(null); setError(null);
    setTitle(`Map #${route.server}`);
    remember(route.server);
    fetch(`/api/x/map/${route.server}${route.scan ? `?scan=${route.scan}` : ""}`)
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(r.status))))
      .then((x) => live && setD(x)).catch((e) => live && setError(e.message));
    return () => { live = false; };
  }, [route.server, route.scan]);
  return (
    <>
      <div className="row small" style={{ gap: 10, flexWrap: "wrap" }}>
        <span className="muted">Server</span> <ServerSwitch rows={rows} current={route.server} />
        <a href={`#/x/server/${route.server}`}>server page ›</a>
        {d && d.found && <ScanAge at={d.at} admin={admin} />}
      </div>
      {error && <p className="err">Could not load: {error}</p>}
      {!d && !error && <p className="muted">Loading the map…</p>}
      {d && !d.found && <p className="muted">No map scanned for #{route.server} yet.</p>}
      {d && d.found && <MapPage key={`${route.server}/${d.scan}`} d={d} route={{ extra: route.extra, hive: route.hive }} base="#/maps" />}
    </>
  );
}
/** Every alliance's hive, every server's newest map (computed on the server, cached ten minutes). */
function HiveScores() {
  const [d, err] = useJson("/api/x/hives");
  const [q, setQ] = useState("");
  const [min, setMin] = useState(90);      // alliance size (bases on the map): a full alliance is 100
  const [top, setTop] = useState(0);       // 0 = every alliance; else the N strongest (hero power) on each server
  useEffect(() => setTitle("Hive scores"), []);
  const rows = useMemo(() => {
    let r = (d?.rows || []).filter((x) => x.bases >= min);
    if (top) {
      const rank = new Map();
      for (const x of [...(d?.rows || [])].sort((a, b) => b.hero - a.hero)) {
        const k = x.server, n = (rank.get(k) || 0) + 1;
        rank.set(k, n); x.rank = n;
      }
      r = r.filter((x) => x.rank <= top);
    }
    return r.filter((x) => !q.trim() || String(x.server).includes(q.trim()) || (x.tag || "").toLowerCase().includes(q.trim().toLowerCase()));
  }, [d, q, min, top]);
  return (
    <>
      <div className="row" style={{ justifyContent: "space-between", gap: 8, flexWrap: "wrap" }}>
        <p className="muted small" style={{ margin: 0 }}>
          How regular each alliance&apos;s main hive is, 0-100: even spacing (45%), bases lined up (35%), no holes (20%; one gap in the middle is allowed).
          The hive is the biggest group of its bases chained at most 8 tiles apart. Members = its bases on the map; top N = strongest by hero power on each server.
        </p>
        <span className="row small" style={{ gap: 8 }}>
          <label className="muted row" style={{ gap: 4 }}>alliances of
            <select value={min} onChange={(e) => setMin(Number(e.target.value))} style={{ width: "auto" }}>
              {[5, 20, 40, 60, 80, 90, 95, 100].map((v) => <option key={v} value={v}>{v}+ members</option>)}
            </select>
          </label>
          <label className="muted row" style={{ gap: 4 }}>
            <select value={top} onChange={(e) => setTop(Number(e.target.value))} style={{ width: "auto" }}>
              <option value={0}>every alliance</option>
              <option value={3}>top 3 per server</option>
              <option value={5}>top 5 per server</option>
            </select>
          </label>
          <input className="page-search" placeholder="server or alliance tag" value={q} onChange={(e) => setQ(e.target.value)} />
        </span>
      </div>
      {err && <p className="err">{err}</p>}
      {!d && !err && <p className="muted">Scoring every server…</p>}
      {d && (
        <div className="card nice-table">
          <SortTable
            initial={{ key: "score", dir: -1 }}
            rows={rows}
            limit={200}
            empty="No hive that size."
            columns={[
              { key: "tag", label: "alliance", render: (r) => <Tag id={r.allianceId} tag={r.tag || "?"} /> },
              { key: "server", label: "server", num: true, render: (r) => <a href={`#/maps/${r.server}`}>#{r.server}</a> },
              { key: "hero", label: "hero power", num: true, render: (r) => big(r.hero) },
              { key: "score", label: "score", num: true, render: (r) => <span className="row" style={{ gap: 6 }}><strong>{r.score}</strong><span className="hive-bar" style={{ width: 60 }}><i style={{ width: `${r.score}%` }} /></span></span> },
              { key: "hive", label: "in hive", num: true, sort: (r) => r.hive, render: (r) => <>{r.hive}<span className="muted">/{r.bases}</span></> },
              { key: "spacing", label: "spacing", num: true, render: (r) => `${Math.round(r.spacing * 100)}%` },
              { key: "alignment", label: "lined up", num: true, render: (r) => `${Math.round(r.alignment * 100)}%` },
              { key: "holes", label: "holes", num: true, render: (r) => <>{r.holes}{r.gap && <span className="muted small"> + middle gap</span>}</> },
              { key: "pitch", label: "every", num: true, render: (r) => `${r.pitch} tiles` },
              { key: "go", label: "", sort: () => 0, render: (r) => <a className="action small-action" href={`#/maps/${r.server}/${r.x},${r.y}?hive=${r.allianceId}`}>◈ on the map</a> },
            ]}
          />
        </div>
      )}
    </>
  );
}

/** Who holds which season cities / dig sites, scored, with the buffs that come with them. */
function LandRanking() {
  const [d, err] = useJson("/api/x/holders");
  const [season, setSeason] = useState(() => { try { return localStorage.getItem("land.season") || "2"; } catch { return "2"; } });
  const [q, setQ] = useState("");
  useEffect(() => { setTitle("Land ranking"); try { localStorage.setItem("land.season", season); } catch { /* private */ } }, [season]);
  const lay = useMemo(() => layout(season), [season]);
  const rows = useMemo(() => (d && lay ? landRanking(lay, d) : []).filter((r) =>
    !q.trim() || String(r.server).includes(q.trim()) || (r.tag || "").toLowerCase().includes(q.trim().toLowerCase())), [d, lay, q]);
  const summary = (held, type) => {
    const l = held.filter((b) => b.type === type).map((b) => b.level).sort((a, b) => b - a);
    return l.length ? <>{l.length} <span className="muted">(L{l.join(", L")})</span></> : <span className="muted">-</span>;
  };
  const newest = d?.reduce((m, h) => (h.at > m ? h.at : m), "");
  return (
    <>
      <div className="row" style={{ justifyContent: "space-between", gap: 8, flexWrap: "wrap" }}>
        <p className="muted small" style={{ margin: 0 }}>
          Score = {LAND_RULE}. Holders come from the alliance-rosters sweep{newest ? ` (newest ${when(newest)})` : ""}; buffs from the game&apos;s own tables, summed as listed per building.
        </p>
        <span className="row small" style={{ gap: 8 }}>
          <select value={season} onChange={(e) => setSeason(e.target.value)} style={{ width: "auto" }}>
            {Object.entries(SEASONS).map(([k, v]) => <option key={k} value={k}>{v.name}</option>)}
          </select>
          <input className="page-search" placeholder="server or alliance tag" value={q} onChange={(e) => setQ(e.target.value)} />
        </span>
      </div>
      {err && <p className="err">{err}</p>}
      {!d && !err && <p className="muted">Loading holders…</p>}
      {d && (
        <div className="card nice-table">
          <SortTable
            initial={{ key: "score", dir: -1 }}
            rows={rows}
            limit={200}
            empty="Nobody holds anything that matches."
            columns={[
              { key: "tag", label: "alliance", render: (r) => <><Tag id={r.allianceId} tag={r.tag || "?"} />{r.capitol && <span className="pill small" title="Holds the capitol - shown, not scored"> capitol</span>}</> },
              { key: "server", label: "server", num: true, render: (r) => <a href={`#/maps/${r.server}`}>#{r.server}</a> },
              { key: "score", label: "score", num: true, render: (r) => <strong>{r.score}</strong> },
              { key: "cities", label: "cities", sort: (r) => r.held.filter((b) => b.type === 1).length, render: (r) => summary(r.held, 1) },
              { key: "digs", label: "dig sites", sort: (r) => r.held.filter((b) => b.type === 4).length, render: (r) => summary(r.held, 4) },
              ...(season === "3" ? [{ key: "trade", label: "trade posts", sort: (r) => r.held.filter((b) => b.type === 5).length, render: (r) => summary(r.held, 5) }] : []),
              { key: "res", label: "alliance resource / h", num: true, render: (r) => int(r.res) },
              { key: "buffs", label: "buffs", sort: (r) => r.buffs.size, render: (r) => (
                <span className="small">{[...r.buffs].sort((a, b) => b[1] - a[1]).map(([e, v]) => `${e} ${buffText(v)}`.trim()).join(" · ") || <span className="muted">-</span>}</span>
              ) },
            ]}
          />
        </div>
      )}
    </>
  );
}

export default function Maps({ admin }) {
  const route = useRoute();
  const [rows, setRows] = useState(null);
  useEffect(() => { fetch("/api/x/overview").then((r) => r.json()).then(setRows).catch(() => setRows([])); }, []);
  useEffect(() => { if (route.tab === "list") setTitle("Maps"); }, [route.tab]);
  const mapHref = `#/maps/${route.server || remember() || (rows && rows.length ? Math.min(...rows.map((r) => r.server)) : 1967)}`;
  return (
    <div className="grid">
      <nav className="subnav">
        {TABS.map(([k, label]) => (
          <a key={k} className={route.tab === k ? "on" : ""} aria-current={route.tab === k ? "page" : undefined}
            href={k === "list" ? "#/maps" : k === "map" ? mapHref : `#/maps/${k}`}>{label}</a>
        ))}
      </nav>
      {route.tab === "list" && <ServerList rows={rows} />}
      {route.tab === "map" && <ServerMap route={route} rows={rows} admin={admin} />}
      {route.tab === "hives" && <HiveScores />}
      {route.tab === "land" && <LandRanking />}
    </div>
  );
}

// How old the map is, and (admins) a button that runs the map-refresh job now - every followed server, ~20 min,
// on the job's own account. The job itself runs once a day (Henry, 2026-10-08).
function ScanAge({ at, admin }) {
  const [msg, setMsg] = useState("");
  const [job, setJob] = useState(null);
  useEffect(() => {
    if (!admin) return;
    fetch("/api/schedule").then((r) => r.json()).then((j) => setJob((j.jobs || []).find((x) => x.id === "map-refresh") || null)).catch(() => {});
  }, [admin, msg]);
  const hours = (Date.now() - Date.parse(at)) / 3600000;
  const run = async () => {
    if (!window.confirm("Rescan every followed server's map now? It takes about 20 minutes and uses the map-refresh job's account.")) return;
    const r = await fetch("/api/schedule/map-refresh/run", { method: "POST" });
    setMsg(r.ok ? "rescan started - reload this map in about 20 minutes" : r.status === 409 ? "a rescan is already running" : `could not start (${r.status})`);
  };
  return (
    <span className="small" title={when(at)}>
      <span className={hours > 26 ? "err" : "muted"}>scanned {ago(at)}</span>
      {admin && job && (
        <>
          {" · "}
          {job.running ? <span className="muted">rescan running ({(job.running.lines || []).slice(-1)[0] || "started"})</span>
            : <button onClick={run} title={job.nextAt ? `next scheduled rescan ${when(job.nextAt)}` : ""}>Rescan maps now</button>}
          {msg && <span className="muted"> {msg}</span>}
        </>
      )}
    </span>
  );
}