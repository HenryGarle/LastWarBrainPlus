import { useState } from "react";
import { LineChart, SortTable } from "./charts.jsx";
import { big, int, when, ago } from "./format.js";
import { Stat, PlayerLink } from "./bits.jsx";
import { StackedHours, useJson } from "./PlayerTabs.jsx";
import { Flag, OnlinePanel, OnlineStats } from "./Online.jsx";

const hh = (h) => String(h).padStart(2, "0");
const offset = -new Date().getTimezoneOffset() / 60;
const local = (h) => hh((h + offset + 24) % 24);

// Hero power on at each UTC hour, one bar per hour with its time under it. Click a bar to see who.
function StrengthByHour({ hours, total, pick, picked, sampled }) {
  const max = Math.max(1, ...hours.map((h) => h.hero));
  return (
    <div style={{ display: "grid", gridTemplateColumns: "repeat(24, 1fr)", columnGap: 3 }}>
      {hours.map((h) => (
        <div key={h.hour} onClick={() => pick(h.hour === picked ? null : h.hour)} style={{ cursor: "pointer" }}
          title={`${hh(h.hour)}:00 UTC (${local(h.hour)}:00 your time)\n${int(h.members)} members · ${big(h.hero)} hero power${total ? ` (${Math.round((h.hero / total) * 100)}% of the alliance)` : ""}`}>
          <div style={{ display: "flex", flexDirection: "column-reverse", height: 110, borderBottom: "1px solid var(--line)" }}>
            <div style={{ flex: "none", height: `${h.hero ? Math.max(2, (h.hero / max) * 108) : 0}px`,
              background: h.hour === picked ? "var(--warn)" : "var(--accent)", borderRadius: "2px 2px 0 0" }} />
          </div>
          <div className="muted mono" style={{ fontSize: 10, textAlign: "center", marginTop: 3 }}>{hh(h.hour)}</div>
          <div className="muted mono" style={{ fontSize: 9, textAlign: "center" }}>{h.members || ""}</div>
          <div title={sampled?.[h.hour] ? `readings taken in this hour on ${sampled[h.hour]} day(s)` : ""}
            style={{ fontSize: 9, textAlign: "center", color: "var(--warn)" }}>{sampled?.[h.hour] ? "▲" : ""}</div>
        </div>
      ))}
    </div>
  );
}

// 24 little cells: the hours a member has been seen in.
const HourStrip = ({ hours }) => (
  <span style={{ display: "inline-flex", gap: 1 }} title={hours.length ? hours.map((h) => `${hh(h)}:00`).join(", ") + " UTC" : "no timed sign"}>
    {Array.from({ length: 24 }, (_, h) => (
      <span key={h} style={{ width: 4, height: 12, background: hours.includes(h) ? "var(--accent)" : "var(--line)" }} />
    ))}
  </span>
);

export function AllianceActivityTab({ id }) {
  const [days, setDays] = useState(30);
  const [picked, setPicked] = useState(null);
  const d = useJson(`/api/x/alliance/${encodeURIComponent(id)}/activity?days=${days}`);
  if (!d) return <p className="muted">Loading activity…</p>;
  if (d.error) return <p className="err">Could not load activity ({d.error}).</p>;
  if (!d.memberCount) return <p className="muted">No members known for this alliance yet.</p>;
  const byUid = Object.fromEntries(d.members.map((m) => [m.uid, m]));
  const best = [...d.hours].sort((a, b) => b.hero - a.hero)[0];
  const worst3 = (() => {   // weakest 3-hour window
    let at = 0, low = Infinity;
    for (let h = 0; h < 24; h++) { const s = [0, 1, 2].reduce((t, i) => t + d.hours[(h + i) % 24].hero, 0); if (s < low) { low = s; at = h; } }
    return at;
  })();
  const pickedHour = picked === null ? null : d.hours[picked];

  return (
    <div className="grid">
      <div className="row small" style={{ gap: 8 }}>
        <span className="muted">Window</span>
        {[7, 14, 30].map((n) => <button key={n} className={days === n ? "on" : ""} onClick={() => setDays(n)}>{n} days</button>)}
      </div>
      <div className="stats">
        <Stat label="Members" value={int(d.memberCount)} sub={d.rosterAt ? `roster read ${when(d.rosterAt)}` : "from map / profiles"} />
        <Stat label="Hero power (all)" value={big(d.totalHero)} sub={`${d.heroKnown} of ${d.memberCount} members known`} />
        <Stat label="With timed evidence" value={`${d.timed} / ${d.memberCount}`} sub={`${int(d.moments)} moments in ${d.days} days`} />
        <Stat label="Strongest hour" value={best?.hero ? `${hh(best.hour)}:00 UTC` : "–"} sub={best?.hero ? `${big(best.hero)} hero power on · ${best.members} members` : ""} />
        <Stat label="Weakest 3 hours" value={`${hh(worst3)}:00–${hh((worst3 + 3) % 24)}:00`} sub={`UTC · ${local(worst3)}:00–${local((worst3 + 3) % 24)}:00 your time`} />
        <OnlineStats total={d.online?.total} noun="members" />
      </div>

      <div className="cards">
        <div className="card" style={{ gridColumn: "1 / -1" }}>
          <h2>Hero power on, by hour (UTC)</h2>
          <StrengthByHour hours={d.hours} total={d.totalHero} pick={setPicked} picked={picked} sampled={d.sampled} />
          <p className="small muted">
            Each bar sums the hero power of members seen in the game at that hour on any day in the last {d.days} days
            (numbers under the times are how many members). Click a bar to see who. Members with no timed sign aren't counted anywhere,
            so this is a floor, not a ceiling.
          </p>
          <p className="small" style={{ color: "var(--warn)" }}>
            ▲ = hours we took readings in. Anyone in the game then shows as "seen a few minutes ago", so those hours read high
            until readings are spread across the day (an hourly profile check would fix this).
          </p>
          {pickedHour && (
            <>
              <h2 style={{ marginTop: 10 }}>On at {hh(pickedHour.hour)}:00 UTC ({local(pickedHour.hour)}:00 your time) — {pickedHour.members} members, {big(pickedHour.hero)}</h2>
              <table className="small">
                <tbody>
                  {pickedHour.top.map((u) => (
                    <tr key={u}>
                      <td><PlayerLink uid={u} name={byUid[u]?.name} /></td>
                      <td className="mono">{big(byUid[u]?.hero)}{byUid[u]?.heroEstimated ? " ≈" : ""}</td>
                      <td><HourStrip hours={byUid[u]?.hours || []} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {pickedHour.members > pickedHour.top.length && <p className="small muted">…and {pickedHour.members - pickedHour.top.length} more.</p>}
            </>
          )}
        </div>

        <OnlinePanel online={d.online} ever={d.hours.map((h) => h.members)} noun="members" title="Members online, by hour (UTC)" />

        <div className="card">
          <h2>When in the day (every moment)</h2>
          <StackedHours hours={d.byKind} />
          <p className="small muted">Power rises and troop / kill / HQ changes only fall between two readings: they count at the midpoint when the gap is 3 hours or less, and never as &quot;online&quot;.</p>
        </div>

        <div className="card">
          <h2>Hero power by last sign of life</h2>
          <table className="small">
            <tbody>
              {d.recency.map((r) => (
                <tr key={r.label}>
                  <td>{r.label}</td>
                  <td style={{ width: 160 }}>
                    <div style={{ background: "var(--line)", borderRadius: 3, height: 8 }}>
                      <div style={{ width: `${d.totalHero ? (r.hero / d.totalHero) * 100 : 0}%`, background: "var(--accent)", height: 8, borderRadius: 3 }} />
                    </div>
                  </td>
                  <td className="mono">{big(r.hero)}</td>
                  <td className="muted">{d.totalHero ? `${Math.round((r.hero / d.totalHero) * 100)}%` : ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <h2 style={{ marginTop: 14 }}>Members with a sign of life, per day</h2>
          <LineChart height={130} points={d.perDay.map((p) => ({ x: p.day, y: p.members }))} label="members" format={int} />
        </div>

        <div className="card" style={{ gridColumn: "1 / -1" }}>
          <h2>Members</h2>
          <SortTable rows={d.members} initial={{ key: "hero", dir: -1 }} columns={[
            { key: "name", label: "Player", render: (m) => <><Flag code={m.country} /> <PlayerLink uid={m.uid} name={m.name} /></> },
            { key: "rank", label: "R", num: true },
            { key: "hero", label: "Hero power", num: true, render: (m) => (m.hero ? `${big(m.hero)}${m.heroEstimated ? " ≈" : ""}` : "–") },
            { key: "last", label: "Last sign", sort: (m) => m.last || "", render: (m) => (m.last ? <span title={when(m.last)}>{ago(m.last)}</span> : "–") },
            { key: "moments", label: "Moments", num: true },
            { key: "on24", label: "Online h 24h", num: true, sort: (m) => m.online?.hours24 || 0, render: (m) => m.online?.hours24 ?? "–" },
            { key: "on7", label: "7d", num: true, sort: (m) => m.online?.hours7 || 0, render: (m) => m.online?.hours7 ?? "–" },
            { key: "on30", label: "30d", num: true, sort: (m) => m.online?.hours30 || 0, render: (m) => m.online?.hours30 ?? "–" },
            { key: "hours", label: "Hours seen (UTC 00→23)", sort: (m) => m.hours.length, render: (m) => <HourStrip hours={m.hours} /> },
            { key: "hours24", label: "Last 24 hours", sort: (m) => (m.hours24 || []).length, render: (m) => <HourStrip hours={m.hours24 || []} /> },
          ]} />
          <p className="small muted">≈ = estimated from their profile (0.78 × (power − troops)); otherwise their hero board value.</p>
        </div>
      </div>
    </div>
  );
}
