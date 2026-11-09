import { useEffect, useMemo, useState } from "react";
import { BarList, MultiLineChart, SortTable, LineChart } from "../explorer/charts.jsx";
import { Stat, PlayerLink, ServerLink } from "../explorer/bits.jsx";
import { int, when } from "../explorer/format.js";
import { useHashParam, setTitle } from "../nav.js";

// #/chat/stats?days=30 - chat across every stored room. Rooms are held over different spans (1967
// since July, others since they were followed), so rooms are also compared per day held.
const SLOTS = ["var(--series-1)", "var(--series-2)", "var(--series-3)", "var(--series-4)"];
const RANGES = [["7", "7 days"], ["30", "30 days"], ["90", "90 days"], ["0", "everything"]];
const roomName = (r) => (r.server ? `#${r.server}` : r.room);

export default function ChatStats() {
  const [days, setDays] = useHashParam("days", "30");
  const [d, setD] = useState(null);
  const [err, setErr] = useState(null);
  const [stickerNames, setStickerNames] = useState({});

  useEffect(() => { setTimeout(() => setTitle("Chat stats")); /* after App titles the tab */ fetch("/api/game/stickers").then((r) => r.json()).then(setStickerNames).catch(() => {}); }, []);
  useEffect(() => {
    setD(null); setErr(null);
    fetch(`/api/chat/stats?days=${days}`).then((r) => (r.ok ? r.json() : Promise.reject(new Error(r.statusText))))
      .then(setD).catch((e) => setErr(e.message));
  }, [days]);

  // One line per busy room (the four biggest; the series slots stop there) plus the total.
  const daily = useMemo(() => {
    if (!d) return [];
    const top = d.rooms.slice(0, 4);
    return [
      ...top.map((r, i) => ({ key: r.room, label: roomName(r), color: SLOTS[i], points: d.daily.map((x) => ({ x: x.day, y: x.day < r.first || x.day > r.last ? null : x.rooms[r.room] || 0 })) })),
    ];
  }, [d]);

  const header = (
    <div className="row small" style={{ gap: 6, marginBottom: 10, alignItems: "center" }}>
      {RANGES.map(([v, label]) => (
        <button key={v} className={days === v ? "on" : ""} onClick={() => setDays(v)}>{label}</button>
      ))}
      {d?.from && <span className="muted">{when(d.from)} – {when(d.to)} · hours are UTC</span>}
    </div>
  );
  if (err) return <>{header}<p className="err">{err}</p></>;
  if (!d) return <>{header}<p className="muted">Counting…</p></>;
  const t = d.totals;
  const diceMax = Math.max(...d.dice, 1);
  const emojiTotal = t.gameEmoji + t.unicodeEmoji + t.stickers;

  return (
    <div className="explorer chat-stats">
      {header}
      <div className="stats">
        <Stat label="Messages" value={int(t.messages)} sub={`${int(t.rooms)} rooms · ${int(t.senders)} people`} />
        <Stat label="Game emojis" value={int(t.gameEmoji)} sub="the in-game emoji set" />
        <Stat label="Animated stickers" value={int(t.stickers)} sub={`${int(t.diceRolls)} dice rolls`} />
        <Stat label="Other emojis" value={int(t.unicodeEmoji)} sub="phone keyboard emoji" />
        <Stat label="Average dice roll" value={t.diceAverage ?? "–"} sub="3.5 if the dice are fair" />
      </div>

      <div className="grid">
        <div className="card">
          <h2>Messages per day</h2>
          {daily.length > 0 ? <MultiLineChart series={daily} format={int} label="messages per day, busiest rooms" />
            : <p className="muted small">No messages in this range.</p>}
          <LineChart height={120} points={d.daily.map((x) => ({ x: x.day, y: x.total }))} format={int} label="all rooms together" />
        </div>

        <div className="card">
          <h2>Busiest rooms</h2>
          <SortTable initial={{ key: "messages", dir: -1 }} rows={d.rooms} columns={[
            { key: "room", label: "room", render: (r) => (r.server ? <><ServerLink id={r.server} /> <a className="small" href={`#/chat/${r.room}`}>chat →</a></> : <a href={`#/chat/${r.room}`}>{r.room}</a>) },
            { key: "messages", label: "messages", num: true, render: (r) => int(r.messages) },
            { key: "perDay", label: "per day held", num: true, render: (r) => int(r.perDay) },
            { key: "senders", label: "people", num: true, render: (r) => int(r.senders) },
            { key: "daysHeld", label: "days held", num: true, render: (r) => `${r.daysHeld} (${r.first} – ${r.last})` },
          ]} />
        </div>

        <div className="card">
          <h2>When chat is busiest (UTC)</h2>
          <BarList limit={24} format={int} rows={d.hours.map((n, h) => ({ key: h, label: `${String(h).padStart(2, "0")}:00`, value: n }))} />
        </div>

        <div className="card">
          <h2>Chattiest people</h2>
          <SortTable initial={{ key: "messages", dir: -1 }} rows={d.talkers} limit={50} columns={[
            { key: "name", label: "player", render: (p) => <PlayerLink uid={p.uid} name={p.name} /> },
            { key: "tag", label: "alliance", render: (p) => (p.tag ? <span className="tag">{p.tag}</span> : "") },
            { key: "server", label: "room", render: (p) => (p.server ? <ServerLink id={p.server} /> : p.room) },
            { key: "messages", label: "messages", num: true, render: (p) => int(p.messages) },
            { key: "emoji", label: "emojis + stickers", num: true, render: (p) => int(p.emoji) },
          ]} />
        </div>

        <div className="card">
          <h2>Chattiest alliances</h2>
          <BarList limit={25} format={int} rows={d.alliances.map((a) => ({ key: `${a.tag}${a.server}`, label: `${a.tag}${a.server ? ` #${a.server}` : ""}`, value: a.messages }))} />
        </div>

        <div className="card">
          <h2>Biggest emoji users</h2>
          <SortTable initial={{ key: "emoji", dir: -1 }} rows={d.emojiUsers} limit={30} columns={[
            { key: "name", label: "player", render: (p) => <PlayerLink uid={p.uid} name={p.name} /> },
            { key: "tag", label: "alliance", render: (p) => (p.tag ? <span className="tag">{p.tag}</span> : "") },
            { key: "emoji", label: "total", num: true, render: (p) => int(p.emoji) },
            { key: "gameEmoji", label: "game emojis", num: true, render: (p) => int(p.gameEmoji) },
            { key: "stickers", label: "stickers", num: true, render: (p) => int(p.stickers) },
            { key: "unicodeEmoji", label: "other", num: true, render: (p) => int(p.unicodeEmoji) },
            { key: "share", label: "per message", num: true, sort: (p) => p.emoji / p.messages, render: (p) => (p.emoji / p.messages).toFixed(2) },
          ]} />
        </div>

        <div className="card">
          <h2>Most used emojis <span className="muted small">{int(emojiTotal)} in total</span></h2>
          <EmojiGrid rows={d.gameEmoji} />
          {d.unicodeEmoji.length > 0 && <><h3 className="small muted">Other emojis</h3><EmojiGrid rows={d.unicodeEmoji} /></>}
        </div>

        <div className="card">
          <h2>Animated stickers</h2>
          <BarList limit={25} format={int} rows={d.stickers.map((s) => ({ key: s.key, label: stickerNames[s.key] || `sticker ${s.key}`, value: s.count }))} />
          {t.diceRolls > 0 && <>
            <h3 className="small muted">Dice rolls ({int(t.diceRolls)})</h3>
            <div className="dice-row">
              {d.dice.map((n, i) => (
                <div key={i} className="dice-col" title={`${int(n)} × ${i + 1}`}>
                  <div className="dice-bar" style={{ height: `${(n / diceMax) * 80}px` }} />
                  <div className="small">{i + 1}</div><div className="small muted">{int(n)}</div>
                </div>
              ))}
            </div>
          </>}
        </div>
      </div>
    </div>
  );
}

function EmojiGrid({ rows }) {
  if (!rows.length) return <p className="muted small">None in this range.</p>;
  return (
    <div className="emoji-grid">
      {rows.map((e) => (
        <div key={e.key} className="emoji-cell" title={[...e.key].map((ch) => `U+${ch.codePointAt(0).toString(16).toUpperCase()}`).join(" ")}>
          <span className="emoji-glyph">{e.key}</span>
          <span className="small muted">{int(e.count)}</span>
        </div>
      ))}
    </div>
  );
}
