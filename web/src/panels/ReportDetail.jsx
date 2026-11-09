import { useState } from "react";
import { big, int } from "../explorer/format.js";

// Everything one fight's report holds, down to the item: each hero's gear, skills and drone chips,
// the player's research / decorations / skins / other bonuses, and every stat the game resolved for
// every hero. `b` is the decoded battle (one round battle, or the report itself); `N` the names the
// server resolved from the game tables (names.hero[id], names.gear[id], names.effect[id]...).

export const TYPE = { 1: "Tank", 2: "Missile", 3: "Aircraft" };
export const JOB = { 1: "guardian", 2: "damage", 3: "support" };
const TYPE_COLOUR = { 1: "#d4a72c", 2: "#e5534b", 3: "#4c9aff" };
const SLOT = { 1: "Weapon", 2: "Armor", 3: "Chip", 4: "Radar" };
const QUALITY = { 1: "N", 2: "R", 3: "SR", 4: "SSR", 5: "UR", 6: "UR+" };
const QUALITY_COLOUR = { 3: "#4c9aff", 4: "#a371f7", 5: "#d4a72c", 6: "#e5534b" };
const gearSlot = (id) => Math.floor(id / 100) % 10;
const gearQuality = (id) => Math.floor(id / 100000) + 1;
const n = (N, kind, id) => N?.[kind]?.[id] ?? `${kind} ${id}`;
const isFront = (u) => [1, 2, 6, 7].includes(u.index);
const sideOf = (u) => (u.unitType === 2 ? (u.index % 2 === 1 ? 0 : 1) : u.index <= 5 ? 0 : 1);
export const effVal = (N, id, v) => (v == null ? "–" : N?.percent?.[id] ? `${+(v * 100).toFixed(2)}%` : Math.abs(v) >= 10000 ? big(v) : +Number(v).toFixed(2));

export function HeroIcon({ id, type, name, size = 40 }) {
  const [failed, setFailed] = useState(false);
  const style = { width: size, height: size, borderRadius: 8, flex: "none", border: `2px solid ${TYPE_COLOUR[type] || "var(--line)"}` };
  if (!failed) return <img src={`/hero-icons/${id}.png`} alt={name} title={name} style={{ ...style, objectFit: "cover", background: "#0002" }} onError={() => setFailed(true)} />;
  return (
    <span title={name} style={{ ...style, display: "inline-flex", alignItems: "center", justifyContent: "center", background: `${TYPE_COLOUR[type] || "#888"}33`, fontWeight: 700, fontSize: size * 0.32 }}>
      {(name || "?").replace(/[^A-Za-z0-9]/g, "").slice(0, 2)}
    </span>
  );
}

export function Deep({ b, N, cat }) {
  const units = [...(b.units || [])].sort((x, y) => x.index - y.index);
  const side = (s) => units.filter((u) => sideOf(u) === s);
  const players = b.player || [];
  return (
    <>
      <div className="two">
        {[0, 1].map((s) => (
          <div key={s}>
            <h3>{s === 0 ? "Attacker" : "Defender"}: line-up</h3>
            {side(s).map((u) => <UnitCard key={u.index} u={u} N={N} cat={cat} />)}
          </div>
        ))}
      </div>
      <div className="two">
        {[0, 1].map((s) => <PlayerBits key={s} p={players[s]} N={N} label={s === 0 ? "Attacker" : "Defender"} />)}
      </div>
      <Bonuses players={players} N={N} />
      <HeroStats units={units} N={N} />
    </>
  );
}

function UnitCard({ u, N, cat }) {
  const drone = u.unitType === 2;
  const c = cat?.[u.heroId] || {};
  const name = drone ? "Drone" : n(N, "hero", u.heroId);
  const st = u.stat || {};
  const skills = [...(u.skillInfos || [])].sort((x, y) => (x.slot || 0) - (y.slot || 0));
  const dmg = { ...(st.skillDamageMap || {}) };
  return (
    <div className="card" style={{ padding: 10, marginBottom: 8 }}>
      <div style={{ display: "flex", gap: 10, alignItems: "center" }}>
        {drone ? <span style={{ fontSize: 28 }}>🛸</span> : <HeroIcon id={u.heroId} type={c.type} name={name} />}
        <div style={{ flex: 1 }}>
          <div><b>{name}</b> {isFront(u) && !drone && <span className="pill" title="front row (slots 1-2)">front</span>}{" "}
            <span className="muted small">{drone ? "" : `${TYPE[c.type] || ""}${JOB[c.job] ? ` · ${JOB[c.job]}` : ""}`}</span></div>
          <div className="small muted">
            Lv {u.heroLevel}{u.rankLv ? ` · rank ${u.rankLv}` : ""}{u.awakenLv ? ` · awaken ${u.awakenLv}` : ""}{u.weaponLevel ? ` · exclusive weapon ${u.weaponLevel}` : ""}
            {u.maxSoldierCount ? ` · troops ${int(u.maxSoldierCount)} → ${int(u.soldierCount || 0)}` : ""}
          </div>
        </div>
        <div className="small" style={{ textAlign: "right" }}>
          <div>dealt <b>{big(st.damage || 0)}</b></div>
          <div className="muted">taken {big(st.injured || 0)}</div>
        </div>
      </div>
      {!drone && (u.equipInfos || []).length > 0 && (
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap", marginTop: 6 }}>
          {[...u.equipInfos].sort((x, y) => gearSlot(x.equipId) - gearSlot(y.equipId)).map((e) => {
            const q = gearQuality(e.equipId);
            return (
              <span key={e.equipId} className="pill" title={n(N, "gear", e.equipId)} style={{ borderColor: QUALITY_COLOUR[q], color: "var(--text)" }}>
                {SLOT[gearSlot(e.equipId)]} <span style={{ color: QUALITY_COLOUR[q] }}>{QUALITY[q]}</span> Lv {e.equipLv || 0}{e.promote ? ` ★${e.promote}` : ""}
              </span>
            );
          })}
        </div>
      )}
      {drone && (u.skillChipInfos || []).length > 0 && (
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap", marginTop: 6 }}>
          {u.skillChipInfos.map((ch) => <span key={ch.cfgId} className="pill" style={{ color: "var(--text)" }}>{n(N, "chip", ch.cfgId)} · Lv {ch.lv || 0} · {"★".repeat(ch.star || 0) || "0★"}</span>)}
        </div>
      )}
      {skills.length > 0 && (
        <table className="small" style={{ marginTop: 6 }}>
          <tbody>
            {skills.map((s) => (
              <tr key={s.skillId}>
                <td>{n(N, "skill", s.skillId)}</td><td className="num muted">Lv {s.skillLv || 0}</td>
                <td className="num">{dmg[s.skillId] ? `dealt ${big(dmg[s.skillId])}` : ""}</td>
                <td className="num muted">{st.skillInjuredMap?.[s.skillId] ? `taken ${big(st.skillInjuredMap[s.skillId])}` : ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {(st.physicalDamage || st.energyDamage || st.enhance || st.weaken) ? (
        <div className="small muted" style={{ marginTop: 4 }}>
          {st.physicalDamage ? `physical ${big(st.physicalDamage)} · ` : ""}{st.energyDamage ? `energy ${big(st.energyDamage)} · ` : ""}
          {st.enhance ? `buffs given ${big(st.enhance)} · ` : ""}{st.weaken ? `debuffs dealt ${big(st.weaken)} · ` : ""}{st.damageNum ? `${st.damageNum} hits` : ""}
        </div>
      ) : null}
    </div>
  );
}

function PlayerBits({ p, N, label }) {
  if (!p) return <div />;
  const pr = p.progress || {};
  const tabs = Object.entries(p.powerTabMap || {});
  return (
    <div className="card">
      <h3>{label}: player</h3>
      {(pr.equipId || []).length > 0 && <p className="small"><b>Squad gear:</b> {pr.equipId.map((id) => n(N, "squadGear", id)).join(", ")}</p>}
      {(pr.science || []).length > 0 && (
        <table className="small"><thead><tr><th>research</th><th className="num">done</th></tr></thead>
          <tbody>{[...pr.science].sort((x, y) => x.scienceTabId - y.scienceTabId).map((s) => (
            <tr key={s.scienceTabId}><td>{n(N, "scienceTab", s.scienceTabId)}</td><td className="num">{s.progress || 0}%</td></tr>
          ))}</tbody></table>
      )}
      <table className="small" style={{ marginTop: 6 }}>
        <tbody>
          {tabs.map(([k, v]) => <tr key={k}><td>{N?.powerTab?.[k] || `power tab ${k}`}</td><td className="num">{big(v)}</td></tr>)}
          {(p.extraPowers || []).map((e) => <tr key={e.viewType}><td>{N?.extraPower?.[e.viewType] || `extra power ${e.viewType}`}</td><td className="num">{big(e.value)}</td></tr>)}
          {pr.formationEquipPower ? <tr><td>Formation gear power</td><td className="num">{big(pr.formationEquipPower)}</td></tr> : null}
          {pr.sciencePower ? <tr><td>Research power</td><td className="num">{big(pr.sciencePower)}</td></tr> : null}
          {pr.decoPower ? <tr><td>Decoration power</td><td className="num">{big(pr.decoPower)}</td></tr> : null}
          {pr.honorPower ? <tr><td>Wall of Honor power</td><td className="num">{big(pr.honorPower)}</td></tr> : null}
          {pr.skinProgress?.power ? <tr><td>Skin power</td><td className="num">{big(pr.skinProgress.power)}</td></tr> : null}
        </tbody>
      </table>
      {(pr.deco || []).length > 0 && <p className="small muted">Decorations: {pr.deco.map((d) => `${QUALITY[d.decoQualityId] || d.decoQualityId} ×${d.totalCount} (lv ${d.totalLv})`).join(" · ")}</p>}
      {(pr.honorWall || []).length > 0 && <p className="small muted">Wall of Honor: {pr.honorWall.map((h) => `${h.count} heroes, lv ${h.totalLevel}`).join(" · ")}</p>}
    </div>
  );
}

// The player's bonuses, by where they come from, both sides side by side (what the game's
// "bonus breakdown" screen shows: lw_other_report_new).
function Bonuses({ players, N }) {
  const sources = (p) => {
    const pr = p?.progress || {};
    return [
      ["Research", pr.scienceEffects], ["Decorations", pr.decoEffects], ["Extra", pr.extraEffects],
      ...(pr.otherEffects || []).map((t) => [N?.otherTab?.[t.tabType] || `other ${t.tabType}`, t.effects]),
      ["Skins", pr.skinProgress?.skinEffects],
    ];
  };
  const rows = new Map();
  players.slice(0, 2).forEach((p, s) => sources(p).forEach(([src, list]) => (list || []).forEach((e) => {
    const k = `${src}|${e.id}`;
    const r = rows.get(k) || { src, id: e.id, v: [null, null] };
    r.v[s] = (r.v[s] || 0) + (e.val || 0);
    rows.set(k, r);
  })));
  if (!rows.size) return null;
  const list = [...rows.values()];
  const groups = [...new Set(list.map((r) => r.src))];
  return (
    <div className="card">
      <h3>Bonuses</h3>
      <p className="small muted">Every bonus each player brought, by source. Bold = the larger of the two.</p>
      <div className="scroll">
        <table className="small">
          <thead><tr><th>bonus</th><th className="num">attacker</th><th className="num">defender</th></tr></thead>
          <tbody>
            {groups.map((g) => [
              <tr key={g}><td colSpan={3}><b>{g}</b></td></tr>,
              ...list.filter((r) => r.src === g).map((r) => (
                <tr key={`${g}${r.id}`}>
                  <td title={`effect ${r.id}`}>{n(N, "effect", r.id)}</td>
                  {r.v.map((v, i) => <td key={i} className="num" style={{ fontWeight: v != null && v > (r.v[1 - i] ?? 0) ? 700 : 400 }}>{effVal(N, r.id, v)}</td>)}
                </tr>
              )),
            ])}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// Every stat the game resolved for every hero (unit.effects, ~150 each): attack, HP, crit, damage boosts...
function HeroStats({ units, N }) {
  const [open, setOpen] = useState(false);
  const [all, setAll] = useState(false);
  const heroes = units.filter((u) => u.unitType !== 2 && (u.effects || []).length);
  if (!heroes.length) return null;
  const val = heroes.map((u) => Object.fromEntries(u.effects.map((e) => [e.effectId, e.value])));
  let ids = [...new Set(heroes.flatMap((u) => u.effects.map((e) => e.effectId)))].sort((a, b) => a - b);
  if (!all) ids = ids.filter((id) => N?.effect?.[id] && !N.effect[id].startsWith("effect ")).filter((id) => val.some((v) => v[id]));
  return (
    <div className="card">
      <h3 style={{ cursor: "pointer" }} onClick={() => setOpen(!open)}>{open ? "▾" : "▸"} Hero stats in this fight ({ids.length})</h3>
      {open && (
        <>
          <p className="small muted">What the game resolved for each hero before the fight - every boost already applied. <label><input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} /> include unnamed and zero stats</label></p>
          <div className="scroll">
            <table className="small">
              <thead><tr><th>stat</th>{heroes.map((u) => <th key={u.index} className="num" title={sideOf(u) ? "defender" : "attacker"}>{sideOf(u) ? "D" : "A"}{u.index}: {n(N, "hero", u.heroId)}</th>)}</tr></thead>
              <tbody>{ids.map((id) => (
                <tr key={id}><td title={`effect ${id}`}>{n(N, "effect", id)}</td>{val.map((v, i) => <td key={i} className="num">{v[id] == null ? "" : effVal(N, id, v[id])}</td>)}</tr>
              ))}</tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
