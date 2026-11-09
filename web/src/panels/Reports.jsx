import { useEffect, useState } from "react";
import { big, int, when } from "../explorer/format.js";
import { PlayerLink, ServerLink } from "../explorer/bits.jsx";
import { SortTable } from "../explorer/charts.jsx";
import { useJson } from "../explorer/PlayerTabs.jsx";
import { sectionArgs } from "../nav.js";

// Admin: battle reports shared with our accounts (Inbox collects them), one report in full, and what
// separates winners from losers across all of them. The comparison is re-worked from the stored
// reports on every visit, so it sharpens as more reports come in.

export const FACTOR = {
  heroPower: "Hero power", armyStartPower: "Squad power at start", troopPower: "Troop power", troops: "Troops",
  formationEquipPower: "Formation gear power", sciencePower: "Research power", decoPower: "Decoration power", honorPower: "Honor power",
  chipLevel: "Drone chip levels", weaponStrengthLevel: "Weapon strengthening", droneLevel: "Drone level",
  heroLevelAvg: "Hero level (avg)", heroRankAvg: "Hero rank (avg)", heroEquipLevels: "Hero gear levels", heroEquipPromote: "Hero gear promotion",
  heroSkillLevels: "Hero skill levels", heroWeaponLevels: "Exclusive weapon levels", heroAwaken: "Hero awakening", injuredRate: "Injured rate",
  formationBonus: "Line-up bonus (HP/Atk/Def)", sameTypeHeroes: "Heroes of the main type", typeAdvantage: "Type advantage (+1 = counters them)",
  heroesCounteringThem: "Heroes that counter their type", frontDamageDealers: "Damage dealers in front row", frontGuardians: "Guardians in front row",
  frontHpShare: "Front row share of HP", frontShieldedByChip: "Front heroes the shield chip covers", droneChipStars: "Drone chip stars",
  droneChipQuality: "Drone chip quality", droneChipsMatchingType: "Drone chips for the squad type", mainType: "Main troop type",
};
import { TYPE, JOB, Deep, HeroIcon } from "./ReportDetail.jsx";
export { TYPE, JOB };
const ROLE = { dd: "damage dealers", guard: "guardians", front: "front row", back: "back row" };
// Item-level factors from BattleReports.Fights: gear{Slot}Quality, gear{Slot}Lv.{role}, chip{Kind}Lv/Star/Quality;
// eff{id} / bonus{id} / science{tab} / powerTab / extraPower come named from the server (names).
const deepName = (k) => {
  let m = k.match(/^gear(\w+?)Quality$/);
  if (m) return `${m[1]} gear quality (avg)`;
  m = k.match(/^gear(\w+?)Lv\.(\w+)$/);
  if (m) return `${m[1]} gear level on ${ROLE[m[2]] || m[2]}`;
  m = k.match(/^chip(\w+?)(Lv|Star|Quality)$/);
  if (m) return `${m[1]} drone chip ${m[2] === "Lv" ? "level" : m[2].toLowerCase()}`;
  return null;
};
export const isDetail = (k) => /^(eff|bonus)\d+$/.test(k);
const pctOr = (k, v) => (k === "formationBonus" || k === "frontHpShare" ? `${Math.round(v * 100)}%` : k === "mainType" ? TYPE[v] || v : k === "typeAdvantage" ? (v > 0 ? "+1" : `${v}`) : null);
export const factorName = (k, names) => FACTOR[k] || names?.[k] || deepName(k) || k.replace(/^science(\d+)$/, "Research tab $1 %").replace(/^powerTab(\d+)$/, "Power tab $1").replace(/^extraPower(\d+)$/, "Extra power $1");
const num = (v) => (v == null ? "–" : Math.abs(v) >= 10000 ? big(v) : Number.isInteger(v) ? int(v) : v.toFixed(2));

export default function Reports() {
  const [args, setArgs] = useState(sectionArgs);
  useEffect(() => { const on = () => setArgs(sectionArgs()); window.addEventListener("hashchange", on); return () => window.removeEventListener("hashchange", on); }, []);
  const page = args[0] || "list";
  return (
    <div className="grid">
      <nav className="subnav big">
        <a href="#/reports" className={page === "list" || page.length > 20 ? "on" : ""}>Reports</a>
        <a href="#/reports/wins" className={page === "wins" ? "on" : ""}>What wins</a>
      </nav>
      {page === "wins" ? <Wins /> : page === "list" ? <List /> : <Report uid={page} />}
    </div>
  );
}

function List() {
  const d = useJson("/api/reports");
  if (!d) return <p className="muted">Loading…</p>;
  const side = (r, s) => r.sides.find((x) => x.fight === 0 && x.side === s);
  return (
    <div className="card">
      <p className="small muted">Reports people shared with our accounts by DM. Ask players to share theirs - wins and losses both count. New shares come in when you press "Collect battle reports" on the Inbox tab - there is no automatic check.</p>
      <SortTable rows={d} initial={{ key: "battleTime", dir: -1 }} empty="No reports yet - open an account in the Inbox and collect." columns={[
        { key: "battleTime", label: "battle", sort: (r) => r.battleTime || r.sharedAt, render: (r) => <a href={`#/reports/${r.reportUid}`}>{when(r.battleTime || r.sharedAt)}</a> },
        { key: "mailTypeName", label: "kind" },
        { key: "attacker", label: "attacker", render: (r) => <Who s={side(r, 0)} /> },
        { key: "defender", label: "defender", render: (r) => <Who s={side(r, 1)} /> },
        { key: "fights", label: "fights", num: true, sort: (r) => r.sides.length / 2, render: (r) => (r.sides.length ? r.sides.length / 2 : "–") },
        { key: "sharedBy", label: "shared by" },
        { key: "fetched", label: "", render: (r) => (r.fetched ? "" : r.error ? <span className="err small" title={r.error}>failed</span> : <span className="muted small">waiting</span>) },
      ]} />
    </div>
  );
}

const Who = ({ s }) => (s ? <span>{s.won ? "🏆 " : ""}{s.tag ? `[${s.tag}] ` : ""}<PlayerLink uid={s.uid} name={s.name} /> <span className="muted small">#{s.server} · {big(s.heroPower)}</span></span> : <span className="muted">–</span>);

function Report({ uid }) {
  const d = useJson(`/api/reports/${uid}?json=true`);
  if (!d) return <p className="muted">Loading…</p>;
  if (d.error && !d.fights?.length) return <p className="err">Could not fetch this report: {d.error}</p>;
  const fights = [...new Set(d.fights.map((f) => f.fight))];
  // The decoded report: a combined one holds its fights in round[].battle[] (same order as the stored fights).
  let raw = null;
  try { raw = d.json ? JSON.parse(d.json) : null; } catch { raw = null; }
  const battles = raw ? (raw.round?.length ? raw.round.flatMap((r) => r.battle || []) : [raw]) : [];
  const cat = {};
  d.fights.forEach((f) => f.heroes.forEach((h) => { cat[h.heroId] = { type: h.type, job: h.job }; }));
  return (
    <>
      <p className="small"><a href="#/reports">← All reports</a></p>
      <div className="page-head"><div>
        <h1 className="page-title">{d.mailTypeName}</h1>
        <p className="muted">{when(d.battleTime)} · {fights.length} fight{fights.length === 1 ? "" : "s"} · shared by {d.shares.map((s) => s.sharedByName).join(", ")}</p>
      </div></div>
      {fights.map((n) => <Fight key={n} n={n} sides={d.fights.filter((f) => f.fight === n)} names={d.heroNames} chips={d.chipNames || {}} many={fights.length > 1} b={battles[n]} N={d.names} cat={cat} />)}
    </>
  );
}

function Fight({ n, sides, names, many, b: battle, N, cat }) {
  const [a, b] = [sides.find((s) => s.side === 0), sides.find((s) => s.side === 1)];
  const [more, setMore] = useState(false);
  if (!a || !b) return null;
  const order = Object.keys(FACTOR);
  const keys = [...new Set([...Object.keys(a.factors), ...Object.keys(b.factors)])].filter((k) => !isDetail(k) && (a.factors[k] || b.factors[k]))
    .sort((x, y) => (FACTOR[x] ? 0 : 1) - (FACTOR[y] ? 0 : 1) || order.indexOf(x) - order.indexOf(y) || x.localeCompare(y));
  const shown = more ? keys : keys.filter((k) => FACTOR[k]);
  const cell = (s, o, k) => {
    const v = s.factors[k], w = o.factors[k];
    return <td className="num" style={{ fontWeight: k !== "mainType" && v > (w ?? 0) ? 700 : 400 }}>{v == null ? "–" : pctOr(k, v) ?? num(v)}</td>;
  };
  const line = (s) => (
    <span style={{ display: "inline-flex", gap: 3, verticalAlign: "middle" }}>
      {s.heroes.filter((h) => h.unit !== 2).map((h, i) => <HeroIcon key={i} id={h.heroId} type={h.type} name={names[h.heroId]} size={22} />)}
    </span>
  );
  return (
    <div className="card">
      {many && <h3>Fight {n + 1}</h3>}
      <div className="scroll">
        <table className="small">
          <thead><tr><th></th>
            <th className="num">{a.won ? "🏆 " : ""}Attacker: {a.tag ? `[${a.tag}] ` : ""}{a.name}<div>{line(a)}</div></th>
            <th className="num">{b.won ? "🏆 " : ""}Defender: {b.tag ? `[${b.tag}] ` : ""}{b.name}<div>{line(b)}</div></th>
            <th className="num">ratio</th></tr></thead>
          <tbody>
            <tr><td className="muted">server</td><td className="num"><ServerLink id={a.server} /></td><td className="num"><ServerLink id={b.server} /></td><td /></tr>
            {shown.map((k) => (
              <tr key={k}><td>{factorName(k, N?.factorNames)}</td>{cell(a, b, k)}{cell(b, a, k)}
                <td className="num muted">{a.factors[k] && b.factors[k] && pctOr(k, 1) == null ? (a.factors[k] / b.factors[k]).toFixed(2) : ""}</td></tr>
            ))}
            <tr><td colSpan={4}><a style={{ cursor: "pointer" }} onClick={() => setMore(!more)}>{more ? "▾ fewer measures" : `▸ ${keys.length - shown.length} more measures (gear by slot and wearer, chips, research tabs, power breakdown)`}</a></td></tr>
            {Object.keys(a.outcome).map((k) => (
              <tr key={k} className="muted"><td>{k === "damage" ? "Damage dealt" : k === "damageTaken" ? "Damage taken" : k === "troopsLost" ? "Troops lost" : "Troops left"}</td>
                <td className="num">{k === "troopsLeftShare" ? `${Math.round(a.outcome[k] * 100)}%` : num(a.outcome[k])}</td>
                <td className="num">{k === "troopsLeftShare" ? `${Math.round(b.outcome[k] * 100)}%` : num(b.outcome[k])}</td><td /></tr>
            ))}
          </tbody>
        </table>
      </div>
      {battle ? <Deep b={battle} N={N} cat={cat} /> : <p className="small muted">The decoded report is not stored, so only the summary shows.</p>}
    </div>
  );
}

const GROUPS = { core: "Main measures", gear: "Gear, chips, research, power", stat: "Hero stats", bonus: "Bonuses" };
const groupOf = (k) => (/^eff\d/.test(k) ? "stat" : /^bonus\d/.test(k) ? "bonus" : FACTOR[k] ? "core" : "gear");

function Wins() {
  const d = useJson("/api/reports/analysis");
  const [group, setGroup] = useState("core");
  if (!d) return <p className="muted">Loading…</p>;
  const name = (k) => factorName(k, d.names);
  const rows = d.factors.filter((f) => groupOf(f.factor) === group);
  return (
    <>
      <Rules />
      <Splits splits={d.splits || []} />
      <div className="card">
        <h3>What the winner had more of</h3>
        <p className="small muted">
          Across {d.fights} fight{d.fights === 1 ? "" : "s"} with both sides known: for each measure, how often the winner had MORE of it than the loser (of the fights
          where the two differed), and the typical winner ÷ loser ratio. A measure that decides fights sits far from 50%; sorted by how strong and how well-supported that is.
          Rounds of one report are counted as separate fights. {d.fights < 30 && <b>With {d.fights} fights this is anecdote, not evidence - it firms up as more reports are shared.</b>}
        </p>
        <nav className="subnav">{Object.entries(GROUPS).map(([g, label]) => (
          <a key={g} className={g === group ? "on" : ""} style={{ cursor: "pointer" }} onClick={() => setGroup(g)}>{label} <span className="muted small">{d.factors.filter((f) => groupOf(f.factor) === g).length}</span></a>
        ))}</nav>
        <SortTable rows={rows} initial={{ key: "strength", dir: -1 }} empty="No fights yet." columns={[
          { key: "factor", label: "measure", render: (f) => name(f.factor) },
          { key: "fights", label: "fights", num: true },
          { key: "winnerHigherShare", label: "winner had more", num: true, render: (f) => <Bar v={f.winnerHigherShare} n={f.fights} /> },
          { key: "strength", label: "signal", num: true, sort: (f) => Math.abs(f.winnerHigherShare - 0.5) * Math.sqrt(f.winnerHigher + f.winnerLower),
            render: (f) => (f.winnerHigher + f.winnerLower ? (Math.abs(f.winnerHigherShare - 0.5) * 2 * Math.sqrt(f.winnerHigher + f.winnerLower)).toFixed(1) : <span className="muted">never differed</span>) },
          { key: "medianRatio", label: "winner ÷ loser", num: true, render: (f) => (f.medianRatio ? f.medianRatio.toFixed(2) : "–") },
          { key: "winnerHigher", label: "more / less / same", render: (f) => <span className="muted small">{f.winnerHigher} / {f.winnerLower} / {f.equal}</span> },
        ]} />
      </div>
      <div className="card">
        <h3>Upsets: the side with less hero power won</h3>
        <p className="small muted">These say most about everything else - what the underdog had more of.</p>
        <SortTable rows={d.upsets} initial={{ key: "heroRatio", dir: 1 }} empty="None yet." columns={[
          { key: "winner", label: "winner", render: (u) => <a href={`#/reports/${u.reportUid}`}>{u.winnerTag ? `[${u.winnerTag}] ` : ""}{u.winner}</a> },
          { key: "loser", label: "beat", render: (u) => `${u.loserTag ? `[${u.loserTag}] ` : ""}${u.loser}` },
          { key: "heroRatio", label: "hero power ratio", num: true, render: (u) => u.heroRatio.toFixed(2) },
          { key: "winnerAhead", label: "winner was ahead on", render: (u) => <span className="small">{u.winnerAhead.filter((k) => groupOf(k) !== "stat" && groupOf(k) !== "bonus").map(name).join(", ")}</span> },
        ]} />
      </div>
    </>
  );
}

const Bar = ({ v, n }) => (
  <span title={`${Math.round(v * 100)}% of ${n}`} style={{ display: "inline-flex", alignItems: "center", gap: 6 }}>
    <span style={{ width: 80, height: 8, background: "rgba(139,151,166,0.25)", borderRadius: 4, position: "relative", display: "inline-block" }}>
      <span style={{ position: "absolute", left: 0, top: 0, bottom: 0, width: `${v * 100}%`, background: v >= 0.5 ? "var(--accent)" : "#f0883e", borderRadius: 4 }} />
      <span style={{ position: "absolute", left: "50%", top: -2, bottom: -2, width: 1, background: "var(--muted, #8b97a6)" }} />
    </span>
    {Math.round(v * 100)}%
  </span>
);

// The game's own rules (its tables and texts) and what the guides say - the things to test against our reports.
function Rules() {
  return (
    <div className="card">
      <h3>The rules, as written</h3>
      <div className="two small">
        <div>
          <p><b>Line-up bonus</b> (game table camp_effect): HP, Attack and Defense +5% for 3 heroes of one troop type, +10% for 3 + 2, +15% for 4, +20% for 5. Unlocks after the first Capitol Conquest.</p>
          <p><b>Type counters</b> (game text): Tanks counter Missile Vehicles, Missile Vehicles counter Aircraft, Aircraft counter Tanks. The countered side deals 20% less damage; research can raise the counter side&apos;s damage and cut what it takes further.</p>
          <p><b>Rows:</b> 2 heroes in front (slots 1-2), 3 behind. Normal attacks hit the front row first; some skills aim at the back row. Some passives only work from the front (Lucius&apos; Silver Armor, Murphy&apos;s short range).</p>
        </div>
        <div>
          <p><b>Drone chips</b> (4 slots): the Initial Move chip shields <i>front-row heroes of its troop type</i> at the start (EM Shield / Absolute Quantum Field); Attack chips boost the back row or one troop type after the drone bombs; Interference and Defense chips add armour-piercing and defense. Type chips only help heroes of that type.</p>
          <p><b>The tactic to test:</b> put a strong damage dealer in the front row so the shield chip protects it (the guides say damage dealers belong at the back). Seen in the reports: Ghost fronts McGregor + Tesla with a Missile shield chip.</p>
          <p className="muted">Sources: the game&apos;s tables and texts (camp_effect, lw_hero, lw_drone_skillchip, skill texts); <a href="https://lastwarvault.com/guides/general/combat-guide" target="_blank" rel="noreferrer">Last War Vault combat guide</a>, <a href="https://lastwarvault.com/guides/general/pvp-squad-setup/" target="_blank" rel="noreferrer">PvP squad setup</a>.</p>
        </div>
      </div>
    </div>
  );
}

// Win rate at each value of the line-up measures: the direct test of the rules above.
function Splits({ splits }) {
  const show = (k, v) => pctOr(k, v) ?? v;
  return (
    <div className="card">
      <h3>Do the rules hold in our reports?</h3>
      <p className="small muted">Every side of every fight, grouped by the measure: how often that side won. Fifty-fifty means the measure did not matter (or there are too few fights to tell).</p>
      <div className="two">
        {splits.filter((s) => s.values.length).map((s) => (
          <table key={s.factor} className="small">
            <thead><tr><th>{factorName(s.factor)}</th><th className="num">sides</th><th className="num">won</th></tr></thead>
            <tbody>{s.values.map((v) => (
              <tr key={v.value}><td>{show(s.factor, v.value)}</td><td className="num">{v.sides}</td><td className="num"><Bar v={v.wins / v.sides} n={v.sides} /></td></tr>
            ))}</tbody>
          </table>
        ))}
      </div>
    </div>
  );
}
