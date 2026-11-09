// One place that knows how to talk to the API host.
const json = async (res) => {
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(body.error || `${res.status} ${res.statusText}`);
  return body;
};

const get = (url) => fetch(url).then(json);
const post = (url, body) =>
  fetch(url, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  }).then(json);

export const api = {
  runners: () => get("/api/runners"),
  reload: () => post("/api/runners/reload"),
  start: (id) => post(`/api/runners/${id}/start`),
  stop: (id) => post(`/api/runners/${id}/stop`),
  call: (id, command, params, timeoutSeconds, confirm) =>
    post(`/api/runners/${id}/call`, { command, params, timeoutSeconds, confirm }),
  setTier: (id, tier) => fetch(`/api/runners/${id}/tier`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ tier }) }).then(json),
  commands: (q) => get(`/api/commands?q=${encodeURIComponent(q || "")}&limit=300`),
  commandCount: () => get("/api/commands/count"),
  tables: () => get("/api/db/tables"),
  presets: () => get("/api/db/presets"),
  query: (sql, limit) => post("/api/db/query", { sql, limit }),
  chatRooms: () => get("/api/chat/rooms"),
  chatStatus: () => get("/api/chat/status"),
  chatMessages: (room, before) =>
    get(`/api/chat/rooms/${encodeURIComponent(room)}/messages?limit=150${before ? `&before=${before}` : ""}`),
  chatFollow: (room, on) => post("/api/chat/follow", { room, on }),
  chatArchive: (room, on) => (on ? post(`/api/chat/rooms/${encodeURIComponent(room)}/archive`)
    : fetch(`/api/chat/rooms/${encodeURIComponent(room)}/archive`, { method: "DELETE" }).then(json)),
  chatDelete: (room, confirm) => post(`/api/chat/rooms/${encodeURIComponent(room)}/delete?confirm=${encodeURIComponent(confirm)}`),
  newGuest: (name) => post("/api/sessions/guest", { name }),
  chatTranslate: (room, seq) => post(`/api/chat/rooms/${encodeURIComponent(room)}/messages/${seq}/translate?lang=en`),
  mail: (id) => get(`/api/runners/${id}/mail`),
  mailClaim: (id, uid) => post(`/api/runners/${id}/mail/${encodeURIComponent(uid)}/claim`),
  mailRead: (id, uids) => post(`/api/runners/${id}/mail/read`, { uids }),
  chatSearch: (q, room, before) =>
    get(`/api/chat/search?q=${encodeURIComponent(q)}&room=${encodeURIComponent(room || "")}${before ? `&before=${encodeURIComponent(before)}` : ""}`),
  backfillJobs: () => get("/api/chat/backfill"),
  backfillStart: (rooms, until) => post("/api/chat/backfill", { rooms, until }),
  backfillCancel: () => post("/api/chat/backfill/cancel"),
  chatBackfill: (room, pages) => post(`/api/chat/rooms/${encodeURIComponent(room)}/backfill?pages=${pages || 20}`),
  chatStart: (id) => post(`/api/runners/${id}/chat/start`),
  // Favourite players belong to whoever is signed in.
  favourites: () => get("/api/me/favourites"),
  favourite: (uid, on) => fetch(`/api/me/favourites/${encodeURIComponent(uid)}`, { method: on ? "PUT" : "DELETE" }).then(json),
};

export const eventsUrl = (id) => `/api/runners/${id}/events`;
