import { BarList, LineChart, SortTable } from "./charts.jsx";
import { big, href, int, pct, when } from "./format.js";
import { Delta, Stat, Status, statusOf, ServerLink, PlayerLink, NameChanges, Tag, Hero } from "./bits.jsx";
import { AllianceIntelCards, Seen } from "./Intel.jsx";
import { AllianceActivityTab } from "./AllianceTabs.jsx";
import { useHashParam } from "../nav.js";

const TABS = [["overview", "Overview"], ["activity", "Activity"]];

export default function AlliancePage({ d }) {
  const [tab, setTab] = useHashParam("tab", "overview");
  if (!d.found) return <p className="muted">No alliance {d.allianceId} in any stored map.</p>;
  const avg = d.memberCount ? d.totalPower / d.memberCount : 0;

  return (
    <div className="grid">
      <div className="stats">
        <Stat label="Members" value={d.memberCount} sub={<>from <ServerLink id={d.server} />{d.seen ? <>, mapped {when(d.seen)}</> : null}</>} />
        <Stat label="Hero power" value={big(d.heroTotal)} sub={`estimated, every member · avg ${big(d.memberCount ? d.heroTotal / d.memberCount : 0)}`} />
        <Stat label="Total power" value={big(d.totalPower)} sub={`avg ${big(avg)} · ${big(d.totalHeroPower)} hero on the top-200 boards`} />
        <Stat label="Active this week" value={d.measured ? pct(d.active, d.measured) : "–"}
          sub={d.measured ? `${d.active} of ${d.measured} logged in, donated or chatted in ${d.windowDays || 7} days` : "no profiles yet"} />
        <Stat label="Transferred in" value={d.migrants} sub="created on another server" />
        <Stat label="Past members" value={d.past.length} sub={`${d.members.filter((m) => m.joinedKnown).length} current members joined while we watched`} />
      </div>

      <nav className="subnav big">
        {TABS.map(([id, label]) => (
          <a key={id} href="#" className={tab === id ? "on" : ""} onClick={(e) => { e.preventDefault(); setTab(id); }}>{label}</a>
        ))}
      </nav>
      {tab === "activity" && <AllianceActivityTab id={d.allianceId} />}

      {tab === "overview" && <>
      <AllianceIntelCards id={d.allianceId} />

      {(d.nameChanges || []).length > 0 && (
        <div className="card">
          <h2>Tag and name history</h2>
          <NameChanges rows={[...d.nameChanges].reverse()} />
        </div>
      )}

      <div className="two">
        <div className="card">
          <h2>Members by hero power</h2>
          <BarList
            rows={d.members.filter((m) => m.heroPower ?? m.heroEst).map((m) => ({
              key: m.uid, label: m.name, value: m.heroPower ?? m.heroEst, href: href.player(m.uid),
              detail: `HQ ${m.hq} · ${big(m.power)} total power${m.heroPower ? "" : " · hero estimated"}`,
            }))}
            onlyTop limit={20}
          />
        </div>
        <div className="card">
          <h2>Members over time</h2>
          <LineChart points={d.history.map((h) => ({ x: h.at, y: h.members }))} format={int} label="members per scan" />
          {d.voices.length > 0 && (
            <>
              <h2 style={{ marginTop: 18 }}>Loudest in chat</h2>
              <BarList
                rows={d.voices.map((v) => ({ key: v.uid, label: v.name, value: v.messages, href: href.player(v.uid) }))}
                format={int} onlyTop limit={8}
              />
            </>
          )}
        </div>
      </div>

      <div className="card">
        <h2>Roster</h2>
        <SortTable
          initial={{ key: "hero", dir: -1 }}
          rows={d.members}
          columns={[
            { key: "name", label: "player", render: (m) => <PlayerLink uid={m.uid} name={m.name} /> },
            { key: "hero", label: "hero power", num: true, sort: (m) => m.heroPower ?? m.heroEst, render: (m) => <Hero board={m.heroPower} est={m.heroEst} /> },
            { key: "power", label: "total power", num: true, render: (m) => big(m.power) },
            { key: "change", label: "change", num: true, sort: (m) => (m.prev ? m.power - m.prev : null),
              render: (m) => <Delta value={m.prev ? m.power - m.prev : null} /> },
            { key: "lastSeen", label: "last seen", sort: (m) => m.lastSeen || "", render: (m) => <Seen iso={m.lastSeen} /> },
            { key: "status", label: "activity", sort: (m) => statusOf(m.power, m.prev, m.active7),
              render: (m) => <Status value={statusOf(m.power, m.prev, m.active7)} /> },
            { key: "hq", label: "HQ", num: true },
            { key: "origin", label: "created on", render: (m) => (m.origin === d.server ? <span className="muted">here</span> : <ServerLink id={m.origin} />) },
            { key: "since", label: "member since", render: (m) => (m.joinedKnown ? when(m.since) : <span className="muted">before tracking</span>) },
          ]}
        />
      </div>

      <div className="card">
        <h2>Past members ({d.past.length})</h2>
        {d.past.length === 0 ? <p className="muted small">Nobody seen leaving yet.</p> : (
          <SortTable rows={d.past} initial={{ key: "leftAt", dir: -1 }} columns={[
            { key: "name", label: "player", render: (m) => <PlayerLink uid={m.uid} name={m.name} /> },
            { key: "since", label: "joined", render: (m) => (m.joinedKnown ? when(m.since) : <span className="muted">before tracking</span>) },
            { key: "leftAt", label: "left", render: (m) => <span title={`last seen in the alliance ${when(m.lastSeen)}`}>{when(m.leftAt)}</span> },
            { key: "nextTag", label: "went to", render: (m) => (m.nextAllianceId ? <Tag id={m.nextAllianceId} tag={m.nextTag || "?"} /> : <span className="muted">no alliance</span>) },
            { key: "home", label: "now from", render: (m) => <ServerLink id={m.home} /> },
            { key: "power", label: "power", num: true, render: (m) => big(m.power) },
          ]} />
        )}
        <p className="muted small">"Left" is the first time we saw them outside the alliance; they left somewhere between their last sighting in it and then.</p>
      </div>
      {d.applicants.length > 0 && (
        <div className="card">
          <h2>Members applying to transfer</h2>
          <SortTable rows={d.applicants} initial={{ key: "power", dir: -1 }} columns={[
            { key: "name", label: "player", render: (m) => <PlayerLink uid={m.uid} name={m.name} /> },
            { key: "fromServer", label: "from", render: (m) => <ServerLink id={m.fromServer} /> },
            { key: "toServer", label: "to", render: (m) => <ServerLink id={m.toServer} /> },
            { key: "power", label: "power", num: true, render: (m) => big(m.power) },
            { key: "heroPower", label: "hero", num: true, render: (m) => big(m.heroPower) },
          ]} />
        </div>
      )}
      </>}
    </div>
  );
}
