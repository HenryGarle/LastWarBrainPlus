import { useState } from "react";
import { int } from "./format.js";
import { Stat, ServerLink } from "./bits.jsx";
import { useJson } from "./PlayerTabs.jsx";

// ---- countries -> regions -> rough zones. The one place this mapping lives (the API sends country codes).
// Country = the profile country (where the game places a player), not the flag they chose to show.
const R = {
  na: ["North America", "americas", "US CA PR GU VI AS MP UM BM"],
  latam: ["Latin America", "americas", "MX BR AR CO CL PE VE EC BO PY UY GT HN SV NI CR PA DO CU JM TT BS BB HT BZ GY SR AW CW KY LC VC GD AG DM KN"],
  eu: ["Europe", "europe", "GB IE FR DE NL BE LU CH AT IT ES PT DK SE NO FI IS MT MC LI AD SM VA PL CZ SK HU SI HR BA RS ME MK AL XK GI JE GG IM FO"],
  eeu: ["Eastern Europe & Russia", "europe", "UA RU BY MD RO BG GR CY EE LV LT"],
  tr: ["Turkey", "europe", "TR"],
  me: ["Middle East", "mena", "SA AE IQ SY JO KW YE LB OM QA PS IL IR BH"],
  naf: ["North Africa", "mena", "EG MA DZ LY TN SD MR"],
  af: ["Sub-Saharan Africa", "europe", "ZA NG KE GH ET TZ UG CM CI SN AO ZW ZM MZ RW BJ BF ML NE TD GA CG CD MG MU BW NA SO DJ ER GM GN LR SL TG RE"],
  cas: ["Central Asia & Caucasus", "asia", "KZ UZ KG TJ TM AZ AM GE AF MN"],
  sa: ["South Asia", "asia", "IN PK BD LK NP MV BT"],
  sea: ["Southeast Asia", "asia", "ID VN TH MY PH SG MM KH LA BN TL"],
  ea: ["East Asia", "asia", "JP KR TW HK CN MO"],
  oc: ["Oceania", "asia", "AU NZ PG FJ NC PF WS TO VU SB"],
};
export const ZONES = { americas: "Americas", europe: "Europe & Africa", mena: "Middle East & N. Africa", asia: "Asia-Pacific", other: "Unknown" };
const REGION = {};
for (const [k, [, , codes]] of Object.entries(R)) for (const c of codes.split(" ")) REGION[c] = k;
export const regionOf = (code) => REGION[code] || "other";
export const regionName = (k) => R[k]?.[0] || "Unknown";
export const zoneOf = (code) => R[regionOf(code)]?.[1] || "other";

let names = null;
try { names = new Intl.DisplayNames(["en"], { type: "region" }); } catch { /* old browser */ }
export const countryName = (code) => {
  if (!code) return "Unknown";
  try { return names?.of(code) || code; } catch { return code; }
};

// Flag emoji. Windows has no flag glyphs, so .flag uses the Noto Color Emoji web font (index.html).
const TAGGED = { ENG: "gbeng", SCO: "gbsct", WLS: "gbwls", WAL: "gbwls" };
// <option>s for a country drop-down: every country we know (/api/x/countries - all tracked players), by name, flag first.
// `here` (code -> count) shows those counts instead, e.g. one server's; countries with none there still list, without a count.
// The <select> needs className="flag-select" so the flags draw on Windows.
export function CountryOptions({ list, here, label }) {
  return [...(list || [])].sort((a, b) => countryName(a.code).localeCompare(countryName(b.code))).map((x) => {
    const n = here ? here.get(x.code) || 0 : x.n;
    return <option key={x.code} value={x.code}>{flagEmoji(x.code)} {label} {countryName(x.code)}{n ? ` (${int(n)})` : ""}</option>;
  });
}

export function flagEmoji(code) {
  const c = (code || "").toUpperCase();
  if (TAGGED[c]) return "\u{1F3F4}" + [...TAGGED[c]].map((ch) => String.fromCodePoint(0xE0000 + ch.charCodeAt(0))).join("") + "\u{E007F}";
  if (!/^[A-Z]{2}$/.test(c)) return "\u{1F3F3}";
  return String.fromCodePoint(...[...c].map((ch) => 0x1F1E6 + ch.charCodeAt(0) - 65));
}
export const Flag = ({ code, title }) => (code ? <span className="flag" title={title || `${countryName(code)} (${code})`}>{flagEmoji(code)}</span> : null);

// ---- summing per-country accumulators (Insights.OnlineAcc JSON) into groups
const zero = (days) => ({ players: 0, active24: 0, active7: 0, active30: 0, hours24: 0, hours7: 0, hours30: 0, hourDays: Array(24).fill(0), perDay: Array(days).fill(0) });
function add(a, b) {
  for (const k of ["players", "active24", "active7", "active30", "hours24", "hours7", "hours30"]) a[k] += b[k];
  b.hourDays.forEach((v, i) => { a.hourDays[i] += v; });
  b.perDay.forEach((v, i) => { a.perDay[i] += v; });
  return a;
}
const keyOf = (mode, code) => (mode === "zone" ? zoneOf(code) : mode === "region" ? regionOf(code) : code || "");
const labelOf = (mode, key) => (mode === "zone" ? ZONES[key] : mode === "region" ? regionName(key) : countryName(key));

/** Groups sorted by active players (7 d), biggest first. */
export function groupCountries(countries, mode, days) {
  const out = {};
  for (const [code, acc] of Object.entries(countries || {})) {
    const k = keyOf(mode, code);
    out[k] = add(out[k] || zero(days), acc);
  }
  return Object.entries(out).map(([key, acc]) => ({ key, label: labelOf(mode, key), acc }))
    .sort((a, b) => b.acc.active7 - a.acc.active7 || b.acc.players - a.acc.players);
}

const COLORS = ["#4da3ff", "#3fb950", "#e3b341", "#f778ba", "#a371f7", "#ff7b72", "#39c5cf", "#d29922", "#8b97a6", "#56d364", "#bc8cff", "#ffa657"];
const hh = (h) => String(h).padStart(2, "0");
const offset = -new Date().getTimezoneOffset() / 60;
const local = (h) => hh((h + offset + 24) % 24);
export const peakHour = (hourDays) => hourDays.reduce((b, v, i) => (v > hourDays[b] ? i : b), 0);
const perDay = (acc) => (acc.active7 ? acc.hours7 / acc.active7 / 7 : 0);

// Stacked bars, one column per label, with an optional small number under each (e.g. "ever seen").
function StackBars({ series, labels, under, tip, height = 120 }) {
  const n = labels.length;
  const total = (i) => series.reduce((s, x) => s + (x.values[i] || 0), 0);
  const max = Math.max(1e-9, ...labels.map((_, i) => total(i)));
  return (
    <div style={{ display: "grid", gridTemplateColumns: `repeat(${n}, 1fr)`, columnGap: n > 24 ? 1 : 3 }}>
      {labels.map((lab, i) => (
        <div key={i} title={tip(i)}>
          <div style={{ display: "flex", flexDirection: "column-reverse", height, borderBottom: "1px solid var(--line)" }}>
            {series.map((s) => (s.values[i] ? <div key={s.key} style={{ flex: "none", height: `${(s.values[i] / max) * (height - 2)}px`, background: s.color }} /> : null))}
          </div>
          <div className="muted mono" style={{ fontSize: n > 24 ? 8 : 10, textAlign: "center", marginTop: 3, overflow: "hidden" }}>{lab}</div>
          {under && <div className="muted mono" style={{ fontSize: 9, textAlign: "center" }}>{under[i] || ""}</div>}
        </div>
      ))}
    </div>
  );
}

/**
 * Who is online when, split by zone / region / country. `online` = the API's OnlineGroup
 * ({days, firstDay, total, countries}); `ever` = optional per-hour "seen at this hour on any day" counts.
 */
export function OnlinePanel({ online, ever, noun = "players", title = "Online by hour (UTC)" }) {
  const [mode, setMode] = useState("zone");
  const [view, setView] = useState("hour");
  if (!online?.total) return null;
  const days = online.days;
  const groups = groupCountries(online.countries, mode, days);
  const top = groups.slice(0, COLORS.length - 1);
  const rest = groups.slice(COLORS.length - 1).reduce((a, g) => add(a, g.acc), zero(days));
  const shown = [...top, ...(groups.length > top.length ? [{ key: "_rest", label: "Everything else", acc: rest }] : [])]
    .map((g, i) => ({ ...g, color: COLORS[i] }));
  const dayLabels = Array.from({ length: days }, (_, i) => {
    const d = new Date(online.firstDay + "T00:00:00Z"); d.setUTCDate(d.getUTCDate() + i); return d.toISOString().slice(5, 10);
  });
  const byHour = shown.map((g) => ({ key: g.key, color: g.color, values: g.acc.hourDays.map((v) => v / days) }));
  const byDay = shown.map((g) => ({ key: g.key, color: g.color, values: g.acc.perDay }));
  const t = online.total;
  const typical = t.hourDays.map((v) => v / days);
  const pk = peakHour(t.hourDays);
  const Noun = noun[0].toUpperCase() + noun.slice(1);
  return (
    <div className="card" style={{ gridColumn: "1 / -1" }}>
      <div className="row" style={{ gap: 8, flexWrap: "wrap" }}>
        <h2 style={{ margin: 0 }}>{view === "hour" ? title : `${Noun} with a sign of life, per day`}</h2>
        <span className="spacer" />
        {[["hour", "by hour"], ["day", "per day"]].map(([k, l]) => <button key={k} className={view === k ? "on" : ""} onClick={() => setView(k)}>{l}</button>)}
        <select value={mode} onChange={(e) => setMode(e.target.value)} title="Split by">
          <option value="none">no split</option><option value="zone">rough time zone</option>
          <option value="region">region</option><option value="country">country</option>
        </select>
      </div>
      {view === "hour" ? (
        <StackBars labels={Array.from({ length: 24 }, (_, h) => hh(h))}
          series={mode === "none" ? [{ key: "all", color: COLORS[0], values: typical }] : byHour}
          under={ever} height={130}
          tip={(h) => `${hh(h)}:00 UTC (${local(h)}:00 your time)\ntypical day: ${typical[h].toFixed(1)} ${noun} online${ever ? `\nseen at this hour on any day: ${ever[h]}` : ""}` +
            (mode === "none" ? "" : "\n" + shown.map((g) => `${g.label}: ${(g.acc.hourDays[h] / days).toFixed(1)}`).join("\n"))} />
      ) : (
        <StackBars labels={dayLabels} series={mode === "none" ? [{ key: "all", color: COLORS[0], values: t.perDay }] : byDay} height={110}
          tip={(i) => `${dayLabels[i]}: ${t.perDay[i]} ${noun}` + (mode === "none" ? "" : "\n" + shown.map((g) => `${g.label}: ${g.acc.perDay[i]}`).join("\n"))} />
      )}
      {mode !== "none" && (
        <p className="small muted" style={{ marginTop: 6 }}>
          {shown.map((g) => <span key={g.key} style={{ marginRight: 12, whiteSpace: "nowrap" }}><span style={{ color: g.color }}>■</span> {mode === "country" && g.key ? <Flag code={g.key} /> : null} {g.label}</span>)}
        </p>
      )}
      <p className="small muted">
        {view === "hour"
          ? <>Bar = {noun} online in that UTC hour on a typical day of the last {days} days (days with a sign in that hour ÷ {days}). Peak {hh(pk)}:00 UTC ({local(pk)}:00 your time), ≈{typical[pk].toFixed(1)} {noun}.{ever ? " The number under each bar is how many were seen at that hour on any day." : ""} A floor: only moments a reading catches count, and the hours readings are taken in read high.</>
          : <>Each bar is one UTC day; one counts if they logged in, donated or chatted that day.</>}
      </p>
      {mode !== "none" && (
        <table className="small">
          <thead><tr><th>{mode === "country" ? "country" : mode}</th><th>{noun}</th><th>active 24 h</th><th>active 7 d</th><th>active 30 d</th><th title="per active one, last 7 days">hours / day</th><th>peak (UTC)</th></tr></thead>
          <tbody>
            {groups.map((g) => (
              <tr key={g.key}>
                <td>{mode === "country" && <Flag code={g.key} />} {g.label}{mode === "country" && g.key ? <span className="muted"> · {regionName(regionOf(g.key))}</span> : null}</td>
                <td className="mono">{int(g.acc.players)}</td><td className="mono">{int(g.acc.active24)}</td>
                <td className="mono">{int(g.acc.active7)}</td><td className="mono">{int(g.acc.active30)}</td>
                <td className="mono">{perDay(g.acc).toFixed(1)}</td>
                <td className="mono">{g.acc.hours30 ? `${hh(peakHour(g.acc.hourDays))}:00` : "–"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

/** Stat tiles for a group's active counts and hours. */
export function OnlineStats({ total, noun = "players" }) {
  if (!total) return null;
  return (
    <>
      <Stat label="Active 24 h" value={`${int(total.active24)} / ${int(total.players)}`} sub={`${int(total.hours24)} hours online between them`} />
      <Stat label="Active 7 d" value={int(total.active7)} sub={`${perDay(total).toFixed(1)} h/day each, on average`} />
      <Stat label="Active 30 d" value={int(total.active30)} sub={`${int(total.hours30)} ${noun.replace(/s$/, "")}-hours seen`} />
    </>
  );
}

/** One player's active hours (Insights.OnlineOf). */
export function PlayerOnline({ o }) {
  if (!o) return null;
  return <Stat label="Hours seen online" value={`${o.hours24} / ${o.hours7} / ${o.hours30}`}
    sub={`last 24 h / 7 d / 30 d · on ${o.days7} of 7 days, ${o.days30} of 30`} />;
}

/** Server page: active counts, online by hour split by zone / region / country. */
export function ServerOnline({ server }) {
  const [days, setDays] = useState(30);
  const d = useJson(`/api/x/server/${server}/online?days=${days}`);
  if (!d) return <div className="card"><p className="muted small">Loading online activity…</p></div>;
  if (d.error || !d.total?.players) return null;
  return (
    <>
      <div className="row small" style={{ gap: 8 }}>
        <span className="muted">Online window</span>
        {[7, 14, 30].map((n) => <button key={n} className={days === n ? "on" : ""} onClick={() => setDays(n)}>{n} days</button>)}
        <a href="#/x/online" style={{ marginLeft: 8 }}>compare servers →</a>
      </div>
      <div className="stats"><OnlineStats total={d.total} /></div>
      <OnlinePanel online={d} title={`Players online by hour (UTC) - #${server}`} />
    </>
  );
}

/** Every server side by side (Explore → Server activity). */
export function OnlineComparePage() {
  const [days, setDays] = useState(7);
  const [split, setSplit] = useState("zone");
  const d = useJson(`/api/x/online?days=${days}`);
  if (!d) return <p className="muted">Loading every server… (the first load after a restart takes a few seconds)</p>;
  if (d.error) return <p className="err">Could not load ({d.error}).</p>;
  const keys = split === "zone" ? Object.keys(ZONES) : Object.keys(R).concat("other");
  const label = (k) => (split === "zone" ? ZONES[k] : regionName(k));
  const share = (s) => {
    const out = Object.fromEntries(keys.map((k) => [k, 0]));
    for (const [code, [, a7]] of Object.entries(s.countries)) out[split === "zone" ? zoneOf(code) : regionOf(code)] += a7;
    return out;
  };
  const colour = Object.fromEntries(keys.map((k, i) => [k, COLORS[i % COLORS.length]]));
  return (
    <div className="grid">
      <div className="row small" style={{ gap: 8 }}>
        <span className="muted">Window</span>
        {[7, 14, 30].map((n) => <button key={n} className={days === n ? "on" : ""} onClick={() => setDays(n)}>{n} days</button>)}
        <span className="muted" style={{ marginLeft: 12 }}>Split</span>
        <select value={split} onChange={(e) => setSplit(e.target.value)}><option value="zone">rough time zone</option><option value="region">region</option></select>
      </div>
      <div className="card">
        <h2>Server activity, side by side</h2>
        <table className="small">
          <thead><tr><th>server</th><th>players</th><th>active 24 h</th><th>active 7 d</th><th>active 30 d</th><th title="per active player, last 7 days">hours / day</th><th>peak (UTC)</th><th title="typical day">online at peak</th><th>who is active (7 d)</th></tr></thead>
          <tbody>
            {d.servers.map((s) => {
              const t = s.total, pk = peakHour(t.hourDays), sh = share(s), sum = Object.values(sh).reduce((a, b) => a + b, 0) || 1;
              return (
                <tr key={s.server}>
                  <td><ServerLink id={s.server} /></td>
                  <td className="mono">{int(t.players)}</td><td className="mono">{int(t.active24)}</td>
                  <td className="mono">{int(t.active7)}</td><td className="mono">{int(t.active30)}</td>
                  <td className="mono">{perDay(t).toFixed(1)}</td>
                  <td className="mono">{hh(pk)}:00</td>
                  <td className="mono">{(t.hourDays[pk] / d.days).toFixed(0)}</td>
                  <td style={{ minWidth: 180 }}>
                    <div style={{ display: "flex", height: 10, borderRadius: 3, overflow: "hidden", background: "var(--line)" }}
                      title={keys.filter((k) => sh[k]).map((k) => `${label(k)}: ${sh[k]} (${Math.round((sh[k] / sum) * 100)}%)`).join("\n")}>
                      {keys.map((k) => (sh[k] ? <div key={k} style={{ width: `${(sh[k] / sum) * 100}%`, background: colour[k] }} /> : null))}
                    </div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
        <p className="small muted" style={{ marginTop: 6 }}>
          {keys.map((k) => <span key={k} style={{ marginRight: 12, whiteSpace: "nowrap" }}><span style={{ color: colour[k] }}>■</span> {label(k)}</span>)}
        </p>
        <p className="small muted">
          Players = everyone whose base was last seen on that server. Active = logged in (a last-seen change that came with a change in power, kills or HQ that week), donated or chatted.
          Hours / day = distinct hours with a sign per active player - a floor, set by how often profiles are read. Country is the profile country.
        </p>
      </div>
    </div>
  );
}
