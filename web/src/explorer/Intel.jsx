import { useEffect, useState } from "react";
import { BarList, LineChart, SortTable } from "./charts.jsx";
import { ago, big, int, pct, when } from "./format.js";
import { PlayerLink, ServerLink, Stat, Tag } from "./bits.jsx";
import { useHashParam } from "../nav.js";

// Views on intel.sweep: rosters with last seen and donations, territory, presidents, and a
// transparent fight estimate. "Last seen" = when the game last refreshed the player (profile
// lastUpdateTime); "donated" = their last alliance-tech donation. Both prove they were in the game.

// ponytail: types 1-4 follow the game's official strings 391063-391066 in order; unconfirmed.
export const OFFICIAL = { 1: "Warlord", 2: "Recruiter", 3: "Muse", 4: "Butler" };

function useJson(url) {
  const [d, setD] = useState(null);
  useEffect(() => {
    let live = true;
    setD(null);
    fetch(url).then((r) => (r.ok ? r.json() : null)).then((x) => live && setD(x)).catch(() => {});
    return () => { live = false; };
  }, [url]);
  return d;
}

const hoursSince = (iso) => (iso ? (Date.now() - new Date(iso).getTime()) / 3.6e6 : Infinity);
export const Seen = ({ iso }) => {
  const h = hoursSince(iso);
  const cls = h <= 24 ? "status-good" : h <= 72 ? "" : "status-muted";
  return iso ? <span className={cls} title={when(iso)}>{ago(iso)}</span> : <span className="muted">–</span>;
};
export const later = (a, b) => (!a ? b : !b ? a : a > b ? a : b);

function Buckets({ s, total }) {
  if (!s) return null;
  const rows = [
    ["in the last day", s.seen1d], ["1–3 days", s.seen3d - s.seen1d], ["3–7 days", s.seen7d - s.seen3d],
    ["over a week", s.gone7d], ["not known yet", s.unknown],
  ];
  return <BarList format={int} rows={rows.map(([label, v]) => ({ key: label, label, value: v ?? 0, detail: pct(v ?? 0, total) }))} />;
}

export function AllianceIntelCards({ id }) {
  const d = useJson(`/api/x/alliance/${encodeURIComponent(id)}/intel`);
  if (!d) return null;
  if (!d.roster) return (
    <div className="card"><h2>Activity and roster</h2>
      <p className="muted small">No roster yet - run Alliance &amp; server intel (it is part of Refresh stats).</p></div>
  );
  const info = d.info || {};
  const a = d.activity[d.activity.length - 1] || {};
  const officers = d.roster.filter((m) => m.official);
  const r5 = d.roster.find((m) => m.rank === 5);
  const r4 = d.roster.filter((m) => m.rank === 4);
  return (
    <>
      <div className="stats">
        <Stat label="In the game today" value={pct(a.seen1d, a.members)} sub={`${int(a.seen1d)} of ${int(a.members)} seen or donated in 24 h`} />
        <Stat label="Active this week" value={pct(a.seen7d, a.members)} sub={`${int(a.gone7d)} not seen for over a week`} />
        <Stat label="Donated today" value={int(a.donatedToday)} sub={`${big(a.donationToday)} tech points`} />
        <Stat label="Fight power" value={big(info.fightPower)} sub={`gift level ${info.giftLevel ?? "–"} · ${int(info.engagement)} engagement`} />
        <Stat label="Cities held" value={int(d.cities.length)} sub={d.cities.length ? `oldest since ${when(d.cities.map((c) => c.since).filter(Boolean).sort()[0])}` : "none"} />
        <Stat label="Strongholds" value={int(d.strongholds.length)} sub={d.strongholds.length ? `on ${[...new Set(d.strongholds.map((s) => `#${s.server}`))].join(", ")}` : "none"} />
        <Stat label="Turnover" value={`+${d.joined7d} / −${d.left.length}`} sub="joined this week / left since the roster before" />
      </div>

      <div className="two">
        <div className="card">
          <h2>When members were last in the game</h2>
          <Buckets s={a} total={a.members} />
          {d.activity.length > 1 && <LineChart height={130} format={int} label="members in the game in the last 24 h, per roster"
            points={d.activity.map((x) => ({ x: x.at, y: x.seen1d }))} />}
        </div>
        <div className="card">
          <h2>The alliance card</h2>
          <div className="small kv">
            <div>Leader</div><div>{r5 ? <PlayerLink uid={r5.uid} name={r5.name} /> : info.leaderName || "–"}</div>
            <div>R4s</div><div>{r4.length ? r4.map((m, i) => <span key={m.uid}>{i ? ", " : ""}<PlayerLink uid={m.uid} name={m.name} /></span>) : "–"}</div>
            <div>Officers</div><div>{officers.length ? officers.map((m, i) => <span key={m.uid}>{i ? ", " : ""}{OFFICIAL[m.official] || `#${m.official}`} <PlayerLink uid={m.uid} name={m.name} /></span>) : "–"}</div>
            <div>Founded</div><div>{when(info.createdAt)} on <ServerLink id={info.createServer} /></div>
            <div>Joining</div><div>{info.joinApproval ? "by approval" : "open"}, HQ {info.levelLimit}+{info.powerLimit ? `, ${big(info.powerLimit)} power` : ""}</div>
            <div>Members</div><div>{info.members}/{info.maxMembers}</div>
          </div>
          {info.announce && <details style={{ marginTop: 8 }}><summary className="small">Announcement (often lists allies)</summary>
            <pre className="small announce">{info.announce}</pre></details>}
        </div>
      </div>

      {d.hours.some((n) => n > 0) && (
        <div className="card">
          <h2>When they are in the game <span className="muted small">UTC, from donations and last-seen moments</span></h2>
          <HourBars hours={d.hours} />
        </div>
      )}

      <div className="card">
        <h2>Roster <span className="muted small">as of {when(d.rosterAt)}</span></h2>
        <SortTable initial={{ key: "power", dir: -1 }} rows={d.roster} columns={[
          { key: "name", label: "player", render: (m) => <PlayerLink uid={m.uid} name={m.name} /> },
          { key: "rank", label: "rank", num: true, render: (m) => <>R{m.rank}{m.official ? <span className="muted small"> {OFFICIAL[m.official]}</span> : null}</> },
          { key: "power", label: "power", num: true, render: (m) => big(m.power) },
          { key: "heroPower", label: "hero", num: true, sort: (m) => m.heroPower ?? m.heroEst ?? 0,
            render: (m) => (m.heroPower ? big(m.heroPower) : m.heroEst ? <span title="estimated from the profile: 0.78 × (power − troops)">≈{big(m.heroEst)}</span> : "–") },
          { key: "dronePower", label: "drone", num: true, render: (m) => (m.dronePower ? <span title={`drone level ${m.droneLevel}`}>{big(m.dronePower)}</span> : "–") },
          { key: "kills", label: "kills", num: true, render: (m) => big(m.kills) },
          { key: "hq", label: "HQ", num: true },
          { key: "lastSeen", label: "last in game", sort: (m) => later(m.lastSeen, m.donatedAt) || "", render: (m) => <Seen iso={later(m.lastSeen, m.donatedAt)} /> },
          { key: "donatedAt", label: "donated", sort: (m) => m.donatedAt || "", render: (m) => <Seen iso={m.donatedAt} /> },
          { key: "donateToday", label: "today", num: true, render: (m) => int(m.donateToday) },
          { key: "donateWeek", label: "week", num: true, render: (m) => big(m.donateWeek) },
          { key: "joinedAt", label: "joined", sort: (m) => m.joinedAt || "", render: (m) => (m.joinedAt ? ago(m.joinedAt) : "–") },
        ]} />
      </div>

      {d.left.length > 0 && (
        <div className="card">
          <h2>Left since {when(d.prevRosterAt)}</h2>
          <SortTable initial={{ key: "power", dir: -1 }} rows={d.left} columns={[
            { key: "name", label: "player", render: (m) => <PlayerLink uid={m.uid} name={m.name} /> },
            { key: "power", label: "power", num: true, render: (m) => big(m.power) },
            { key: "rank", label: "was", render: (m) => `R${m.rank}` },
            { key: "nowTag", label: "now in", render: (m) => <Tag id={m.nowAllianceId} tag={m.nowTag} /> },
          ]} />
        </div>
      )}
    </>
  );
}

// part: "stats" (the headline row), "land" (cities and strongholds), "presidents" - the server page puts them on different tabs.
export function ServerIntelCards({ server, part = "stats" }) {
  const d = useJson(`/api/x/server/${server}/intel`);
  if (!d) return null;
  const st = d.state;
  const s = d.seen;
  if (part === "stats") return (
      <div className="stats">
        {s && <Stat label="In the game today" value={pct(s.seen1d, s.players)} sub={`${int(s.seen1d)} of ${int(s.players)} players (last seen)`} />}
        {st && <Stat label="Server score" value={int(st.score)} sub={`state ${st.state}`} />}
        {st && <Stat label="President" value={st.presidentName || "–"} sub={<>{st.presidentTag && <>[{st.presidentTag}] </>}first lady {st.firstLadyName || "–"}</>} />}
        {st && <Stat label="Transfers in" value={int(st.inHigh + st.inMid + st.inLow + st.inSuperLow)}
          sub={`${st.inHigh} high · ${st.inMid} mid · ${st.inLow} low · ${st.inSuperLow} very low`} />}
        {d.card?.createdAt && <Stat label="Server badge earned" value={when(d.card.createdAt).split(" ").slice(0, 2).join(" ") + " " + new Date(d.card.createdAt).getFullYear()}
          sub={`${Math.round((Date.now() - new Date(d.card.createdAt)) / 864e5)} days ago (from season results)`} />}
      </div>
  );
  if (part === "presidents") return (
        <div className="card">
          <h2>Presidents</h2>
          <SortTable initial={{ key: "round", dir: -1 }} rows={d.presidents} limit={40} columns={[
            { key: "round", label: "term", num: true },
            { key: "name", label: "president", render: (p) => <PlayerLink uid={p.uid} name={p.name} /> },
            { key: "tag", label: "alliance", render: (p) => <span className="tag">{p.tag}</span> },
            { key: "becameAt", label: "from", sort: (p) => p.becameAt || "", render: (p) => when(p.becameAt) },
            { key: "occupyServer", label: "won from", render: (p) => (p.occupyServer && p.occupyServer !== server ? <ServerLink id={p.occupyServer} /> : "") },
          ]} />
        </div>
  );
  return (
    <>
      {d.cityTotals && <p className="small muted">{int(d.cityTotals.held)} of {int(d.cityTotals.total)} cities held, as of {when(d.citiesAt)}.</p>}
      <div className="two">
        <div className="card">
          <h2>Who holds the cities</h2>
          <BarList format={int} rows={d.holders.map((h) => ({ key: h.allianceId, label: h.tag || h.name, value: h.cities, href: `#/x/alliance/${encodeURIComponent(h.allianceId)}`, detail: h.since ? `since ${when(h.since)}` : "" }))} />
        </div>
        {d.strongholds.length > 0 && (
          <div className="card">
            <h2>Who holds the strongholds</h2>
            <BarList format={int} rows={d.strongholds.map((h) => ({ key: h.allianceId, label: h.tag, value: h.held, href: `#/x/alliance/${encodeURIComponent(h.allianceId)}` }))} />
          </div>
        )}
      </div>
      {d.cityChanges.length > 0 && (
        <div className="card">
          <h2>Cities changing hands</h2>
          <SortTable initial={{ key: "before", dir: -1 }} rows={d.cityChanges} columns={[
            { key: "cityId", label: "city", num: true },
            { key: "fromTag", label: "from", render: (x) => <Tag id={x.fromId} tag={x.fromTag || "(empty)"} /> },
            { key: "toTag", label: "to", render: (x) => <Tag id={x.toId} tag={x.toTag || "(empty)"} /> },
            { key: "before", label: "seen", render: (x) => <>{when(x.after)} – {when(x.before)}</> },
            { key: "occupiedAt", label: "taken at", render: (x) => when(x.occupiedAt) },
          ]} />
        </div>
      )}
    </>
  );
}

// 24 columns, the quietest 3-hour stretch marked: when fewest of them are around.
function HourBars({ hours }) {
  const max = Math.max(...hours, 1);
  let quiet = 0, best = Infinity;
  for (let h = 0; h < 24; h++) { const s = hours[h] + hours[(h + 1) % 24] + hours[(h + 2) % 24]; if (s < best) { best = s; quiet = h; } }
  const isQuiet = (h) => (h - quiet + 24) % 24 < 3;
  return (
    <>
      <div className="hour-row">
        {hours.map((n, h) => (
          <div key={h} className="hour-col" title={`${String(h).padStart(2, "0")}:00 UTC - ${int(n)}`}>
            <div className={isQuiet(h) ? "hour-bar quiet" : "hour-bar"} style={{ height: `${(n / max) * 70}px` }} />
            <div className="small muted">{h % 3 === 0 ? String(h).padStart(2, "0") : ""}</div>
          </div>
        ))}
      </div>
      <p className="small muted">Quietest: {String(quiet).padStart(2, "0")}:00–{String((quiet + 3) % 24).padStart(2, "0")}:00 UTC.</p>
    </>
  );
}

// The hero-power estimate's own back-test against every server's top-200 hero board.
export function HeroModelCard() {
  const rows = useJson("/api/x/hero-model");
  if (!rows?.length) return null;
  const all = rows.find((r) => r.server === 0);
  const pc = (v) => `${(v * 100).toFixed(1)}%`;
  return (
    <div className="card">
      <h2>Hero power for everyone <span className="muted small">estimated, back-tested</span></h2>
      <p className="small">Hero power ≈ <b>{all.k.toFixed(3)} × (power − troop power)</b>, both from each player's profile. Checked against
        {" "}{int(all.players)} players on the {rows.length - 1} servers' top-200 hero boards: half are within <b>{pc(all.medianErr)}</b>,
        {" "}90% within {pc(all.p90Err)}, the worst {pc(all.worstErr)}; ranks 181–200 (closest to those off the board) {pc(all.bottom20Err)}.
        {" "}So players below the board get a real number, not a guess.</p>
      <SortTable initial={{ key: "server", dir: 1 }} rows={rows.filter((r) => r.server)} limit={40} columns={[
        { key: "server", label: "server", num: true, render: (r) => <ServerLink id={r.server} /> },
        { key: "k", label: "k on its own board", num: true, render: (r) => r.k.toFixed(3) },
        { key: "players", label: "checked", num: true },
        { key: "medianErr", label: "median error", num: true, render: (r) => pc(r.medianErr) },
        { key: "bottom20Err", label: "ranks 181–200", num: true, render: (r) => pc(r.bottom20Err) },
        { key: "worstErr", label: "worst", num: true, render: (r) => pc(r.worstErr) },
      ]} />
    </div>
  );
}

const chance = (p) => `${Math.round(p * 100)}%`;
const hh = (h) => `${String(h).padStart(2, "0")}:00`;

// Alliance war over one building the whole defending side garrisons (Siege.cs on the server).
const BUILDINGS = [["100", "Alliance Center / main building (100 squads)"], ["20", "secondary building (20 squads)"]];
function SiegeCard({ d, name, slots, setSlots }) {
  const s = d.siege;
  if (!s) return null;
  const [A, B] = [name(d.a), name(d.b)];
  const hrs = s.byHour || [];
  const best = (k) => hrs.reduce((b, h) => (h[k].breaks > b[k].breaks ? h : b), hrs[0]);
  const ba = best("aOnB"), bb = best("bOnA");
  const line = (r) => `~${Math.round(r.attackers)} attackers vs ${Math.round(r.garrison)} squads garrisoned · ${r.left} squads left on average`;
  return (
    <>
      <div className="row" style={{ justifyContent: "space-between", flexWrap: "wrap", gap: 8, marginTop: 14 }}>
        <h3 style={{ margin: 0 }}>Alliance war: breaking the other side's building</h3>
        <label className="small muted">building{" "}
          <select value={slots} onChange={(e) => setSlots(e.target.value)}>
            {BUILDINGS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
        </label>
      </div>
      <div className="stats">
        <Stat label={`${A} breaks ${B}'s building`} value={chance(s.aOnB.breaks)} sub={`scheduled war (everyone who plays) · ${line(s.aOnB)}`} />
        <Stat label={`${B} breaks ${A}'s building`} value={chance(s.bOnA.breaks)} sub={`scheduled war (everyone who plays) · ${line(s.bOnA)}`} />
        {hrs.length > 0 && <Stat label={`${A}'s best hour to attack`} value={hh(ba.hour)} sub={`${chance(ba.aOnB.breaks)} · ${line(ba.aOnB)}`} />}
        {hrs.length > 0 && <Stat label={`${B}'s best hour to attack`} value={hh(bb.hour)} sub={`${chance(bb.bOnA.breaks)} · ${line(bb.bOnA)}`} />}
      </div>
      {hrs.length > 0 && (
        <>
          <div className="small war-legend">
            <span><i className="swatch" style={{ background: "var(--series-1)" }} />{A} attacking</span>
            <span><i className="swatch" style={{ background: "var(--series-2)" }} />{B} attacking</span>
            <span className="muted">bars = chance the building breaks if the attack lands at that hour (events you can time, e.g. trade posts / markets)</span>
          </div>
          <div className="war-hours">
            {hrs.map((h) => (
              <div key={h.hour} className={`wh ${h.hour === ba.hour || h.hour === bb.hour ? "best" : ""}`}
                title={`${hh(h.hour)} UTC\n${A} attacks: ${chance(h.aOnB.breaks)} · ${line(h.aOnB)}\n${B} attacks: ${chance(h.bOnA.breaks)} · ${line(h.bOnA)}`}>
                <div className="wh-bars">
                  <i style={{ height: `${h.aOnB.breaks * 100}%`, background: "var(--series-1)" }} />
                  <i style={{ height: `${h.bOnA.breaks * 100}%`, background: "var(--series-2)" }} />
                </div>
                <div className="wh-h">{String(h.hour).padStart(2, "0")}</div>
              </div>
            ))}
          </div>
        </>
      )}
      <p className="muted small">
        Simulated as the season guides describe it: every defender has one squad in the building (strongest first when it is full);
        attackers land in timed waves - everyone's strongest squad, then 2nd, then 3rd - and each march stops after beating 5 squads;
        between waves defenders who are on re-garrison with their next squad. Squads fight by Lanchester's square law (a stronger squad
        survives with √(a² − d²) and fights on), with random swings for the counters, morale and gear we cannot see - one per duel and
        one for the whole side. Scheduled war: everyone expected by how recently they played. By hour: the garrison was set beforehand,
        but attacking and re-garrisoning need them on at that hour, from their own daily pattern. Squad = about 34% / 26% / 20% of a
        player's hero power (a guess, the same for both sides).
      </p>
    </>
  );
}

// The war game: who is stronger at each hour of the day (UTC), from when each member is usually on.
function WarHours({ d, name }) {
  const hrs = d.byHour || [];
  if (!hrs.length || !d.observedDays) return <p className="muted small">No timed activity yet for a by-hour war game.</p>;
  const max = Math.max(1, ...hrs.flatMap((h) => [h.aHero, h.bHero]));
  const best = (k) => hrs.reduce((b, h) => (h[k] > b[k] ? h : b), hrs[0]);
  const ba = best("aAttacks"), bb = best("bAttacks");
  const [A, B] = [name(d.a), name(d.b)];
  // Defending = the other side's attack failing, so each side's weakest hour is the other's best.
  const side = (me, them, atk, other) => ({
    me, them, atk: best(atk), weak: best(other),
    safe: hrs.reduce((b, h) => (h[other] < b[other] ? h : b), hrs[0]),
  });
  const sides = [side(A, B, "aAttacks", "bAttacks"), side(B, A, "bAttacks", "aAttacks")];
  return (
    <>
      <h3 style={{ marginTop: 14 }}>Base vs base by hour of day (UTC)</h3>
      <div className="stats">
        {sides.map((s, i) => {
          const atk = i === 0 ? "aAttacks" : "bAttacks", other = i === 0 ? "bAttacks" : "aAttacks";
          return [
            <Stat key={`${i}a`} label={`${s.me} attacking: best hour`} value={hh(s.atk.hour)} sub={`${chance(s.atk[atk])} the attack succeeds`} />,
            <Stat key={`${i}d`} label={`${s.me} defending: weakest hour`} value={hh(s.weak.hour)}
              sub={`holds ${chance(1 - s.weak[other])} · safest ${hh(s.safe.hour)} (holds ${chance(1 - s.safe[other])})`} />,
          ];
        })}
        <Stat label="Hours ahead in an open fight" value={`${hrs.filter((h) => h.open > 0.5).length} – ${hrs.filter((h) => h.open < 0.5).length}`} sub={`${A} – ${B}`} />
      </div>
      <div className="small war-legend">
        <span><i className="swatch" style={{ background: "var(--series-1)" }} />{A}</span>
        <span><i className="swatch" style={{ background: "var(--series-2)" }} />{B}</span>
        <span className="muted">number = open-fight winner's chance, in their colour · dashed = best attack hours</span>
      </div>
      <div className="war-hours">
        {hrs.map((h) => (
          <div key={h.hour} className={`wh ${h.hour === ba.hour || h.hour === bb.hour ? "best" : ""}`}
            title={`${hh(h.hour)} UTC\n${A}: ~${h.aOn} on, ${big(h.aHero)} hero power · attack ${chance(h.aAttacks)}\n${B}: ~${h.bOn} on, ${big(h.bHero)} hero power · attack ${chance(h.bAttacks)}\nopen fight: ${chance(h.open)} ${A}`}>
            <div className="wh-bars">
              <i style={{ height: `${(h.aHero / max) * 100}%`, background: "var(--series-1)" }} />
              <i style={{ height: `${(h.bHero / max) * 100}%`, background: "var(--series-2)" }} />
            </div>
            <div className="wh-chance" style={{ color: h.open >= 0.5 ? "var(--series-1)" : "var(--series-2)" }}>{Math.round(Math.max(h.open, 1 - h.open) * 100)}</div>
            <div className="wh-h">{String(h.hour).padStart(2, "0")}</div>
          </div>
        ))}
      </div>
      <div style={{ overflowX: "auto" }}>
        <table className="matchup-table war-table small">
          <thead>
            <tr><th rowSpan={2}>UTC</th><th colSpan={4}>{A}</th><th colSpan={4}>{B}</th><th rowSpan={2}>open fight</th></tr>
            <tr>
              <th title="expected hero power of the 10 strongest on">rally</th><th title="expected hero power of the 20 strongest on">garrison</th><th>attacks</th><th>defends</th>
              <th title="expected hero power of the 10 strongest on">rally</th><th title="expected hero power of the 20 strongest on">garrison</th><th>attacks</th><th>defends</th>
            </tr>
          </thead>
          <tbody>
            {hrs.map((h) => {
              const aDef = 1 - h.bAttacks, bDef = 1 - h.aAttacks;
              const c = (p) => (p >= 0.5 ? "win" : "");
              return (
                <tr key={h.hour}>
                  <td>{hh(h.hour)}</td>
                  <td>{big(h.aRally)}</td><td>{big(h.aGarrison)}</td><td className={c(h.aAttacks)}>{chance(h.aAttacks)}</td><td className={c(aDef)}>{chance(aDef)}</td>
                  <td>{big(h.bRally)}</td><td>{big(h.bGarrison)}</td><td className={c(h.bAttacks)}>{chance(h.bAttacks)}</td><td className={c(bDef)}>{chance(bDef)}</td>
                  <td>{chance(Math.max(h.open, 1 - h.open))} {h.open >= 0.5 ? A : B}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <p className="muted small">
        Attacks = the chance a rally of that side's 10 strongest on at that hour beats the other side's 20 strongest on (half weight,
        +10% home advantage). Defends = the chance the other side's attack fails at that hour. Rally and garrison are expected hero
        power: each member's hero power × how often they are on then.
      </p>
      <p className="muted small">
        Bars: estimated hero power in the game at that hour - each member's hero power × the share of the last {d.observedDays} observed
        days they showed up then (logged in, donated or chatted; the hour either side counts half). Attacks and open fights use the same
        rules as above with that weighting instead of "last seen". Thin until the hourly pulse has run for a week or two.
      </p>
    </>
  );
}
// How decisive is a stronger squad? 1 = strength adds up plainly; 2 = Lanchester's square law;
// 4 = a clearly stronger squad nearly always wins. The one knob to tune against real fights.
const STEEP = [["1", "additive"], ["2", "Lanchester (square)"], ["3", "steep"], ["4", "decisive"]];
export function Matchup({ a, b }) {
  const [steep, setSteep] = useHashParam("steep", "2");
  const [slots, setSlots] = useHashParam("slots", "100");
  const d = useJson(`/api/x/matchup?a=${encodeURIComponent(a)}&b=${encodeURIComponent(b)}&steep=${steep}&slots=${slots}`);
  if (!d) return null;
  if (!d.a.rosterAt || !d.b.rosterAt) return <div className="card"><h2>Matchup</h2><p className="muted small">Both alliances need a roster from Alliance &amp; server intel.</p></div>;
  const name = (s) => s.info?.tag || s.allianceId.slice(0, 6);
  const rows = [
    ["Members", "members", int], ["In the game today", "seen1d", int], ["Active (3 days)", "active3d", int],
    ["Fighting strength (hero × activity)", "combat", big], ["Strongest 10 (rally)", "top10", big], ["Strongest 20 (garrison)", "top20", big],
    ["Hero power (every member)", "heroTotal", big], ["Members on a hero board", "onHeroBoard", int],
    ["Total power", "power", big], ["Average drone power (strongest players)", "droneAvg", big], ["Players with drone read", "withDrone", int], ["Kills", "kills", big], ["HQ 30", "hq30", int], ["Donated today", "donatedToday", int],
    ["Cities held", "cities", int],
  ];
  return (
    <div className="card matchup">
      <div className="row" style={{ justifyContent: "space-between", flexWrap: "wrap" }}>
        <h2>War game: <Tag id={d.a.allianceId} tag={name(d.a)} /> vs <Tag id={d.b.allianceId} tag={name(d.b)} /></h2>
        <label className="small muted">how decisive is a stronger squad?{" "}
          <select value={steep} onChange={(e) => setSteep(e.target.value)}>
            {STEEP.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
        </label>
      </div>
      {/* The alliance war first - it is what season fights are; base vs base follows. */}
      <SiegeCard d={d} name={name} slots={slots} setSlots={setSlots} />
      <h3 style={{ marginTop: 18 }}>Base vs base</h3>
      <div className="stats">
        <Stat label={`${name(d.a)} attacks ${name(d.b)}`} value={chance(d.aAttacks)} sub="chance the attack succeeds" />
        <Stat label={`${name(d.b)} attacks ${name(d.a)}`} value={chance(d.bAttacks)} sub="chance the attack succeeds" />
        <Stat label="Open fight" value={`${chance(d.open)} ${name(d.a)}`}
          sub={d.strengthRatio ? `${name(d.a)} has ${d.strengthRatio}× ${name(d.b)}'s fighting strength` : "everyone, no home advantage"} />
      </div>
      <table className="matchup-table">
        <thead><tr><th></th><th>{name(d.a)}</th><th>{name(d.b)}</th></tr></thead>
        <tbody>
          {rows.map(([label, k, f]) => {
            const x = Number(d.a[k] || 0), y = Number(d.b[k] || 0);
            return <tr key={k}><td>{label}</td><td className={x > y ? "win" : ""}>{f(x)}</td><td className={y > x ? "win" : ""}>{f(y)}</td></tr>;
          })}
        </tbody>
      </table>
      <div className="two" style={{ marginTop: 8 }}>
        {[d.a, d.b].map((s) => (
          <div key={s.allianceId}>
            <h3 className="small muted">{name(s)}: strongest 10 as counted</h3>
            {s.strongest.map((m) => (
              <div key={m.uid} className="small row" style={{ justifyContent: "space-between" }}>
                <PlayerLink uid={m.uid} name={m.name} />
                <span>{big(m.hero)}{m.estimated ? <span className="muted" title="not on the hero board - estimated"> est.</span> : ""}{m.weight < 1 ? <span className="muted"> ×{m.weight}</span> : ""}</span>
              </div>
            ))}
          </div>
        ))}
      </div>
      <WarHours d={d} name={name} />
      <p className="muted small">{d.model} Still a heuristic: troop tiers, gear, buffs and tactics are not visible to us.</p>
    </div>
  );
}
