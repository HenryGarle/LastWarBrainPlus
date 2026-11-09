import { useEffect, useMemo, useState } from "react";
import { LineChart, SortTable } from "./charts.jsx";
import { big, int, when, ago } from "./format.js";
import { Stat } from "./bits.jsx";

export function useJson(url) {
  const [d, setD] = useState(null);
  useEffect(() => {
    let live = true;
    setD(null);
    if (!url) return undefined;   // no url = nothing to load (a caller that already has the data)
    fetch(url).then((r) => (r.ok ? r.json() : { error: `${r.status}` })).then((x) => live && setD(x)).catch(() => {});
    return () => { live = false; };
  }, [url]);
  return d;
}

// One colour per kind of evidence, used by the hour chart, the day strip and the event list.
export const KIND = {
  seen: ["In game (profile)", "#4da3ff"],
  donated: ["Donated", "#3fb950"],
  chat: ["Chat", "#e3b341"],
  rise: ["Power rose", "#a371f7"],
  other: ["Troops, kills, HQ, moves", "#8b97a6"],
};
const kindOf = (k) => (KIND[k] ? k : "other");

// When in the day, stacked by the kind of evidence. Hours are UTC (and your local time in the tip).
export function StackedHours({ hours }) {
  const kinds = Object.keys(KIND);
  const total = (h) => kinds.reduce((s, k) => s + (hours[k]?.[h] || 0), 0);
  const max = Math.max(1, ...Array.from({ length: 24 }, (_, h) => total(h)));
  const offset = -new Date().getTimezoneOffset() / 60;
  const local = (h) => String((h + offset + 24) % 24).padStart(2, "0");
  let best = 0, bestN = -1;
  for (let h = 0; h < 24; h++) { const s = total(h) + total((h + 1) % 24) + total((h + 2) % 24); if (s > bestN) { bestN = s; best = h; } }
  const sum = Array.from({ length: 24 }, (_, h) => total(h)).reduce((a, b) => a + b, 0);
  return (
    <>
      {/* Own grid rather than .hour-row: every column is bars (fixed height, bottom-aligned) over a label row, so bars never sit on the times. A non-zero part is at least 2px so rare kinds still show beside chat. */}
      <div style={{ display: "grid", gridTemplateColumns: "repeat(24, 1fr)", columnGap: 3 }}>
        {Array.from({ length: 24 }, (_, h) => (
          <div key={h} title={`${String(h).padStart(2, "0")}:00 UTC (${local(h)}:00 your time)\n${kinds.map((k) => `${KIND[k][0]}: ${hours[k]?.[h] || 0}`).join("\n")}`}>
            <div style={{ display: "flex", flexDirection: "column-reverse", height: 80, overflow: "hidden", borderBottom: "1px solid var(--line)" }}>
              {kinds.map((k) => (hours[k]?.[h] ? <div key={k} style={{ flex: "none", height: `${Math.max(2, (hours[k][h] / max) * 78)}px`, background: KIND[k][1] }} /> : null))}
            </div>
            <div className="muted mono" style={{ fontSize: 10, textAlign: "center", marginTop: 3 }}>{String(h).padStart(2, "0")}</div>
          </div>
        ))}
      </div>
      <p className="small muted">
        {Object.keys(KIND).map((k) => <span key={k} style={{ marginRight: 12 }}><span style={{ color: KIND[k][1] }}>■</span> {KIND[k][0]}</span>)}
      </p>
      <p className="small">
        {sum === 0 ? "No timed evidence yet." : <>Busiest 3 hours: <b>{String(best).padStart(2, "0")}:00–{String((best + 3) % 24).padStart(2, "0")}:00 UTC</b> ({local(best)}:00–{local((best + 3) % 24)}:00 your time), from {int(sum)} moments.</>}
      </p>
    </>
  );
}

// Last 30 days, one cell per day, filled if anything shows they were there.
function DayStrip({ days }) {
  return (
    <div className="row" style={{ gap: 3, flexWrap: "wrap" }}>
      {days.map((d) => {
        const kinds = Object.keys(KIND).filter((k) => d[k] > 0);
        return (
          <div key={d.day} title={`${d.day}\n${Object.keys(KIND).map((k) => `${KIND[k][0]}: ${d[k]}`).join("\n")}`}
            style={{ width: 16, height: 22, borderRadius: 3, border: "1px solid #333",
              background: kinds.length ? `linear-gradient(${kinds.map((k, i) => `${KIND[k][1]} ${(i / kinds.length) * 100}% ${((i + 1) / kinds.length) * 100}%`).join(",")})` : "transparent" }} />
        );
      })}
    </div>
  );
}

const Ago = ({ iso }) => (iso ? <span title={when(iso)}>{ago(iso)}</span> : "–");

// One day's hours (UTC), from the timed events - pick the day; today by default.
function DayHours({ days, events }) {
  const today = new Date().toISOString().slice(0, 10);
  const [day, setDay] = useState(today);
  const recent = days.slice(-14).reverse();   // newest first
  const count = (d) => Object.keys(KIND).reduce((s, k) => s + (d[k] || 0), 0);
  const hours = useMemo(() => {
    const h = Object.fromEntries(Object.keys(KIND).map((k) => [k, new Array(24).fill(0)]));
    for (const e of events) {
      if (e.kind === "touch" || !e.at?.startsWith(day)) continue;
      h[kindOf(e.kind)][Number(e.at.slice(11, 13))]++;
    }
    return h;
  }, [events, day]);
  const label = (d) => (d === today ? "today" : new Date(`${d}T12:00:00Z`).toLocaleDateString(undefined, { weekday: "short", day: "numeric", month: "short" }));
  return (
    <>
      <div className="day-pick">
        {recent.map((d) => (
          <button key={d.day} className={`${d.day === day ? "on" : ""} ${count(d) ? "" : "empty"}`} onClick={() => setDay(d.day)} title={`${count(d)} moments`}>
            {label(d.day)}
          </button>
        ))}
      </div>
      <StackedHours hours={hours} />
    </>
  );
}

export function ActivityTab({ uid }) {
  const d = useJson(`/api/x/player/${encodeURIComponent(uid)}/activity`);
  if (!d) return <p className="muted">Loading activity…</p>;
  if (d.error) return <p className="err">Could not load activity ({d.error}).</p>;
  const don = d.donation;
  return (
    <div className="grid">
      <div className="stats">
        <Stat label="Activity score" value={`${d.score} / 100`} sub={d.lastActive ? `last sign of life ${ago(d.lastActive)}` : "no sign of life"} />
        <Stat label="In game (profile)" value={<Ago iso={d.last.seen} />} sub={`${int(d.readings.seenMoments)} distinct moment(s)`} />
        <Stat label="Last donation" value={<Ago iso={d.last.donated} />} sub={don ? `${int(don.today)} today · ${big(don.week)} this week` : "no roster read"} />
        <Stat label="Power last rose" value={<Ago iso={d.last.rise} />} sub={`${d.gain7 >= 0 ? "+" : ""}${big(d.gain7)} in 7 days · ${d.gain30 >= 0 ? "+" : ""}${big(d.gain30)} in 30`} />
        <Stat label="Chat" value={<Ago iso={d.last.chat} />} sub={`${int(d.chat7)} in 7 days · ${int(d.chat30)} in 30 · ${int(d.chatAll)} all`} />
        <Stat label="Troops changed" value={<Ago iso={d.last.troops} />} sub="training or losses between profiles" />
        <Stat label="Kills went up" value={<Ago iso={d.last.kills} />} sub="fought between two readings" />
        <Stat label="Moved" value={<Ago iso={d.last.moved} />} sub="base, alliance or home server" />
      </div>

      <div className="cards">
        <div className="card">
          <h2>How the score is made</h2>
          <table className="small">
            <tbody>
              {d.parts.map((p) => (
                <tr key={p.name}>
                  <td>{p.name}</td>
                  <td style={{ width: 120 }}>
                    <div style={{ background: "#333", borderRadius: 3, height: 8 }}>
                      <div style={{ width: `${(p.score / p.of) * 100}%`, background: "var(--accent)", height: 8, borderRadius: 3 }} />
                    </div>
                  </td>
                  <td className="mono">{p.score}/{p.of}</td>
                  <td className="muted">{p.why}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="small muted">
            Readings behind this: {int(d.readings.power)} power, {int(d.readings.profiles)} profiles, {int(d.readings.rosters)} alliance rosters, {int(d.readings.map)} map sightings.
            {d.svip ? ` SVIP ${d.svip}.` : ""}{d.monthCard ? ` Monthly card ${new Date(d.monthCard) > new Date() ? "until" : "ended"} ${when(d.monthCard)}.` : ""}
          </p>
        </div>

        <div className="card">
          <h2>When in the day</h2>
          <StackedHours hours={d.hours} />
          <p className="small muted">A power rise only counts here when the two readings either side were 3 hours or less apart.</p>
        </div>

        <div className="card">
          <h2>On one day</h2>
          <DayHours days={d.days} events={d.events} />
          <p className="small muted">From the newest 300 timed moments, so a very busy player's older days may be incomplete. Hours are UTC.</p>
        </div>

        <div className="card">
          <h2>Last 30 days</h2>
          <DayStrip days={d.days} />
          <p className="small muted">Each square is a day (oldest left). Coloured parts show which kinds of evidence were seen that day; blank means nothing — which may just mean nothing was read that day.</p>
        </div>

        <div className="card">
          <h2>Donations</h2>
          {!don ? <p className="muted">No alliance roster read yet (intel.sweep).</p> : (
            <>
              <p className="small">
                <b>#{don.place}</b> of {don.members} in the alliance this week with {big(don.week)} (alliance median {big(don.median)}).
                {" "}{int(don.gaveToday)} of {don.members} members have donated today. Last donation {don.last ? `${ago(don.last)} (${when(don.last)})` : "never seen"}.
              </p>
              <SortTable rows={don.history} initial={{ key: "at", dir: -1 }} columns={[
                { key: "at", label: "Roster read", render: (r) => when(r.at) },
                { key: "today", label: "Today", render: (r) => int(r.today), num: true },
                { key: "week", label: "This week", render: (r) => big(r.week), num: true },
                { key: "last", label: "Last donated", render: (r) => (r.last ? when(r.last) : "–") },
              ]} />
            </>
          )}
        </div>

        <div className="card" style={{ gridColumn: "1 / -1" }}>
          <h2>Every sign of life</h2>
          <SortTable rows={d.events} initial={{ key: "at", dir: -1 }} limit={300} empty="Nothing recorded yet." columns={[
            { key: "at", label: "When", render: (r) => <span title={r.from ? `between ${when(r.from)} and ${when(r.at)}` : ""}>{when(r.at)}{r.from && <span className="muted"> (since {when(r.from)})</span>}</span> },
            { key: "kind", label: "Kind", render: (r) => <span><span style={{ color: KIND[kindOf(r.kind)][1] }}>■</span> {r.kind}</span> },
            { key: "detail", label: "What" },
          ]} />
          <p className="small muted">Chat messages aren't listed here; they're on the Overview tab and counted in the hour chart.</p>
        </div>
      </div>
    </div>
  );
}

export function DroneTab({ uid }) {
  const d = useJson(`/api/x/player/${encodeURIComponent(uid)}/drone`);
  if (!d) return <p className="muted">Loading drone…</p>;
  if (d.error) return <p className="err">Could not load the drone ({d.error}).</p>;
  if (!d.history?.length) return <p className="muted">No drone read for this player yet. Drone reads (intel.players) cover the top 100 of each tracked server.</p>;
  const l = d.latest;
  const pctText = (v) => `${Math.round(v)}th percentile`;
  return (
    <div className="grid">
      <div className="stats">
        <Stat label="Drone level" value={int(l.level)} sub={`as of ${when(l.at)}`} />
        <Stat label="Drone power" value={big(l.power)} sub={d.share != null ? `${d.share.toFixed(1)}% of their total power` : ""} />
        <Stat label="Chip level" value={int(l.chip)} sub="skill chips, summed" />
        <Stat label="Career" value={l.careerLevel ? `lv ${l.careerLevel}` : "–"} sub={l.careerType ? `type ${l.careerType}` : ""} />
        <Stat label="Readings" value={int(d.history.length)} sub="a new one each intel.players run" />
      </div>
      <div className="cards">
        <div className="card">
          <h2>Against other players</h2>
          <table className="small">
            <thead><tr><th>Compared with</th><th>Players</th><th>Drone level</th><th>Chip level</th><th>Drone power</th></tr></thead>
            <tbody>
              {d.compare.map((c) => (
                <tr key={c.label}>
                  <td>{c.label}</td>
                  <td className="mono">{int(c.players)}</td>
                  <td title={`median ${c.levelMedian}`}>{pctText(c.levelPct)} <span className="muted">(median {c.levelMedian})</span></td>
                  <td title={`median ${c.chipMedian}`}>{pctText(c.chipPct)} <span className="muted">(median {c.chipMedian})</span></td>
                  <td>{pctText(c.powerPct)} <span className="muted">(median {big(c.powerMedian)})</span></td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="small muted">A player well above their power peers on drone and chips has put more into combat stats than their power shows. {d.coverage}.</p>
        </div>
        <div className="card">
          <h2>Drone level over time</h2>
          <LineChart height={140} points={d.history.map((h) => ({ x: h.at, y: h.level }))} label="drone level" format={int} />
          <h2 style={{ marginTop: 12 }}>Chip level over time</h2>
          <LineChart height={140} points={d.history.map((h) => ({ x: h.at, y: h.chip }))} label="chip level" format={int} />
          {d.history.length < 2 && <p className="small muted">Only one reading so far — the lines fill in as intel.players runs again.</p>}
        </div>
        {d.props?.length > 0 && (
          <div className="card">
            <h2>Drone stats</h2>
            <SortTable rows={d.props} initial={{ key: "id", dir: 1 }} columns={[
              { key: "name", label: "Stat", render: (r) => (r.name && !r.name.startsWith("effectnumber_") ? r.name : `#${r.id}`) },
              { key: "value", label: "Value", render: (r) => int(Math.round(r.value)), num: true },
            ]} />
          </div>
        )}
      </div>
    </div>
  );
}
