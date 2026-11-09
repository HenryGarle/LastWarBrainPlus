import { useRef, useState } from "react";
// Tabs (Henry, 2026-09-30: too much on one page; land is interesting but not the main stat).
const TABS = [["overview", "Overview"], ["alliances", "Alliances"], ["players", "Players"], ["activity", "Activity"], ["movement", "Transfers"], ["season", "Season & presidents"], ["land", "Land"]];
import { useHashParam } from "../nav.js";
import { BarList, LineChart, SortTable } from "./charts.jsx";
import ServerPlayers from "./ServerPlayers.jsx";
import { allianceColour, big, href, int, pct, when, originOf } from "./format.js";
import { countryName, flagEmoji } from "./Online.jsx";
import { Delta, Stat, Status, statusOf, Tag, ServerLink, PlayerLink, Hero } from "./bits.jsx";
import { ServerIntelCards } from "./Intel.jsx";
import { ServerNewAccounts } from "./NewAccounts.jsx";
import { ServerOnline } from "./Online.jsx";
import { ServerSeason } from "./ServersPage.jsx";

export default function ServerPage({ d }) {
  const [origin, setOrigin] = useHashParam("origin", "all");   // in the URL: #/x/server/1967?origin=1963
  const [tab, setTab] = useHashParam("tab", "overview");
  const [country, setCountry] = useHashParam("country", "all");   // created in (profile country)
  const [flag, setFlag] = useHashParam("flag", "all");            // displays as (chosen flag)
  const [split, setSplit] = useState("country");                  // which breakdown is shown
  const [players, setPlayers] = useState(null);   // the player list's rows, for the country breakdown
  const list = useRef(null);
  // Clicking an origin (or the tile) filters the player list and brings it into view.
  const show = (o) => { setTab("players"); setOrigin(o); setTimeout(() => list.current?.scrollIntoView({ behavior: "smooth", block: "start" }), 0); };
  const showCountry = (key, cc) => { setTab("players"); (key === "flag" ? setFlag : setCountry)(cc || "unknown"); setTimeout(() => list.current?.scrollIntoView({ behavior: "smooth", block: "start" }), 0); };
  if (!d.found) return (
    <div className="grid">
      <p className="muted">No scans for server #{d.server} - it is in the registry only (Tracking page sets how closely each server is followed).</p>
      <ServerSeason server={d.server} />
    </div>
  );
  const migrants = d.origins.filter((o) => o.origin !== d.server).reduce((s, o) => s + o.players, 0);
  const inflow = Object.entries(d.arrivals.reduce((m, a) => ((m[a.fromServer ?? "?"] = (m[a.fromServer ?? "?"] || 0) + 1), m), {}))
    .sort((a, b) => b[1] - a[1]);

  return (
    <div className="grid">
      <div className="stats">
        <Stat label="Players" value={int(d.players)} sub={`mapped ${when(d.scannedAt)}`} />
        <Stat label="Alliances" value={d.alliances.length} sub={`${int(d.unaffiliated?.members)} players in none`} />
        <Stat label="Hero power" value={big(d.heroTotal)} sub={`estimated, every player from #${d.server} · ${big(d.totalPower)} total power`} />
        <Stat onClick={() => setTab("activity")} label="Active this week" value={d.compared ? pct(d.active, d.compared) : "–"}
          sub={d.compared ? `${int(d.active)} of ${int(d.compared)} players from #${d.server} logged in, donated or chatted in ${d.windowDays || 7} days · ${int(d.active1d)} today` : "needs a profile sweep"} />
        <Stat onClick={() => show("elsewhere")} label="Created elsewhere" value={pct(migrants, d.players)} sub={`${int(migrants)} players transferred in`} />
        <Stat onClick={() => setTab("movement")} label="Applying to join" value={d.applicants.length} sub={d.applicants.length ? `${big(d.applicants.reduce((s, a) => s + a.power, 0))} power` : "none captured"} />
      </div>

      <nav className="subnav big">
        {TABS.map(([id, label]) => (
          <a key={id} href="#" className={tab === id ? "on" : ""} onClick={(e) => { e.preventDefault(); setTab(id); }}>{label}</a>
        ))}
      </nav>

      {tab === "overview" && <>
      <ServerIntelCards server={d.server} part="stats" />      <div className="two">
        <div className="card">
          <h2>Strongest players</h2>
          <SortTable
            initial={{ key: "hero", dir: -1 }}
            rows={d.top}
            columns={[
              { key: "name", label: "player", render: (p) => <PlayerLink uid={p.uid} name={p.name} /> },
              { key: "tag", label: "alliance", render: (p) => <Tag id={p.allianceId} tag={p.tag} /> },
              { key: "hero", label: "hero power", num: true, sort: (p) => p.heroPower ?? p.heroEst, render: (p) => <Hero board={p.heroPower} est={p.heroEst} /> },
              { key: "power", label: "total power", num: true, render: (p) => big(p.power) },
              { key: "change", label: "change", num: true, sort: (p) => (p.prev ? p.power - p.prev : null),
                render: (p) => <Delta value={p.prev ? p.power - p.prev : null} /> },
            ]}
          />
        </div>
        <div className="card">
          <h2>Hero power board</h2>
          <SortTable
            initial={{ key: "rank", dir: 1 }}
            rows={d.heroBoard}
            empty="No leaderboard saved for this server."
            columns={[
              { key: "rank", label: "#", num: true },
              { key: "name", label: "player", render: (p) => <PlayerLink uid={p.uid} name={p.name} /> },
              { key: "tag", label: "alliance", render: (p) => <Tag id={p.allianceId} tag={p.tag} /> },
              { key: "heroPower", label: "hero power", num: true, render: (p) => big(p.heroPower) },
            ]}
          />
        </div>
      </div>

      <div className="card">
          <h2>Alliances by hero power</h2>
          <BarList
            rows={d.alliances.filter((a) => a.hero).map((a) => ({
              key: a.allianceId, label: a.tag || "(no tag)", value: a.hero, href: href.alliance(a.allianceId), color: allianceColour(a.allianceId),
              detail: `${a.members} members · ${big(a.power)} total power${a.compared ? ` · ${a.active} of ${a.compared} active` : ""}`,
            }))}
            onlyTop limit={15}
          />
        </div>
      </>}

      {tab === "season" && <>
      <ServerSeason server={d.server} />
      <ServerIntelCards server={d.server} part="presidents" />
      </>}

      {tab === "alliances" && <>      <div className="card">
        <h2>Alliances</h2>
        <SortTable
          initial={{ key: "hero", dir: -1 }}
          rows={d.alliances}
          columns={[
            { key: "tag", label: "alliance", render: (a) => <Tag id={a.allianceId} tag={a.tag || "(no tag)"} /> },
            { key: "members", label: "members", num: true },
            { key: "hero", label: "hero power", num: true, render: (a) => big(a.hero) },
            { key: "power", label: "power", num: true, render: (a) => big(a.power) },
            { key: "avg", label: "avg", num: true, sort: (a) => (a.measured ? a.power / a.measured : null),
              render: (a) => (a.measured ? big(a.power / a.measured) : "–") },
            { key: "active", label: "active", num: true, sort: (a) => (a.compared ? a.active / a.compared : null),
              render: (a) => (a.compared ? `${pct(a.active, a.compared)} (${a.active})` : "–") },
          ]}
        />
      </div>

      </>}

      {tab === "players" && <>        <div className="two">
        <div className="card">
          <h2>Where its players were created <span className="muted small" style={{ textTransform: "none", letterSpacing: 0 }}>- click one to list them</span></h2>
          <BarList
            rows={d.origins.map((o) => ({
              key: o.origin, label: o.origin === d.server ? `#${o.origin} (here)` : `#${o.origin}`,
              value: o.players, onClick: () => show(o.origin === d.server ? "here" : String(o.origin)),
            }))}
            format={int} onlyTop limit={15}
          />
        </div>
        <div className="card">
          <h2>Where its players are from <span className="muted small" style={{ textTransform: "none", letterSpacing: 0 }}>- click one to list them</span></h2>
          <nav className="subnav" style={{ marginBottom: 8 }}>
            <a href="#" className={split === "country" ? "on" : ""} onClick={(e) => { e.preventDefault(); setSplit("country"); }} title="The profile country - where the account is from">Created in</a>
            <a href="#" className={split === "flag" ? "on" : ""} onClick={(e) => { e.preventDefault(); setSplit("flag"); }} title="The flag the player chooses to show">Displays as</a>
          </nav>
          {!players ? <p className="muted small">Loading…</p> : (() => {
            const m = new Map();
            players.forEach((p) => m.set(p[split] || "", (m.get(p[split] || "") || 0) + 1));
            return (
              <BarList
                key={split}
                rows={[...m.entries()].sort((a, b) => b[1] - a[1]).map(([cc, n]) => ({
                  key: cc || "unknown",
                  label: <><tspan className="flag">{cc ? flagEmoji(cc) : ""}</tspan> {cc ? countryName(cc) : split === "flag" ? "no flag" : "unknown"} {pct(n, players.length)}</>,
                  value: n, onClick: () => showCountry(split, cc),
                }))}
                format={int} onlyTop limit={15}
              />
            );
          })()}
        </div>
        </div>
      <div ref={list}>
        <ServerPlayers server={d.server} origin={origin} setOrigin={setOrigin} country={country} setCountry={setCountry} flag={flag} setFlag={setFlag} onRows={setPlayers} />
      </div>
      </>}

      {tab === "activity" && <>
      <ServerOnline server={d.server} />
      <ServerNewAccounts server={d.server} />      <div className="two">
        <div className="card">
          <h2>Players on the map, by scan</h2>
          <LineChart points={d.scans.map((s) => ({ x: s.at, y: s.players }))} format={int} label="players per scan" />
        </div>
        <div className="card">
          <h2>Total power, by snapshot</h2>
          <LineChart points={d.snapshots.map((s) => ({ x: s.at, y: s.power }))} label="total power per snapshot" />
        </div>
      </div>
      </>}

      {tab === "movement" && <>      <div className="two">
        <div className="card">
          <h2>Arrived since the last map ({d.arrivals.length})</h2>
          {inflow.length > 0 && (
            <p className="small muted">
              From {inflow.slice(0, 6).map(([s, n]) => (s === "?" ? `new or unseen ×${n}` : `#${s} ×${n}`)).join(", ")}
            </p>
          )}
          <SortTable
            rows={d.arrivals}
            empty="No arrivals between the last two maps."
            columns={[
              { key: "name", label: "player", render: (p) => <PlayerLink uid={p.uid} name={p.name} /> },
              { key: "tag", label: "alliance", render: (p) => <Tag id={p.allianceId} tag={p.tag} /> },
              { key: "fromServer", label: "from", render: (p) => (p.fromServer ? <ServerLink id={p.fromServer} /> : <span className="muted">new / unseen</span>) },
            ]}
          />
        </div>
        <div className="card">
          <h2>Left since the last map ({d.departures.length})</h2>
          <SortTable
            rows={d.departures}
            empty="Nobody left between the last two maps."
            columns={[
              { key: "name", label: "player", render: (p) => <PlayerLink uid={p.uid} name={p.name} /> },
              { key: "tag", label: "was in", render: (p) => <Tag id={p.allianceId} tag={p.tag} /> },
              { key: "nowServer", label: "now on", render: (p) => (p.nowServer && p.nowServer !== d.server ? <ServerLink id={p.nowServer} /> : <span className="muted">not seen since</span>) },
            ]}
          />
        </div>
      </div>

      {d.applicants.length > 0 && (
        <div className="card">
          <h2>Applying to transfer here</h2>
          <SortTable
            initial={{ key: "power", dir: -1 }}
            rows={d.applicants}
            columns={[
              { key: "name", label: "player", render: (p) => <PlayerLink uid={p.uid} name={p.name} /> },
              { key: "fromServer", label: "from", render: (p) => <ServerLink id={p.fromServer} /> },
              { key: "tag", label: "alliance" },
              { key: "power", label: "power", num: true, render: (p) => big(p.power) },
              { key: "heroPower", label: "hero", num: true, render: (p) => big(p.heroPower) },
              { key: "thpRank", label: "hero rank", num: true, render: (p) => (p.thpRank ? `#${p.thpRank}` : "–") },
            ]}
          />
        </div>
      )}

      </>}

      {tab === "land" && <ServerIntelCards server={d.server} part="land" />}
    </div>
  );
}