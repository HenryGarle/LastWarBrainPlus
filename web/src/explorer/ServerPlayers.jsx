import { useEffect, useMemo, useState } from "react";
import { useHashParam } from "../nav.js";
import { SortTable } from "./charts.jsx";
import { big, int } from "./format.js";
import { Flag, countryName, CountryOptions } from "./Online.jsx";
import { useJson } from "./PlayerTabs.jsx";
import { Delta, Status, statusOf, Tag, ServerLink, PlayerLink, Hero } from "./bits.jsx";
import { Seen } from "./Intel.jsx";

const PAGE = 250;

/**
 * Every player on the server's newest map, filterable. The origin filter is the point: an
 * account's uid carries the server it was created on, so "created elsewhere" is everyone who
 * has transferred in at some time - this wave or any before it.
 */
// country = the profile country (where the account is from - "created in"); flag = the flag they choose to show ("displays as").
export default function ServerPlayers({ server, origin, setOrigin, country = "all", setCountry = () => {}, flag = "all", setFlag = () => {}, onRows }) {
  const [rows, setRows] = useState(null);
  const [alliance, setAlliance] = useHashParam("alliance", "all");
  const [name, setName] = useHashParam("name", "");
  const [shown, setShown] = useState(PAGE);

  useEffect(() => {
    let live = true;
    setRows(null);
    fetch(`/api/x/server/${server}/players`).then((r) => r.json()).then((d) => { if (!live) return; setRows(d); onRows?.(d); });
    return () => { live = false; };
  }, [server]);

  useEffect(() => setShown(PAGE), [origin, alliance, name, country, flag]);

  // Every country we know is offered (Henry 2026-11-02), with how many of them are on this server ("" = the game did not say).
  const known = useJson("/api/x/countries");
  const tally = (key) => {
    const m = new Map();
    (rows || []).forEach((r) => m.set(r[key] || "", (m.get(r[key] || "") || 0) + 1));
    return m;
  };
  const countries = useMemo(() => tally("country"), [rows]);   // eslint-disable-line react-hooks/exhaustive-deps
  const flags = useMemo(() => tally("flag"), [rows]);          // eslint-disable-line react-hooks/exhaustive-deps
  const is = (v, want) => want === "all" || (v || "") === (want === "unknown" ? "" : want);

  const origins = useMemo(() => {
    if (!rows) return [];
    const m = new Map();
    rows.forEach((r) => m.set(r.origin, (m.get(r.origin) || 0) + 1));
    return [...m.entries()].sort((a, b) => b[1] - a[1]);
  }, [rows]);

  const tags = useMemo(() => {
    if (!rows) return [];
    const m = new Map();
    rows.forEach((r) => { if (r.tag) m.set(r.allianceId, r.tag); });
    return [...m.entries()].sort((a, b) => a[1].localeCompare(b[1]));
  }, [rows]);

  const filtered = useMemo(() => {
    if (!rows) return [];
    const q = name.trim().toLowerCase();
    return rows.filter((r) =>
      (origin === "all" || (origin === "here" ? r.origin === server
        : origin === "elsewhere" ? r.origin !== server : String(r.origin) === origin)) &&
      (alliance === "all" || (alliance === "none" ? !r.tag : r.allianceId === alliance)) &&
      is(r.country, country) && is(r.flag, flag) &&
      (!q || (r.name || "").toLowerCase().includes(q) || r.uid.includes(q)));
  }, [rows, origin, alliance, name, server, country, flag]);

  const power = filtered.reduce((s, r) => s + (r.heroPower ?? r.heroEst ?? 0), 0);

  const csv = () => {
    const cols = ["name", "uid", "tag", "heroPower", "heroEst", "power", "prev", "hq", "origin", "country", "flag"];
    const esc = (v) => (v == null ? "" : /[",\n]/.test(String(v)) ? `"${String(v).replace(/"/g, '""')}"` : v);
    navigator.clipboard.writeText([cols.join(","), ...filtered.map((r) => cols.map((c) => esc(r[c])).join(","))].join("\n"));
  };

  return (
    <div className="card">
      <h2>All players {rows && <span className="muted">({int(rows.length)})</span>}</h2>
      <div className="filters">
        <select value={origin} onChange={(e) => setOrigin(e.target.value)}>
          <option value="all">Created anywhere</option>
          <option value="here">Created here (#{server})</option>
          <option value="elsewhere">Created elsewhere</option>
          {origins.filter(([o]) => o !== server).map(([o, n]) => (
            <option key={o} value={String(o)}>Created on #{o} ({n})</option>
          ))}
        </select>
        <select value={alliance} onChange={(e) => setAlliance(e.target.value)}>
          <option value="all">Any alliance</option>
          <option value="none">No alliance</option>
          {tags.map(([id, tag]) => <option key={id} value={id}>{tag}</option>)}
        </select>
        <select className="flag-select" value={country} onChange={(e) => setCountry(e.target.value)} title="The profile country - where the account is from">
          <option value="all">Created in: any country</option>
          <CountryOptions list={known?.countries} here={countries} label="Created in" />
          <option value="unknown">Created in: unknown{countries.get("") ? ` (${countries.get("")})` : ""}</option>
        </select>
        <select className="flag-select" value={flag} onChange={(e) => setFlag(e.target.value)} title="The flag the player chooses to show">
          <option value="all">Displays as: any flag</option>
          <CountryOptions list={known?.flags} here={flags} label="Displays" />
          <option value="unknown">Displays: no flag{flags.get("") ? ` (${flags.get("")})` : ""}</option>
        </select>
        <input placeholder="name or uid" value={name} onChange={(e) => setName(e.target.value)} />
        <span className="small muted">
          {int(filtered.length)} player{filtered.length === 1 ? "" : "s"}{power ? ` · ${big(power)} hero power` : ""}
        </span>
        <button onClick={csv} disabled={!filtered.length}>Copy as CSV</button>
      </div>
      {!rows ? (
        <p className="muted small">Loading every player…</p>
      ) : (
        <>
          <SortTable
            initial={{ key: "hero", dir: -1 }}
            rows={filtered}
            limit={shown}
            empty="No players match those filters."
            columns={[
              { key: "name", label: "player", render: (r) => <PlayerLink uid={r.uid} name={r.name} /> },
              { key: "tag", label: "alliance", render: (r) => <Tag id={r.allianceId} tag={r.tag} /> },
              { key: "hero", label: "hero power", num: true, sort: (r) => r.heroPower ?? r.heroEst, render: (r) => <Hero board={r.heroPower} est={r.heroEst} /> },
              { key: "power", label: "total power", num: true, render: (r) => big(r.power) },
              { key: "change", label: "change", num: true, sort: (r) => (r.prev != null && r.power != null ? r.power - r.prev : null),
                render: (r) => <Delta value={r.prev != null && r.power != null ? r.power - r.prev : null} /> },
              { key: "status", label: "activity", sort: (r) => statusOf(r.power, r.prev, r.active7), render: (r) => <Status value={statusOf(r.power, r.prev, r.active7)} /> },
              { key: "lastSeen", label: "last seen", sort: (r) => r.lastSeen || "", render: (r) => <Seen iso={r.lastSeen} /> },
              { key: "hq", label: "HQ", num: true },
              { key: "country", label: "created in", sort: (r) => (r.country ? countryName(r.country) : null), render: (r) => (r.country ? <span><Flag code={r.country} /> {countryName(r.country)}</span> : <span className="muted">–</span>) },
              { key: "flag", label: "displays as", sort: (r) => (r.flag ? countryName(r.flag) : null), render: (r) => (!r.flag ? <span className="muted">–</span>
                : r.flag === r.country ? <span className="muted"><Flag code={r.flag} /> same</span> : <span><Flag code={r.flag} /> {countryName(r.flag)}</span>) },
              { key: "origin", label: "created on", render: (r) => (r.origin === server ? <span className="muted">here</span> : <ServerLink id={r.origin} />) },
            ]}
          />
          {filtered.length > shown && (
            <button style={{ marginTop: 8 }} onClick={() => setShown((s) => s + PAGE * 2)}>
              Show more ({int(filtered.length - shown)} left)
            </button>
          )}
        </>
      )}
    </div>
  );
}
