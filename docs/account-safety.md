# Accounts and safety

Henry's words are the rules. Everything here is enforced in code where possible; when in doubt, use a guest or ask.

## Accounts

| Account | uid | Status |
|---|---|---|
| Ghost | 1001690072001967 | Henry's personal account. "Never use my personal Ghost account - ever." Hard-blocked in `LastWar.Client.BlockedAccounts`. |
| Sadmin | 2359053554001967 | Banned 2026-10-02. Blocked in code. |
| lastwar_1 (lw_1) | service | Banned 2026-10-08 ("data error"). |
| lw_2 | service | Banned ~2026-11-02. Henry cannot easily get another service account. |
| Guests | throwaway | Created per operation (`WithGuestAsync`). Free to probe anything. |

Tiers (`Operations.cs`): Guest, Service, Privileged. An op's `accounts` decides who may run it; `needsSession: false`
ops make their own guest.

## Rules

- Guests for anything new: "Give whatever you'd like a try on guest accounts but don't use real accounts."
- Real accounts: one read-only command at a time, never touch saved credentials.
- Service accounts: "be much more careful", "be careful not to make invalid requests". Only request shapes the real
  client sends; prove a new shape on a guest first (and take the shape from the client code, not a guess).
- Never send coordinates outside the target server's world (1000 x 1000 for other servers).
- Never ask for the contents of an unfinished secret task, or a point that is not a task in the latest scan.
- Messages: test messages only to our own accounts; DMs from a service account; inbox handling is manual.
- Chat (clarified with Henry 2026-11-09): an alliance's MAIN chat is an open-join group channel - any player can
  join the alliance and read it, and the game gates it by membership (see the chat-gateway test in
  game-client-research.md: the chat server returns an empty result to a non-member, so reading a room needs a real
  member account on that server). Reading an alliance's main chat through an account that is a member of it is fine
  (that is the Inbox chat-link path). DMs and officer/R4-only channels are the private ones - do NOT go after those.
  AGENTS.md line 154 (custom_* rooms not stored with public chat) is about STORAGE of DM rooms, not a ban on reading.
- Paid/account-changing APIs: tell Henry, do not execute.

## Why lw_1 was banned (2026-10-08)

For ~24 h every map scan of another server used the client's 3000-wide own-server layout: coordinates and block
indices up to 2999 on 1000-wide worlds - thousands of out-of-range requests. Fixed: own server = 3000-wide layout,
any other server = 1000-wide rows (MapScanner).

## Why lw_2 was banned (2026-11-02)

1. map-refresh: 64 servers x ~80 world.get.block at ~4 req/s. Oct 28 - Nov 1 the game closed the connection every day
   after ~30 servers / ~10 min (it was cutting us off); Nov 2 a full 64-server run completed; ban the same day.
   No person views 64 servers' maps in 20 minutes.
2. secret.task.rewards (world.get.detail.new pointType 17, never proven on a guest): 20 requests on Oct 30, five for
   unfinished tasks (the client never allows that) and one for a point that was no longer a task.
3. secret.tasks scans with a 70 ms gap (~14 requests/s).

## Pacing now (2026-11-02)

| Op | Setting |
|---|---|
| scan.map / refresh.map | gap 1000 ms jittered, `maxServers` 10 per run (longest-unscanned first); an IOException (game closed the connection) stops the run at once and marks the rest "not tried". |
| map-refresh job | disabled on hosted. |
| secret.task.rewards | Guest tier only; Scans page shows "guest only" for real accounts. |
| secret.tasks | gap 250 ms. MapScanOptions default gap 250 ms. |
| scan.power | gap 1000 ms (was 60). |
| servers.season | own guest, 3 s jittered. |

## What guests can and cannot do (2026-11-07)

- A default guest (`opt new`, no zone) lands wherever the gateway puts it - seen #647, #74, #1276, #2078, #198, #457,
  #933, #9, #1738, #70, #1025. Once (early October) #1959. Random placement into 1957-2020 is rare.
- On its OWN server a guest can read: get.season.infos (season list with cfg ids), server.badges.get (war-zone badge
  servers), lw.season.trend.info (season_group), get.zone.intelligence, get.player.cross.server.list.
- A guest cannot be created on a chosen server: gateway `zone=APSxxxx` + login with a blank uid -> `E002` every time,
  even on a server that accepts default guests. See game-client-research.md.
- Guests re-logging in fail (E005/E011) - guests are one-login.
- `probe.run` (set = data/probes/<set>.json, optional zone) runs a list of commands on a fresh guest and saves raw
  replies to data/raw/probe_<set>_<time>.json.
