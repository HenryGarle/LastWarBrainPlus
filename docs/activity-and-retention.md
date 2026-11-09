# Activity ("seen", "active") and data retention

## Where "was this player in the game" comes from

- `player_seen (uid, seen_at, power, army_power, army_kill, level)`, PK (uid, seen_at). `seen_at` = the profile's
  `lastUpdateTime`. Written by the hourly pulse (`scan.profiles changes=true`) only when last-seen moved forward, and by
  every full sweep. `profile_reading (taken_at, source, players)` notes each read.
- `player_profile` holds full sweep snapshots (and `capitol` rows from the capitol sweep). Do NOT read last-seen history
  from player_profile - read player_seen.
- `player_activity` (derived, rebuilt hourly and after scans by `PlayerActivity.Rebuild`):
  - last_seen_at - newest real login (see filters below)
  - last_donated_at, last_chat_at
  - last_active_at = greatest of the three; "active" = inside 7 days (WindowDays)
  - last_rise_at - newest power rise (display only: queued builds finish while away)
  - last_updated_at - newest lastUpdateTime of ANY kind (display only, "updated X ago" on the player page)

## What does NOT count as a login (`PlayerActivity.NotALogin`)

1. mass_touch - a minute with 100+ last-seen moments, 85%+ of them accounts under 20M power (game-side batch).
2. first_look - the first value we ever saw for an account.
3. idle_seen (2026-11-03, Henry) - a moment whose power, kills and HQ level equal the player's reading 7 days before it
   (or their first reading if we have none that old).

Why idle_seen: lastUpdateTime moves when the game changes an account - its base attacked or moved, rewards landing -
mostly on Saturdays (war day). Example: MillyNilly (1075198930001967, #1967): SVIP 15 expired March 2026, no alliance,
power exactly 89,241,970 on 20 readings over 5 weeks, yet 7 "logins". 24,664 HQ25+ accounts with no power change all
had October moments, half on Saturdays with a 14:00-15:00 UTC spike.

Mechanics: `PlayerActivity.JudgeIdle(rows)` SQL; `ProfileStore.InsertSeen` judges new moments as they are inserted
(CTE). Old rows were judged once by `JudgeBacklog` at startup, 64 buckets by `hashtext(uid) & 63`, each recorded in
`schema_fix` as `idle_seen_<k>` (~1 min per bucket on hosted). `idle_seen` columns are `player`/`at` on purpose so an
unqualified `uid`/`seen_at` in a reader binds to the outer row. Rebuild writes the filters as AND-ed NOTs so the idle
check plans as an anti-join.

Effect on hosted (2026-11-03, 64 tracked servers, active in 7 days): all players 194,490 -> 100,537; HQ25+
90,693 -> 77,393; HQ30+ 54,526 -> 52,183. 1.19M moments marked idle. Test: tests/LastWar.Tests/IdleSeenTests.cs.

Known edge: someone who logs in but builds/trains/fights nothing for a week now counts as inactive (Henry's rule).

## Retention (2026-10-09, Henry chose "daily 14 days, then weekly")

- Growth had been ~1.5 GB/day. DB went 17 GB -> 8.7 GB after thinning.
- Pulse stores only login moments (player_seen); sweeps store full rows.
- `data.thin` (job data-thin, daily): sweep profiles, power_sample (Profile + AllianceRoster per source/server) and
  alliance_member_sample keep one snapshot per day for 14 days, one per ISO week before; biggest snapshot per bucket
  wins; today is untouched. Rows with source other than "sweep" (e.g. capitol) are never thinned. `compact=true`
  runs VACUUM FULL (locks tables).
- Henry approved this thinning explicitly; otherwise collected data is never deleted - bad data is moved aside
  (e.g. `map_scan_partial` tables).
