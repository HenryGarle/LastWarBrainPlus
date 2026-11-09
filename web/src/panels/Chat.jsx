import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { api } from "../api.js";
import { href, ago, int } from "../explorer/format.js";
import { Tag } from "../explorer/bits.jsx";
import { sectionArgs, usePolling } from "../nav.js";
import ChatStats from "./ChatStats.jsx";

// Room ids are the game's; these make them readable. Unknown prefixes show as-is.
export function roomLabel(id) {
  let m;
  if ((m = /^country_(\d+)$/.exec(id))) return `World #${m[1]}`;
  if ((m = /^country_(\d+)_(\d+)$/.exec(id))) return `World #${m[1]} (season ${m[2]})`;
  if ((m = /^warzone_(\d+)/.exec(id))) return `Warzone #${m[1]}`;
  if ((m = /^custom_season_sustain_(\d+)/.exec(id))) return `Season room ${m[1]}`;
  if ((m = /^crossbattle_/.exec(id))) return "Cross-server battle";
  if ((m = /^alliance_/.exec(id))) return "Alliance";
  if ((m = /^language_(.+)$/.exec(id))) return `Language: ${m[1]}`;
  return id;
}
const serverOfRoom = (id) => Number((/^(?:country|warzone)_(\d+)/.exec(id) || [])[1]) || null;

const time = (iso) => new Date(iso).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
const day = (iso) => new Date(iso).toLocaleDateString(undefined, { weekday: "short", day: "numeric", month: "short" });

// The server the game prints beside a name: where they are FROM (it follows them when they
// teleport over for a war). Highlighted when that is not this room's server; an arrow shows
// where they physically are when that differs again.
function ServerTag({ who, home, origin }) {
  if (!who?.server) return null;
  const away = home && who.server !== home;
  const title = [
    `from #${who.server}`,
    who.current && who.current !== who.server ? `now on #${who.current}` : null,
    origin && origin !== who.server ? `account created on #${origin}` : null,
    who.level ? `HQ ${who.level}` : null,
    who.power ? `${(who.power / 1e6).toFixed(1)}M power` : null,
    who.lang ? `lang ${who.lang}` : null,
  ].filter(Boolean).join(" · ");
  return (
    <span className="chat-server" title={title}>
      <a className={away ? "chat-origin" : "muted"} href={href.server(who.server)}>#{who.server}</a>
      {who.current && who.current !== who.server && <span className="chat-origin"> → #{who.current}</span>}
    </span>
  );
}

// Sticker names come from the extracted game tables; loaded once and shared.
let stickerNames = null;
const stickerLoad = fetch("/api/game/stickers").then((r) => (r.ok ? r.json() : {})).catch(() => ({})).then((n) => (stickerNames = n));

function Sticker({ id, param }) {
  const [names, setNames] = useState(stickerNames);
  useEffect(() => { if (!names) stickerLoad.then(setNames); }, [names]);
  const name = names?.[id] || `sticker ${id}`;
  // ponytail: sticker 5 with 1-6 looks like a dice roll; other params shown raw.
  return <span className="chat-sticker" title={`<lwSticker:${id}:${param || ""}:>`}>[{name}{param ? ` · ${param}` : ""}]</span>;
}

// Stickers and gifts carry no text of their own; say what they are rather than show markup.
function Body({ m }) {
  if (m.gift) {
    const g = m.gift;
    return (
      <span className="chat-gift">
        🎁 {g.anonymous ? "someone" : g.fromUid ? <a href={href.player(g.fromUid)}>{g.from}</a> : g.from}
        {" sent "}{g.num > 1 ? `${g.num} gifts` : "a gift"}{" to "}
        {g.toUid ? <a href={href.player(g.toUid)}>{g.to}</a> : g.to || "?"}
      </span>
    );
  }
  const st = /^<lwSticker:(\d+)(?::(\d*))?:?>$/.exec(m.text || "");
  if (st) return <Sticker id={st[1]} param={st[2]} />;
  return m.text?.trim() ? m.text : <span className="muted">[message type {m.type}]</span>;
}

const shortDate = (iso) => new Date(iso).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });

// Pull rooms' history back to a date - by default the oldest date any room is held from, so every
// room can be brought level with the longest-held one. Runs on the server one room at a time.
function Backfill({ rooms, room, onChange }) {
  const oldest = rooms.map((r) => r.firstAt).filter(Boolean).sort()[0];
  const [until, setUntil] = useState("");
  const [jobs, setJobs] = useState([]);
  const [err, setErr] = useState(null);
  const target = until || (oldest ? oldest.slice(0, 10) : "");
  const active = jobs.some((j) => j.state === "queued" || j.state === "running");
  // Backfill jobs: watched every 3 s while one is queued or running; otherwise read once.
  const tick = () => api.backfillJobs().then(setJobs).catch(() => {});
  usePolling(() => { tick(); if (active) onChange?.(); }, 3000, active);
  useEffect(() => { tick(); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  const start = async (ids) => {
    setErr(null);
    if (!ids.length) { setErr("every room already reaches that date"); return; }
    try { setJobs(await api.backfillStart(ids, target)); } catch (e) { setErr(e.message); }
  };
  const short = rooms.filter((r) => r.firstAt && r.firstAt.slice(0, 10) > target).map((r) => r.room);
  return (
    <div className="chat-backfill">
      <div className="small muted">Backfill history back to</div>
      <div className="row" style={{ gap: 6, marginTop: 4 }}>
        <input type="date" value={target} onChange={(e) => setUntil(e.target.value)} />
      </div>
      <div className="row" style={{ gap: 6, marginTop: 6, flexWrap: "wrap" }}>
        <button disabled={!room || !target} onClick={() => start([room])}>This room</button>
        <button disabled={!target} onClick={() => start(short)} title="Every room whose first message is after this date">All rooms ({short.length})</button>
        {active && <button onClick={() => api.backfillCancel().then(setJobs)}>Stop</button>}
      </div>
      {err && <div className="err small">{err}</div>}
      {jobs.length > 0 && (
        <div className="small" style={{ marginTop: 8 }}>
          {jobs.map((j) => (
            <div key={j.room} className="backfill-job" title={j.progress}>
              <b>{roomLabel(j.room)}</b> <span className={j.state === "running" ? "status-good" : j.state === "failed" ? "err" : "muted"}>{j.state}</span>
              <div className="muted">{j.heldFrom ? `from ${shortDate(j.heldFrom)}` : ""}{j.progress ? ` · ${j.progress}` : ""}</div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

const PAGE = 150;
const KEEP = 3000; // messages held on screen per room before the oldest are let go

// Chat has two sub-tabs: the rooms (#/chat/<room>) and stats across all of them (#/chat/stats).
const onStats = () => sectionArgs()[0] === "stats";
export default function ChatSection(props) {
  const [stats, setStats] = useState(onStats);
  useEffect(() => {
    const on = () => window.location.hash.startsWith("#/chat") && setStats(onStats());
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);
  return (
    <>
      <nav className="subnav">
        <a href="#/chat" className={stats ? "" : "on"}>Chat</a>
        <a href="#/chat/stats" className={stats ? "on" : ""}>Stats</a>
      </nav>
      {stats ? <ChatStats /> : <Chat {...props} />}
    </>
  );
}

function Chat({ runner }) {
  const [rooms, setRooms] = useState([]);
  const [status, setStatus] = useState(null);
  // #/chat/<room> names the room; the last one used is the fallback.
  const [room, setRoom] = useState(() => (onStats() ? "" : sectionArgs()[0]) || (() => { try { return localStorage.getItem("chat.room") || ""; } catch { return ""; } })());
  useEffect(() => {
    const on = () => { const r = window.location.hash.startsWith("#/chat") && sectionArgs()[0]; if (r && r !== "stats") setRoom(r); };
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);
  useEffect(() => {
    if (room && sectionArgs()[0] !== room && window.location.hash.startsWith("#/chat"))
      window.history.replaceState(null, "", `#/chat/${encodeURIComponent(room)}`);
  }, [room]);
  const [msgs, setMsgs] = useState([]);
  const [more, setMore] = useState(true);
  const [unread, setUnread] = useState({});
  const [filter, setFilter] = useState("");
  const [add, setAdd] = useState("");
  const [err, setErr] = useState(null);
  const [live, setLive] = useState(false);
  const [users, setUsers] = useState({}); // uid -> profile, filled by "users" events as lookups land
  const roomRef = useRef(room);
  const box = useRef(null);
  const stick = useRef(true);   // follow the bottom unless the reader has scrolled up
  const keep = useRef(null);    // scroll height before prepending older messages

  const load = useCallback(async () => {
    try {
      const [r, s] = await Promise.all([api.chatRooms(), api.chatStatus()]);
      setRooms(r);
      setStatus(s);
      setRoom((cur) => cur || r[0]?.room || "");
    } catch (e) { setErr(e.message); }
  }, []);

  usePolling(load, 60000);

  // Room switch: newest page from the database, then live messages append to it.
  useEffect(() => {
    roomRef.current = room;
    if (!room) return;
    try { localStorage.setItem("chat.room", room); } catch { /* private window */ }
    setMsgs([]);
    setMore(true);
    stick.current = true;
    setUnread((u) => ({ ...u, [room]: 0 }));
    const a = anchor.current?.room === room ? anchor.current : null;
    anchor.current = null;
    loadPage(room, a?.seq);
  }, [room]);

  // A page of the room: the newest, or (seq) the stretch around one message, which is then
  // highlighted and scrolled to - how a search result is shown in its context.
  const anchor = useRef(null);
  const [focus, setFocus] = useState(null);
  // While an older stretch is shown, live messages would land after it out of place; they are
  // counted as unread instead, and "back to the newest" picks them up.
  const focusRef = useRef(null);
  useEffect(() => { focusRef.current = focus; }, [focus]);
  const loadPage = (r, seq) => {
    stick.current = !seq;
    setFocus(seq || null);
    if (!seq) setUnread((u) => ({ ...u, [r]: 0 }));
    api.chatMessages(r, seq ? seq + 40 : undefined).then((page) => {
      if (roomRef.current !== r) return;
      setMsgs(page.reverse());
      setMore(page.length === PAGE);
    }).catch((e) => setErr(e.message));
  };
  const jumpTo = (m) => {
    setFilter("");
    if (m.room === room) loadPage(room, m.seq);
    else { anchor.current = { room: m.room, seq: m.seq }; setRoom(m.room); }
  };
  useLayoutEffect(() => {
    if (!focus) return;
    document.getElementById(`msg-${focus}`)?.scrollIntoView({ block: "center" });
  }, [focus, msgs]);

  // Search runs over everything stored, not the loaded page. 100 matches a page; "search older"
  // continues below the last one, and at the end of stored history the room can be pulled
  // further back from the game.
  const [scope, setScope] = useState("room");
  const [found, setFound] = useState(null);
  const [searching, setSearching] = useState(false);
  const [pulling, setPulling] = useState(false);
  const runSearch = useCallback(async (q, more) => {
    setSearching(true);
    try {
      const r = await api.chatSearch(q, scope === "room" ? room : "", more ? found?.oldest : null);
      setFound((prev) => (more && prev ? { ...r, messages: [...prev.messages, ...r.messages] } : r));
    } catch (e) { setErr(e.message); }
    finally { setSearching(false); }
  }, [scope, room, found?.oldest]);
  useEffect(() => {
    const q = filter.trim();
    if (q.length < 2) { setFound(null); return; }
    const t = setTimeout(() => runSearch(q, false), 350);
    return () => clearTimeout(t);
  }, [filter, scope, room]); // eslint-disable-line react-hooks/exhaustive-deps
  const pullOlder = async () => {
    setPulling(true); setErr(null);
    try { await api.chatBackfill(room, 20); await runSearch(filter.trim(), false); await load(); }
    catch (e) { setErr(e.message); }
    finally { setPulling(false); }
  };

  useEffect(() => {
    const es = new EventSource("/api/chat/live");
    es.onopen = () => setLive(true);
    es.onerror = () => setLive(false);
    es.addEventListener("users", (e) => setUsers((u) => ({ ...u, ...JSON.parse(e.data) })));
    es.onmessage = (e) => {
      const m = JSON.parse(e.data);
      if (m.room === roomRef.current && !focusRef.current) {
        setMsgs((prev) => {
          if (prev.some((p) => p.seq === m.seq)) return prev;
          const next = [...prev, m];
          if (prev.length && prev[prev.length - 1].seq > m.seq) next.sort((a, b) => a.seq - b.seq);
          return next.slice(-KEEP);
        });
      } else {
        setUnread((u) => ({ ...u, [m.room]: (u[m.room] || 0) + 1 }));
      }
    };
    return () => es.close();
  }, []);

  const older = async () => {
    if (!msgs.length) return;
    keep.current = box.current?.scrollHeight ?? null;
    stick.current = false;
    const page = await api.chatMessages(room, msgs[0].seq);
    setMsgs((prev) => [...page.reverse(), ...prev]);
    setMore(page.length === PAGE);
  };

  useLayoutEffect(() => {
    const el = box.current;
    if (!el) return;
    if (keep.current !== null) { el.scrollTop = el.scrollHeight - keep.current; keep.current = null; }
    else if (stick.current) el.scrollTop = el.scrollHeight;
  }, [msgs]);

  const onScroll = () => {
    const el = box.current;
    stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 60;
  };

  const follow = async (id, on) => {
    try { await api.chatFollow(id, on); await load(); } catch (e) { setErr(e.message); }
  };

  const archive = async (id, on) => {
    try { await api.chatArchive(id, on); await load(); } catch (e) { setErr(e.message); }
  };

  // Irreversible: the reader types the room id to go ahead.
  const remove = async (id, messages) => {
    const typed = window.prompt(`Delete ALL ${int(messages)} stored messages of ${id} for good? This cannot be undone - archive keeps them instead.\n\nType the room id to confirm:`);
    if (typed === null) return;
    if (typed.trim() !== id) { setErr("Room id did not match - nothing deleted."); return; }
    try {
      const r = await api.chatDelete(id, typed.trim());
      setErr(`Deleted ${int(r.removed)} messages from ${id}.`);
      setRoom("");
      await load();
    } catch (e) { setErr(e.message); }
  };

  const addRoom = async (e) => {
    e.preventDefault();
    const v = add.trim();
    if (!v) return;
    const id = /^\d+$/.test(v) ? `country_${v}` : v;
    await follow(id, true);
    setAdd("");
    setRoom(id);
  };

  const listener = status?.listeners?.find((l) => l.alive);
  const attached = runner && status?.listeners?.some((l) => l.runner === runner.id && l.alive);
  const goLive = async () => {
    try { await api.chatStart(runner.id); await load(); } catch (e) { setErr(e.message); }
  };
  // World chat reads cross-server, so any account will do - and a throwaway guest keeps the
  // real account out of it. Guests log in once only, hence a fresh one each time.
  const [starting, setStarting] = useState(false);
  const liveOnGuest = async () => {
    setStarting(true);
    setErr(null);
    try {
      const g = await api.newGuest(`chat-${new Date().toISOString().slice(5, 16).replace(/[-:T]/g, "")}`);
      await api.start(g.id);
      setTimeout(load, 4000);
    } catch (e) { setErr(e.message); }
    finally { setStarting(false); }
  };

  const [busy, setBusy] = useState({});
  const [tErr, setTErr] = useState({});
  const translate = async (m) => {
    setBusy((b) => ({ ...b, [m.seq]: true }));
    setTErr((e) => ({ ...e, [m.seq]: null }));
    try {
      const t = await api.chatTranslate(m.room, m.seq);
      setMsgs((prev) => prev.map((p) => (p.room === m.room && p.seq === m.seq ? { ...p, translation: { text: t.text, from: t.from } } : p)));
    } catch (e) {
      setTErr((x) => ({ ...x, [m.seq]: e.message }));
    } finally {
      setBusy((b) => ({ ...b, [m.seq]: false }));
    }
  };

  const shown = msgs;

  const home = serverOfRoom(room);
  const current = rooms.find((r) => r.room === room);

  return (
    <div className="chat-panel">
      <aside className="chat-rooms">
        <form onSubmit={addRoom} className="row" style={{ gap: 6 }}>
          <input value={add} onChange={(e) => setAdd(e.target.value)} placeholder="server no. or room id" />
          <button type="submit">Follow</button>
        </form>
        <div className="small muted" style={{ margin: "8px 0 4px" }}>
          Followed rooms are polled live; the rest are stored history.
        </div>
        {rooms.map((r) => (
          <div key={r.room} className={`chat-room ${r.room === room ? "on" : ""}`} onClick={() => setRoom(r.room)} title={r.room}>
            <div className="row" style={{ gap: 6 }}>
              {r.followed && <span className={`dot ${listener ? "ready" : "stopped"}`} title="followed" />}
              <span className="chat-room-name">{roomLabel(r.room)}</span>
              {r.state && <span className="small muted" title="left out of chat stats">{r.state}</span>}
              {unread[r.room] > 0 && <span className="badge">{int(unread[r.room])}</span>}
            </div>
            <div className="small muted">{int(r.messages)} held · {r.lastAt ? ago(r.lastAt) : "nothing yet"}</div>
            {r.firstAt && <div className="small muted" title={new Date(r.firstAt).toLocaleString()}>from {shortDate(r.firstAt)}</div>}
          </div>
        ))}
        <Backfill rooms={rooms} room={room} onChange={load} />
      </aside>

      <section className="chat-main">
        <div className="chat-bar">
          <div>
            <strong>{room ? roomLabel(room) : "No room"}</strong>
            {room && <span className="mono small muted"> {room}</span>}
            {home && <> · <a href={href.server(home)}>server page</a></>}
          </div>
          <span className="spacer" />
          <input className="chat-filter" value={filter} onChange={(e) => setFilter(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Escape") setFilter(""); if (e.key === "Enter" && filter.trim().length >= 2) runSearch(filter.trim(), false); }}
            placeholder="search all history: text, name, tag or uid" />
          <select value={scope} onChange={(e) => setScope(e.target.value)} title="Where to search">
            <option value="room">this room</option>
            <option value="all">all rooms</option>
          </select>
          {current && (
            <button onClick={() => follow(room, !current.followed)}>{current.followed ? "Unfollow" : "Follow"}</button>
          )}
          {current && (current.state === "archived"
            ? <button onClick={() => archive(room, false)} title="Count it in chat stats again">Restore</button>
            : <button onClick={() => archive(room, true)} title="Stop following, keep the messages, leave it out of chat stats">Archive</button>)}
          {current && current.messages > 0 && (
            <button className="danger" onClick={() => remove(room, current.messages)} title="Delete every stored message of this room">Delete…</button>
          )}
        </div>

        <div className="chat-state small">
          {listener ? (
            <span><span className="dot ready" /> live via <b>{listener.runner}</b> · {int(listener.received)} pushed · polled {ago(status.lastPollAt)}</span>
          ) : (
            <span className="muted"><span className="dot stopped" /> not live - showing stored history.
              </span>
          )}
          {runner?.state === "ready" && !attached && <button onClick={goLive} style={{ marginLeft: 8 }}>Go live on {runner.name}</button>}
          {!listener && <button className="primary" disabled={starting} onClick={liveOnGuest} style={{ marginLeft: 8 }}>{starting ? "Starting guest..." : "Go live on a fresh guest"}</button>}
          {!live && <span className="err"> · feed disconnected</span>}
          {status?.lastError && <span className="err"> · {status.lastError}</span>}
          {err && <span className="err"> · {err}</span>}
        </div>

        {found && filter.trim().length >= 2 ? (
          <div className="chat-log">
            <div className="small muted chat-search-head">
              {searching ? "Searching…" : <>{int(found.messages.length)}{found.more ? "+" : ""} match{found.messages.length === 1 ? "" : "es"} {scope === "room" ? "in this room" : "in all rooms"}
                {found.oldest && <> · back to {new Date(found.oldest).toLocaleString()}</>}</>}
              {" "}<a href="#" onClick={(e) => { e.preventDefault(); setFilter(""); }}>clear</a>
            </div>
            {found.messages.map((m) => (
              <div key={`${m.room}:${m.seq}`} className="chat-msg chat-hit" onClick={() => jumpTo(m)} title="Show in context">
                <span className="chat-time mono">{day(m.at)} {time(m.at)}</span>
                <span className="chat-who">
                  {scope === "all" && <span className="muted small">{roomLabel(m.room)} · </span>}
                  {m.alliance && <Tag id={m.sender?.allianceId} tag={m.alliance} />}{" "}
                  {m.uid && m.uid !== "system" ? <a href={href.player(m.uid)} onClick={(e) => e.stopPropagation()}>{m.name || m.uid}</a> : <span className="muted">system</span>}
                </span>
                <span className="chat-text">
                  <Body m={m} />
                  {m.translation && <div className="chat-trans"><span className="chat-trans-lang">{m.translation.from ? `${m.translation.from} → en` : "en"}</span> {m.translation.text}</div>}
                </span>
              </div>
            ))}
            <div className="center chat-search-foot">
              {found.more ? <button disabled={searching} onClick={() => runSearch(filter.trim(), true)}>{searching ? "Searching…" : "Search older"}</button>
                : <span className="small muted">End of stored history{found.heldFrom ? ` (this room is held from ${new Date(found.heldFrom).toLocaleString()})` : ""}.</span>}
              {!found.more && scope === "room" && room && (
                <button disabled={pulling} onClick={pullOlder} style={{ marginLeft: 8 }} title="Page further back through this room on a throwaway guest, then search again">
                  {pulling ? "Fetching older history… (about a minute)" : "Fetch older history from the game (~1,000 messages)"}
                </button>
              )}
            </div>
          </div>
        ) : (
        <div className="chat-log" ref={box} onScroll={onScroll}>
          {focus && <div className="center small"><button onClick={() => loadPage(room)}>Back to the newest messages</button></div>}
          {more && msgs.length > 0 && !filter && (
            <div className="center"><button onClick={older}>Load older</button></div>
          )}
          {shown.map((m, i) => {
            const prev = shown[i - 1];
            const newDay = !prev || day(prev.at) !== day(m.at);
            const who = users[m.uid] || m.sender;
            return (
              <div key={m.seq} id={`msg-${m.seq}`}>
                {newDay && <div className="chat-day">{day(m.at)}</div>}
                <div className={m.seq === focus ? "chat-msg chat-focus" : "chat-msg"}>
                  <span className="chat-time mono" title={`seq ${m.seq} · ${new Date(m.at).toLocaleString()}`}>{time(m.at)}</span>
                  <span className="chat-who">
                    {m.alliance && <Tag id={typeof who === "object" ? who?.allianceId : null} tag={m.alliance} />}{" "}
                    {m.uid && m.uid !== "system" ? <a href={href.player(m.uid)}>{m.name || m.uid}</a> : <span className="muted">{m.gift ? "gift" : "system"}</span>}
                    <ServerTag who={who} home={home} origin={m.origin} />
                  </span>
                  <span className="chat-text">
                    <Body m={m} />
                    {m.translation && (
                      <div className="chat-trans">
                        <span className="chat-trans-lang">{m.translation.from ? `${m.translation.from} → en` : "en"}</span> {m.translation.text}
                      </div>
                    )}
                    {tErr[m.seq] && <div className="err small">{tErr[m.seq]}</div>}
                  </span>
                  {m.text && !/^<lwSticker/.test(m.text) && !m.translation && (
                    <button className="chat-tbtn" disabled={busy[m.seq]} onClick={() => translate(m)} title="Translate to English">
                      {busy[m.seq] ? "…" : "Translate"}
                    </button>
                  )}
                </div>
              </div>
            );
          })}
          {room && msgs.length === 0 && <p className="muted center">Nothing stored for this room yet{listener ? " - the next poll will fetch its newest messages." : "."}</p>}
        </div>
        )}
      </section>
    </div>
  );
}
