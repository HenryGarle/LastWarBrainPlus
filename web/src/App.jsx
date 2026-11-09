import { useCallback, useEffect, useRef, useState } from "react";
import { api, eventsUrl } from "./api.js";
import Fleet from "./panels/Fleet.jsx";
import Commands from "./panels/Commands.jsx";
import Wire from "./panels/Wire.jsx";
import Data from "./panels/Data.jsx";
import Sessions from "./panels/Sessions.jsx";
import Ops from "./panels/Ops.jsx";
import Explorer from "./panels/Explorer.jsx";
import Chat from "./panels/Chat.jsx";
import Mail from "./panels/Mail.jsx";
import Scheduled from "./panels/Scheduled.jsx";
import Access, { SignIn } from "./panels/Access.jsx";
import Maps from "./panels/Maps.jsx";
import War from "./panels/War.jsx";
import Account from "./panels/Account.jsx";
import Tracking from "./panels/Tracking.jsx";
import Events from "./panels/Events.jsx";
import Inbox from "./panels/Inbox.jsx";
import Reports from "./panels/Reports.jsx";
import Scans from "./panels/Scans.jsx";
import Search from "./explorer/Search.jsx";
import { hashPath, setTitle, usePolling } from "./nav.js";

// Each tab is a URL section (see nav.js): #/x Explorer, #/chat, #/fleet, #/sessions, #/ops ...
const TABS = [
  ["explorer", "Explore"],
  ["maps", "Maps"],
  ["war", "War game"],
  ["events", "Events"],
  ["chat", "Chat"],
  ["scheduled", "Scheduled"],
  ["fleet", "Fleet"],
  ["sessions", "Sessions"],
  ["mail", "Mail"],
  ["ops", "Operations"],
  ["commands", "Commands"],
  ["wire", "Raw TCP"],
  ["data", "Data"],
  ["access", "Access"],
  ["account", "Account"],
  ["tracking", "Tracking"],
  ["inbox", "Inbox"],
  ["reports", "Reports"],
  ["scans", "Scans"],
];
// What a viewer password opens (the server enforces the same - Access.ViewerMay).
const VIEWER_TABS = ["explorer", "maps", "war", "events", "chat"];
// Pages that show runners (the list is polled only there) and pages that read the live event stream.
const RUNNER_TABS = ["fleet", "sessions", "mail", "ops", "commands", "wire", "inbox", "scans"];
const EVENT_TABS = ["fleet", "ops", "commands", "wire"];

const SECTION_HOME = { explorer: "#/x", maps: "#/maps", war: "#/war", events: "#/events/meteorite", chat: "#/chat", scheduled: "#/scheduled", fleet: "#/fleet", sessions: "#/sessions", mail: "#/mail", ops: "#/ops", commands: "#/commands", wire: "#/wire", data: "#/data", access: "#/access", account: "#/account", tracking: "#/tracking", inbox: "#/inbox", reports: "#/reports", scans: "#/scans/secret" };
const SECTION_OF = { x: "explorer", maps: "maps", war: "war", events: "events", chat: "chat", scheduled: "scheduled", fleet: "fleet", sessions: "sessions", mail: "mail", ops: "ops", commands: "commands", wire: "wire", data: "data", access: "access", account: "account", tracking: "tracking", inbox: "inbox", reports: "reports", scans: "scans" };
const sectionOf = () => SECTION_OF[hashPath().split("/")[0]] || "explorer";

// Frames arrive far faster than React should re-render, so events are buffered and
// flushed on an interval rather than set per message.
const FLUSH_MS = 250;
const MAX_EVENTS = 3000;

export default function App() {
  // Who is signed in: undefined = checking, null = nobody (show the sign-in page).
  const [me, setMe] = useState(undefined);
  const whoAmI = useCallback(() => fetch("/api/auth/me").then((r) => (r.ok ? r.json() : null)).then(setMe).catch(() => setMe(null)), []);
  useEffect(() => { whoAmI(); }, [whoAmI]);
  const admin = me?.role === "admin";
  // The URL decides the tab, so reload, Back and a pasted link all land where they should. Each
  // tab remembers its own last address, so switching away and back returns to the same place.
  const [tab, setTab] = useState(sectionOf);
  const last = useRef({});
  useEffect(() => {
    if (!window.location.hash) window.history.replaceState(null, "", "#/x");
    const on = () => { const t = sectionOf(); last.current[t] = window.location.hash; setTab(t); };
    on();
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);
  useEffect(() => {
    if (!["explorer", "maps", "war", "events"].includes(tab)) setTitle(TABS.find(([id]) => id === tab)?.[1]);
    // A new tab starts at its top (the Explorer restores its own per-page positions).
    const main = document.querySelector("main");
    if (main && tab !== "explorer") main.scrollTop = 0;
  }, [tab]);
  const openTab = (id) => {
    last.current[tab] = window.location.hash;   // includes view state set without a history entry
    const target = id === tab ? SECTION_HOME[id] : last.current[id] || SECTION_HOME[id];
    if (window.location.hash !== target) window.location.hash = target;
  };
  const [runners, setRunners] = useState([]);
  const [selected, setSelected] = useState(null);
  const [events, setEvents] = useState([]);
  const [connected, setConnected] = useState(false);
  const [error, setError] = useState(null);
  const pending = useRef([]);

  const refresh = useCallback(async () => {
    try {
      const list = await api.runners();
      setRunners(list);
      setError(null);
      // A stopped guest is deleted server-side, so the selection can vanish under us.
      setSelected((cur) => (cur && list.some((r) => r.id === cur) ? cur : list[0]?.id ?? null));
      return list;
    } catch (e) {
      setError(e.message);
      return [];
    }
  }, []);

  // Runners: admin-only, and only on the pages that use them. Quick while one is connecting,
  // otherwise every 20 s; each page also refreshes straight after its own start / stop / call.
  const busy = runners.some((r) => r.state === "connecting");
  usePolling(refresh, busy ? 3000 : 20000, admin && RUNNER_TABS.includes(tab));

  // One event stream for the whole app: the wire view, pushes and call results all
  // come down it, so every panel sees the same truth.
  const wantEvents = admin && EVENT_TABS.includes(tab);
  useEffect(() => {
    if (!selected || !wantEvents) return;
    setEvents([]);
    pending.current = [];
    const es = new EventSource(eventsUrl(selected));
    es.onopen = () => setConnected(true);
    es.onerror = () => setConnected(false);
    es.onmessage = (m) => pending.current.push(JSON.parse(m.data));
    const flush = setInterval(() => {
      if (!pending.current.length) return;
      const batch = pending.current;
      pending.current = [];
      setEvents((prev) => [...prev, ...batch].slice(-MAX_EVENTS));
    }, FLUSH_MS);
    return () => {
      clearInterval(flush);
      es.close();
      setConnected(false);
    };
  }, [selected, wantEvents]);

  const runner = runners.find((r) => r.id === selected) || null;
  if (me === undefined) return <p className="muted" style={{ padding: 20 }}>Loading…</p>;
  if (me === null) return <SignIn onDone={whoAmI} />;
  const tabs = TABS.filter(([id]) => admin || VIEWER_TABS.includes(id) || (me?.role === "viewerplus" && ["tracking", "reports"].includes(id)));
  const shownTab = tabs.some(([id]) => id === tab) ? tab : "explorer";
  const Panel =
    { explorer: Explorer, maps: Maps, war: War, events: Events, chat: Chat, scheduled: Scheduled, fleet: Fleet, sessions: Sessions, mail: Mail, ops: Ops, commands: Commands, wire: Wire, data: Data, access: Access, account: Account, tracking: Tracking, inbox: Inbox, reports: Reports, scans: Scans }[shownTab];
  const signOut = () => fetch("/api/auth/logout", { method: "POST" }).then(whoAmI);

  return (
    <div className="app">
      <header>
        <a className="brand" href="#/x"><span className="brand-mark">◆</span> Last War <b>Brain</b></a>
        <nav>
          {tabs.map(([id, label]) => (
            <button key={id} className={shownTab === id ? "on" : ""} aria-current={shownTab === id ? "page" : undefined}
              onClick={() => openTab(id)} title={shownTab === id ? "Back to the start of this tab" : undefined}>
              {label}
            </button>
          ))}
        </nav>
        <span className="spacer" />
        <Search />
        <span className="small muted who" title={me.dev ? "dev mode: this machine is signed in automatically" : me.role}>
          {me.name}{!me.dev && <> · <a href="#" onClick={(e) => { e.preventDefault(); signOut(); }}>sign out</a></>}
        </span>
        {/* The runner picker lives on the pages that use one runner (Commands, Raw TCP, Operations). */}
      </header>
      <main>
        {error && <p className="err">API unreachable: {error}</p>}
        <Panel
          runners={runners}
          runner={runner}
          selected={selected}
          setSelected={setSelected}
          refresh={refresh}
          events={events}
          admin={admin}
        />
      </main>
    </div>
  );
}
