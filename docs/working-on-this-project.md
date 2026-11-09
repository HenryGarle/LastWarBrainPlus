# Working on this project

## Places

| What | Where |
|---|---|
| Source (git repo) | `C:\Temp\LWC\LastWar` on Henry's PC (Windows) |
| Local site | http://localhost:5183 (published build in `C:\Temp\LWC\bin\server`) |
| Local database | Docker container `lastwar-postgres` (Docker Desktop) |
| Runtime data | `C:\Temp\LWC\data` (runners, sessions, raw replies, game tables, probes) |
| Throwaway code / scratch | `C:\Temp\LWC\data\raw` (never in the repo) |
| Decompiled game client | `C:\Temp\LWC\apk` (see game-client-research.md) |
| Real logins (keep, never commit, never read for a task) | `C:\Temp\LWC\auth-backup` |
| Files waiting for deletion | `C:\Temp\LWC\_to_delete` (deleting needs Henry's approval - move here instead) |
| Hosted | https://lastwarbrain.dev, Hetzner VPS `root@2.29.53.222`, compose project in `/opt/lastwar` |

Agents reach the PC through a remote PowerShell tool. SSH to the VPS works from the PC.

## Build, test, run

- Web: `cd web; npm run build` (outputs into `src/LastWar.Server/wwwroot`). Build the web BEFORE restarting.
- Server: `dotnet build src\LastWar.Server -c Release`.
- Tests: `dotnet test tests\LastWar.Tests -c Release` (93 passing on 2026-11-07). Storage tests need Docker running.
- Restart local (publishes and restarts the server):
  `Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{CommandLine='powershell -ExecutionPolicy Bypass -File C:\Temp\LWC\data\raw\restart.ps1'}`
  then wait ~50 s. The script lives in `C:\Temp\LWC\data\raw`, NOT `LastWar\data\raw`.
- If the site or DB is down, Docker Desktop may have stopped: start
  `"C:\Program Files\Docker\Docker\Docker Desktop.exe"` the same Win32_Process way, wait ~50 s.
- Browser cache: add `?v=<anything>` to the URL after a deploy.

## Deploy (hosted)

- "Do it locally first then push up" (Henry). Never deploy over a running job.
- Run `C:\Temp\LWC\data\raw\deploy_when_idle.ps1` via Win32_Process; it waits (retries every 4-6 min) while a job runs,
  then calls `deploy\deploy.ps1`. Log: `C:\Temp\LWC\LastWar\data\raw\deploy.log` - success line is `live: <tag>`.
- deploy.ps1 prunes images and Docker build cache older than 72 h (`docker builder prune -f --filter until=72h`); the
  build cache once filled the disk (30 GB).
- Never ship `data\runners` or `data\sessions`. Hosted runs guests only; Henry manages hosted sessions.

## Hosted SQL

Write the query to a .sql file, copy it up, run it in the DB container:

```
scp -q C:\Temp\LWC\data\raw\q.sql root@2.29.53.222:/tmp/q.sql
ssh root@2.29.53.222 "docker exec -i lastwar-db-1 psql -U lastwar -d lastwar < /tmp/q.sql"
```

For anything over ~50 s wrap it: `nohup sh -c '... > /tmp/q.out 2>&1' &`, then `cat /tmp/q.out`.
Use psql `\pset format unaligned` + `\pset fieldsep ','` for CSV (python's csv module chokes on psql's header lines -
split lines yourself). Containers: lastwar-app-1, lastwar-db-1, lastwar-caddy-1, lastwar-backup-1. Data: /opt/lastwar/data.
Python 3 exists on the VPS host for throwaway analysis (`python3 -I script.py`); not in the product.

Local SQL: `"..." | docker exec -i lastwar-postgres psql -U lastwar -d lastwar`.

## Tool gotchas (cost real time before)

- The remote PowerShell call times out at 60 s. Anything longer: write a .ps1 into data\raw, start it with
  Win32_Process Create, have it write a log, and poll the log. A timed-out call may still be running - check before rerunning.
- `[IO.File]` with a relative path resolves against the process cwd (C:\Users\hgarle) - always use full paths.
- Normalise CRLF (`.Replace("`r`n","`n")`) before string Replace on source files; check `Contains(old)` and print MISSING.
- In PowerShell single-quoted strings `''` is one `'`; prefer `@' ... '@` here-strings for code.
- Long greps over `C:\Temp\LWC\apk` (18k lua + 3.6k cs files) must run in the background.
- Never kill python processes on Henry's PC (his remote tooling is python).
- Operations run through `POST /api/runners/{id}/operation {operation, args}`. Ops with `needsSession: false` (they use
  their own throwaway guest) can be posted to any runner id - the runner is not logged in for them.

## Scheduled jobs worth knowing (hosted)

stats-refresh (daily, includes rosters), activity-pulse (hourly), data-thin (daily), game-tables (daily),
server-registry / server-basic (daily, guests), season-groups (daily, guest, added 2026-11-07), svs-forecast (daily), own-server (off).
Capitol War logging is a scan.profiles sweep with source 'capitol' (added as a job on hosted, not seeded in code); those rows are never thinned. map-refresh is DISABLED (service accounts banned).
Timed/scheduled actions are jittered (Henry).
