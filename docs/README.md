# Docs index (start here)

Last War Brain: a C# (.NET 10) + React site that reads the mobile game Last War over its own socket protocol and
turns it into stats and intel. Local copy runs on Henry's PC; the hosted copy is https://lastwarbrain.dev.

Read in this order:

1. [../AGENTS.md](../AGENTS.md) - Henry's standing rules (top of the file) and the dated running log of findings.
2. [working-on-this-project.md](working-on-this-project.md) - paths, build, test, restart, deploy, hosted SQL, tool gotchas.
3. [account-safety.md](account-safety.md) - which accounts may do what, the two service-account bans and the pacing rules they produced.
4. Topic notes, as needed:
   - [activity-and-retention.md](activity-and-retention.md) - what "seen" / "active" means, player_seen, idle_seen, data thinning.
   - [season-groups.md](season-groups.md) - season configs, reading every server's season group, decoding the game's season table.
   - [svs.md](svs.md) - Warzone Duel bracket and the forecast model.
   - [secret-tasks.md](secret-tasks.md) - Scans > Secret Tasks: rank, rewards, the map dialog, the rules.
   - [explore-pages.md](explore-pages.md) - Explore > Players, server Players tab (countries), HQ levels.
   - [game-client-research.md](game-client-research.md) - where the decompiled client lives, how to search it, the login / new-character flow.
   - [hosting.md](hosting.md) - the VPS, disk, backups, the co-hosted riftfall app.
5. Older deep research: [protocol-notes.md](protocol-notes.md) (long log), [setup-notes.md](setup-notes.md).

## Pick up here (state as of 2026-11-09)

- Season 3 for our block (1957-2020) starts 2026-11-23. Servers still report their S2 config (1319-1326) and flip to
  the S3 config (1497-1504) at season START, not pre-season. The daily `season-groups` job will read the full S3 groups
  once they flip - on/after the 23rd, check Explore > Servers > Season groups, or `/api/x/season-groups?lo=1957&hi=2020`.
  The game re-drew S3 in table 40210 (2026-11-09); 1500 = 1962, 1963, 1965, 1967, 1969, 1975, 1976, 1984 from Henry's
  in-game display, entered by hand (server_season_hand). Other groups: 2 servers each from the decode - see season-groups.md.
- Everything built this run is deployed (hosted `live:` tags in deploy.log). Recent work: idle_seen activity fix,
  service-account pacing after the lw_2 ban, Explore > Players (country flags, tracked-servers default), secret-task
  reward checks (guest-only), season-groups read + page + job. All in AGENTS.md's dated log and the topic notes here.
- The docs below (this folder) were written 2026-11-07..09 as the handoff; AGENTS.md is the running log. The large files
  protocol-notes.md / setup-notes.md predate them.
- Open loops noted in the topic notes: SvS boss-1st on #1981 v #1970 (unanswered); a reward "value score" (not built);
  an hourly capitol "away" log (offered, not built).
- Nothing in docs/ is committed to git yet - ask Henry before committing.

Conventions for new notes: plain English, dated where it matters, say who decided ("Henry 2026-11-03"), never paste
credentials, tokens or anything from C:\Temp\LWC\auth-backup.
