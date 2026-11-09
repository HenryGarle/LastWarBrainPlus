import { Component, useEffect, useLayoutEffect, useRef, useState } from "react";
import Overview from "../explorer/Overview.jsx";
import ServerPage from "../explorer/ServerPage.jsx";
import AlliancePage from "../explorer/AlliancePage.jsx";
import PlayerPage from "../explorer/PlayerPage.jsx";
import Compare, { compareHref } from "../explorer/Compare.jsx";
import MapPage from "../explorer/MapPage.jsx";
import Rankings, { parseRankingsRoute, rankingsHref } from "../explorer/Rankings.jsx";
import { NamesPage, ChangesPage } from "../explorer/Overview.jsx";
import { NewAccountsPage } from "../explorer/NewAccounts.jsx";
import { OnlineComparePage } from "../explorer/Online.jsx";
import { ServersPage } from "../explorer/ServersPage.jsx";
import { HqLevelsPage } from "../explorer/HqLevels.jsx";
import { PlayersPage } from "../explorer/PlayersPage.jsx";
import { SeasonGroupPage } from "../explorer/SeasonGroup.jsx";
import { FavStar } from "../explorer/favs.jsx";
import { SendPosition } from "./Account.jsx";
import { useJson } from "../explorer/PlayerTabs.jsx";

// The alliance's hive (Insights.Hives: centre of its biggest group of bases on the newest map).
function SendHive({ id, tag }) {
  const hives = useJson("/api/x/hives");
  const h = Array.isArray(hives?.rows) ? hives.rows.find((r) => r.allianceId === id) : Array.isArray(hives) ? hives.find((r) => r.allianceId === id) : null;
  if (!h) return null;
  return <SendPosition sid={h.server} x={h.x} y={h.y} label={`[${tag || h.tag}] hive (${h.hive} bases)`} text="📍 Send hive to me" />;
}
import { HeroModelCard } from "../explorer/Intel.jsx";
import { href } from "../explorer/format.js";
import { ServerLink, Tag } from "../explorer/bits.jsx";
import { setTitle } from "../nav.js";

// #/x                                  all servers
// #/x/server/1967   #/x/alliance/<id>   #/x/player/<uid>
// #/x/map/1967[/x,y[,uid]][?scan=N]     #/x/compare/<servers|alliances|players>/<a,b>
// #/x/rankings/<entity>/<metric>[?at=&vs=&server=&limit=&chart=]
// Anything after "?" is view state. For map and rankings it picks the data, so it is part of the
// fetch; on the other pages it is a filter the page itself keeps (nav.js useHashParam).
const parse = () => {
  const [path, qs = ""] = (window.location.hash || "#/x").split("?");
  const [, , kind = "home", id, extra = ""] = path.split("/");
  const k = kind || "home";
  const dataQuery = (k === "map" || k === "rankings") && qs ? `?${qs}` : "";
  return { kind: k, id: id ? decodeURIComponent(id) : null, extra: extra + dataQuery };
};

// One page failing to render must not take the whole dashboard down with it.
class Boundary extends Component {
  state = { error: null };
  static getDerivedStateFromError(error) { return { error }; }
  componentDidUpdate(prev) { if (prev.resetKey !== this.props.resetKey && this.state.error) this.setState({ error: null }); }
  render() {
    return this.state.error
      ? <p className="err">This page hit a problem rendering: {String(this.state.error.message || this.state.error)}</p>
      : this.props.children;
  }
}

const endpoint = ({ kind, id, extra }) =>
  kind === "home" ? "/api/x/overview"
  : kind === "compare" ? `/api/x/compare/${id || "servers"}?ids=${extra}`
  : kind === "rankings" ? (() => {
      const { entity, metric, q } = parseRankingsRoute({ id, extra });
      return `/api/x/rankings?${new URLSearchParams({ entity, metric, ...q })}`;
    })()
  : kind === "map" ? `/api/x/map/${id}${extra.includes("?scan=") ? `?scan=${extra.split("?scan=")[1]}` : ""}`
  : `/api/x/${kind}/${encodeURIComponent(id)}`;

// The Explorer's own sections. Server / alliance / player pages sit under "Overview".
const SECTIONS = [
  ["home", "Overview", href.home()],
  ["players", "Players", "#/x/players"],
  ["rankings", "Rankings", rankingsHref()],
  ["compare", "Compare", compareHref("servers", [])],
  ["names", "Name changes", "#/x/names"],
  ["newaccounts", "New accounts", "#/x/newaccounts"],
  ["online", "Server activity", "#/x/online"],
  ["hq", "HQ levels", "#/x/hq"],
  ["servers", "Servers & groups", "#/x/servers"],
  ["season", "Season group", "#/x/season"],
  ["hero", "Hero power model", "#/x/hero"],
];
const OWN = ["rankings", "compare", "names", "hero", "newaccounts", "online", "servers", "season", "hq", "players"];
const sectionOf = (kind) => (OWN.includes(kind) ? kind : "home");
// Pages that fetch their own data (nothing for the Explorer to load first).
const SELF_LOADING = ["names", "hero", "changes", "newaccounts", "online", "servers", "season", "hq", "players"];
const HeroPage = () => <HeroModelCard />;
const ExplorerMap = (p) => <MapPage {...p} base="#/x/map" />;

// Where you were on each page, so Back (or returning via a link) lands at the same spot. The page
// scrolls inside <main> (the header stays put), not the window.
const scrolls = new Map();
const scroller = () => document.querySelector("main") || document.scrollingElement;

export default function Explorer() {
  const [route, setRoute] = useState(parse);
  const [data, setData] = useState(null);
  const [error, setError] = useState(null);
  const shownKey = useRef(null);

  useEffect(() => {
    // Save the position BEFORE the new route renders - the loading state shrinks the page to nothing.
    const on = () => { if (shownKey.current) scrolls.set(shownKey.current, scroller().scrollTop); setRoute((cur) => {
      const next = parse();
      // A query-only change on a normal page is view state - keep the same route object.
      return next.kind === cur.kind && next.id === cur.id && next.extra === cur.extra ? cur : next;
    }); };
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);

  const key = `${route.kind}/${route.id}/${route.extra}`;

  useEffect(() => {
    let live = true;
    setData(null);
    setError(null);
    // A comparison with nothing picked yet has nothing to fetch - just the picker.
    if ((route.kind === "compare" && !route.extra) || SELF_LOADING.includes(route.kind)) {
      setData({ key, d: { entities: [], metrics: [] } });
      return () => { live = false; };
    }
    fetch(endpoint(route))
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`${r.status}`))))
      .then((d) => live && setData({ key, d }))
      .catch((e) => live && setError(e.message));
    return () => { live = false; };
  }, [key]);   // eslint-disable-line react-hooks/exhaustive-deps

  const current = data && data.key === key ? data.d : null;

  // Map and rankings re-fetch on every control change; keep the scroll there, reset elsewhere.
  useLayoutEffect(() => {
    if (!current) return;
    const sameView = shownKey.current && shownKey.current.split("/").slice(0, 2).join("/") === key.split("/").slice(0, 2).join("/")
      && (route.kind === "rankings" || route.kind === "map");
    if (!sameView) {
      const to = scrolls.get(key) ?? 0;
      const el = scroller();
      el.scrollTop = to;
      // Parts of a page load after it (a server's player list) and move things; put it back as they
      // do, unless the reader has scrolled in the meantime.
      let last = el.scrollTop;
      if (to > 0) [250, 700, 1500].forEach((ms) => setTimeout(() => {
        if (Math.abs(el.scrollTop - last) > 2 && el.scrollTop !== to) return;   // they moved it
        el.scrollTop = to; last = el.scrollTop;
      }, ms));
    }
    shownKey.current = key;
  }, [current, key]);   // eslint-disable-line react-hooks/exhaustive-deps

  const title = !current ? null
    : route.kind === "server" ? `Server #${route.id}`
    : route.kind === "alliance" ? `Alliance ${current.tag || ""}`.trim()
    : route.kind === "player" ? current.name || route.id
    : route.kind === "compare" ? `Compare ${route.id || "servers"}`
    : route.kind === "map" ? `Map #${route.id}`
    : route.kind === "rankings" ? `Rankings - ${current.label || ""} (${current.entity || ""})`
    : route.kind === "names" ? "Name changes"
    : route.kind === "newaccounts" ? "New accounts"
      : route.kind === "online" ? "Server activity"
      : route.kind === "servers" ? "Servers & groups"
      : route.kind === "season" ? "Season group"
      : route.kind === "hq" ? "HQ levels"
      : route.kind === "players" ? "Players"
    : route.kind === "changes" ? "What changed"
    : route.kind === "hero" ? "Hero power model"
    : "Overview";
  useEffect(() => { if (title) setTitle(title); }, [title]);

  const Page = { home: Overview, server: ServerPage, alliance: AlliancePage, player: PlayerPage, compare: Compare,
    map: ExplorerMap, rankings: Rankings, names: NamesPage, hero: HeroPage, changes: ChangesPage, newaccounts: NewAccountsPage, online: OnlineComparePage, servers: ServersPage, season: SeasonGroupPage, hq: HqLevelsPage, players: PlayersPage }[route.kind];
  const cmpWith = { server: "servers", alliance: "alliances", player: "players" }[route.kind];
  const favKind = { server: "server", alliance: "alliance", player: "player" };
  const section = sectionOf(route.kind);
  const server = route.kind === "server" || route.kind === "map" ? route.id : current?.home ?? current?.server; // a player belongs to their home server, not where a base is visiting

  return (
    <div className="explorer">
      <nav className="subnav">
        {SECTIONS.map(([k, label, to]) => (
          <a key={k} href={to} className={section === k ? "on" : ""} aria-current={section === k ? "page" : undefined}>{label}</a>
        ))}
      </nav>
      {/* Breadcrumbs: every level above this page, each one a link. */}
      <div className="crumbs small">
        {section === "home" && route.kind !== "home" && <a href={href.home()}>Overview</a>}
        {server && route.kind !== "server" && section === "home" && <> › <ServerLink id={server} /></>}
        {route.kind === "player" && current?.allianceId && <> › <Tag id={current.allianceId} tag={current.tag} /></>}
      </div>
      {route.kind !== "home" && (
        <div className="row" style={{ gap: 10, alignItems: "center" }}>
          <h1 className="page-title">{title || "…"}</h1>
          {current && favKind[route.kind] && route.id && <FavStar kind={favKind[route.kind]} id={route.id} />}
        </div>
      )}
      {current && (route.kind === "server" || cmpWith || (route.kind === "map" && route.id)) && (
        <div className="actions">
          {route.kind === "server" && <a className="action primary" href={`#/maps/${route.id}`}>◈ Open the map</a>}
          {route.kind === "map" && <a className="action" href={href.server(route.id)}>Server page</a>}
          {route.kind === "server" && <a className="action" href={rankingsHref("alliances", "heroest", { server: route.id })}>Alliance rankings here</a>}
          {route.kind === "server" && <SendPosition sid={Number(route.id)} x={500} y={500} label={`#${route.id} centre`} text="📍 Send server to me" />}
          {route.kind === "alliance" && <SendHive id={route.id} tag={current?.tag} />}
          {route.kind === "alliance" && <a className="action primary" href={`#/war/${encodeURIComponent(route.id)},`}>⚔ War game</a>}
          {route.kind === "alliance" && current?.server && <a className="action" href={`#/maps/${current.server}`}>◈ Map of #{current.server}</a>}
          {route.kind === "alliance" && current?.server && <a className="action" href={rankingsHref("alliances", "heroest", { server: current.server })}>Rankings on #{current.server}</a>}
          {cmpWith && <a className="action" href={compareHref(cmpWith, [route.id])}>Compare with others</a>}
        </div>
      )}
      {error && <p className="err">Could not load: {error}</p>}
      {!current && !error && <p className="muted">Loading…</p>}
      {current && <Boundary resetKey={key}><Page d={current} data={current} route={route} /></Boundary>}
    </div>
  );
}
