import { allianceColour, big, href, int, when } from "./format.js";
import { useFavourites, isFav } from "./favs.jsx";

// Signed change with a glyph, so up/down never rests on colour alone.
export function Delta({ value }) {
  if (value === null || value === undefined) return <span className="muted">–</span>;
  const v = Number(value);
  if (v === 0) return <span className="muted">0</span>;
  return (
    <span className={v > 0 ? "up" : "down"}>
      {v > 0 ? "▲ +" : "▼ −"}
      {big(Math.abs(v))}
    </span>
  );
}

// The activity rule, made visible. Only a rise proves a login; "down" proves nothing.
export function Status({ value }) {
  const map = {
    active: ["▲", "active", "status-good"],   // a real sign of play in the last 7 days
    quiet: ["■", "quiet 7d+", "status-muted"],
    unknown: ["?", "no data", "status-muted"],
  };
  const [icon, label, cls] = map[value] || map.unknown;
  return (
    <span className={`status ${cls}`} title={value === "active" ? "Logged in, donated or chatted in the last 7 days"
      : value === "quiet" ? "No login, donation or chat in the last 7 days (power rising alone proves nothing - builds finish while away)" : undefined}>
      {icon} {label}
    </span>
  );
}

// active7: the server's verdict over the last 7 days (a rise at ANY point, not just the last pair).
// Active = a login, donation or chat in the last 7 days (PlayerActivity). Power movements are shown
// elsewhere but never make a player "active": queued builds finish and troops die while they are away.
export const statusOf = (now, prev, active7) =>
  active7 === null || active7 === undefined ? "unknown" : Number(active7) === 1 ? "active" : "quiet";

export function Stat({ label, value, sub, onClick }) {
  return (
    <div className={onClick ? "stat link" : "stat"} onClick={onClick} title={onClick ? "Show these players" : undefined}>
      <div className="stat-label">{label}</div>
      <div className="stat-value">{value}</div>
      {sub && <div className="stat-sub">{sub}</div>}
    </div>
  );
}

// Every alliance tag carries its colour stripe, so the same alliance reads the same everywhere.
// A small star on any favourite player, alliance or server link; a table row whose player (or whose
// first-column subject) is a favourite is shaded - styles.css, "tr:has(.fav-mark...)".
function FavMark({ kind, id }) {
  const favs = useFavourites();
  return id && isFav(favs, kind, id) ? <span className={`fav-mark ${kind}`} title="favourite">★</span> : null;
}

export const Tag = ({ id, tag }) =>
  !tag ? <span className="muted">none</span>
  : id ? <span className="fav-wrap"><FavMark kind="alliance" id={id} /><a className="tag" style={{ "--al": allianceColour(id) }} href={href.alliance(id)}>{tag}</a></span>
  : <span className="tag">{tag}</span>;

export const ServerLink = ({ id }) =>
  id ? <span className="fav-wrap"><FavMark kind="server" id={id} /><a className="mono" href={href.server(id)}>#{id}</a></span> : <span className="muted">–</span>;

export const PlayerLink = ({ uid, name }) => <><FavMark kind="player" id={uid} /><a href={href.player(uid)}>{name || uid}</a></>;

// Hero power: the in-game board value when they are on it, else the estimate (marked).
export const Hero = ({ board, est }) =>
  board != null ? <span title="from the in-game hero board">{big(board)}</span>
  : est != null ? <span title="estimated from their profile">{big(est)}<span className="muted small"> est</span></span>
  : <span className="muted">–</span>;

export { int };

// A logged rename: the old value, the new one, and when we first saw the new one. "Last seen
// as" brackets the moment - the change happened somewhere between the two dates.
export function NameChanges({ rows, showWho = false }) {
  if (!rows?.length) return <p className="muted small">No name changes seen yet.</p>;
  return (
    <table className="small">
      <thead>
        <tr>
          <th>detected</th>{showWho && <th>who</th>}<th>was</th><th>now</th><th>last seen as was</th><th>seen in</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <tr key={`${r.kind}${r.id}${r.field}${r.detected}`}>
            <td className="mono">{when(r.detected)}</td>
            {showWho && (
              <td>{r.kind === "player" ? <a href={href.player(r.id)}>player</a> : <a href={href.alliance(r.id)}>alliance {r.field}</a>}</td>
            )}
            <td className="muted">{r.old}</td>
            <td>{r.kind === "player" && showWho ? <a href={href.player(r.id)}>{r.new}</a> : r.new}
              {!showWho && r.field !== "name" && <span className="muted"> ({r.field})</span>}</td>
            <td className="muted mono">{when(r.lastSeenOld)}</td>
            <td className="muted">{r.source}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
