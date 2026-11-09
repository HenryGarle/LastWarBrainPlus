# Explore pages built Oct-Nov 2026

## Explore > Players (`web/src/explorer/PlayersPage.jsx`, `Insights.PlayerDirectory.cs`, `/api/x/players`)

- Everyone in player_latest (one row per uid, newest profile's numbers) joined to player_activity.
- Paged server-side: 100 per page, drop-down 100/250/500, first/prev/next/last. All filters in the URL hash.
- Filters: name/uid, servers (1967, 1957-1988, lists), alliance tag (or "none"), created in (country), displays as
  (flag), "unknown" for either, HQ min/max, hero min, power min, last seen (1d/3d/7d/30d/not7d/not30d/never on
  last_active_at), moved (transferred / native / visiting).
- Sorts are a whitelist (never a column name from the request).
- With no servers named, only TRACKED servers (server_track level 'full') are listed - players glimpsed from far-off
  servers (#600s-#900s via capitol sweep / probes) are left out unless their server is named (Henry 2026-11-02).
- player_latest gained level, country, flag, current_server, svip (filled from the newest profile).

## Countries: "created in" vs "displays as" (Henry 2026-10)

- profile `country` = the country the account was created in; profile `flag` = the flag the player shows.
- Server page Players tab: two filters, two columns ("same" when equal), CSV has both, top tabs for the % breakdown
  (first tab created in, second displays as).
- Every country drop-down lists ALL countries we know (`/api/x/countries` = PlayerFacets, cached 10 min), sorted by
  name, flag emoji first (`CountryOptions` in `explorer/Online.jsx`); on the server page the counts shown are that
  server's. The select needs `className="flag-select"` - Windows has no flag glyphs, the font stack falls through to
  the Noto Color Emoji web font.

## HQ levels (`explorer/HqLevels.jsx`, `Insights.HqLevels.cs`, `/api/x/hq`)

- Counts per home server and HQ level (latest sweep, tracked servers), "active HQ 30+" per server (last_active_at in
  3 days), firsts per level, and everyone at >= min level (31-40).
- Reached dates: exact from board type 5 extra `level=..;updateTime=ms`, else bracketed between readings (one-pass
  MIN/MAX per level; the first version took 23 s).

## Capitol War logging

A profile sweep run with source 'capitol' (a job added on hosted, not seeded in code) logs everyone currently in other
servers during Capitol War; those rows are never thinned. Offered but not built: an hourly "away" log.

## Player page

Shows "last in game" (real login or donation) and, when different, "updated X ago" (raw lastUpdateTime, not proof of play).
