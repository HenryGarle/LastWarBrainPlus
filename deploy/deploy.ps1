# One command to put the current code live on the server:
#   powershell -File deploy\deploy.ps1
#
# Server layout (created by the first run):
#   /opt/lastwar/.env        settings (DOMAIN, POSTGRES_PASSWORD, SCHEDULER) - yours, never overwritten
#   /opt/lastwar/data/       the app's files        /opt/lastwar/backups/   nightly dumps
#   /opt/lastwar/LastWar/    the code now live      /opt/lastwar/LastWar.prev/  the one before (-Rollback)
# The code folders only hold links to .env, data and backups, so a deploy never moves them, and the
# database lives in a Docker volume. The new image is built while the old version keeps serving; a
# failed build leaves it running.
#
# The server comes from -Server, else deploy\server.txt (one line, e.g. root@203.0.113.7). Uses
# Windows' built-in ssh/scp with your SSH key.
#
#   -WithData   also send the reference files (assets, command list, migration CSVs) - first time, or when they change
#   -Logs       follow the app's log afterwards (Ctrl+C stops watching; the app keeps running)
#   -Rollback   go back to the version before the last deploy
#   -MigrateDatabase  copy the WHOLE local database up, replacing the server's: pauses the local
#               hourly pulse (so the copy is complete and only one place collects), dumps, uploads,
#               restores, restarts the app. Run it once, after the first deploy.
#   -PullDatabase     the other way: copy the server's WHOLE database DOWN, replacing the local one.
#               Tables with more rows locally than on the server (local chat, a local map scan) get
#               their local-only rows put back afterwards; rows that clash with a server row on a key
#               are reported, not guessed at. Always saves the local database to C:\Temp\LWC\backups
#               first. Stops and restarts the local server, and leaves every local scheduled job off
#               so only the server collects.
#   -Force      deploy even while a scheduled job is running on the server (the restart cuts it off)
param([string]$Server, [switch]$WithData, [switch]$Logs, [switch]$Rollback, [switch]$MigrateDatabase, [switch]$PullDatabase, [switch]$Force)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
if (-not $Server) {
    $file = Join-Path $here "server.txt"
    if (-not (Test-Path $file)) { throw "Say where to deploy: -Server root@<ip>, or put that in deploy\server.txt" }
    $Server = (Get-Content $file -TotalCount 1).Trim()
}
$out = "C:\Temp\LWC\deploy-out"
# Give up on a dead connection instead of hanging (a dropped Wi-Fi once left a deploy waiting forever).
$SshOpts = @("-o", "ConnectTimeout=20", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4")
# Runs a bash script on the server. It goes up as a file: Windows PowerShell mangles quotes in a
# script passed as an ssh argument.
function Remote([string]$script) {
    $tmp = Join-Path $env:TEMP "lastwar-step.sh"
    [IO.File]::WriteAllText($tmp, ($script -replace "`r", ""))
    scp -q @SshOpts $tmp "${Server}:/tmp/lastwar-step.sh"
    if ($LASTEXITCODE) { throw "could not reach $Server" }
    ssh @SshOpts $Server "bash /tmp/lastwar-step.sh; rc=`$?; rm -f /tmp/lastwar-step.sh; exit `$rc"
    if ($LASTEXITCODE) { throw "stopped - see above (the running version is unchanged)" }
}

# A deploy restarts the app, and a restart kills the job that is running (2026-09-30: stats sweeps cut off
# part-way left half-finished snapshots). So refuse while one runs, unless -Force.
if (-not $Force -and -not $PullDatabase) {
    $busy = Remote 'docker exec -i lastwar-db-1 psql -U lastwar -d lastwar -tA -c "SELECT job_id || '' since '' || started_at FROM lastwar.schedule_run WHERE state = ''running'';"'
    if ("$busy".Trim()) { throw "A job is running on the server and a deploy would cut it off: $busy. Wait for it to finish, or pass -Force." }
}

if ($Rollback) {
    Remote @'
set -e
cd /opt/lastwar
[ -d LastWar.prev ] || { echo "no previous version to go back to"; exit 1; }
mv LastWar LastWar.rolledback && mv LastWar.prev LastWar && mv LastWar.rolledback LastWar.prev
cd LastWar/deploy && docker compose build app && docker compose up -d --force-recreate app caddy
echo "live: $(cat ../VERSION 2>/dev/null || echo previous)"
'@
    exit 0
}

if ($MigrateDatabase) {
    $local = "http://localhost:5183"
    $wasOn = $true   # the hourly pulse's state BEFORE pausing it here - the server copy gets that, not "paused"
    try {
        $wasOn = [bool](((Invoke-RestMethod "$local/api/schedule").jobs | Where-Object { $_.id -eq "activity-pulse" }).enabled)
        Invoke-RestMethod -Method Post "$local/api/schedule/activity-pulse/pause" | Out-Null
        Write-Host "local hourly pulse paused"
        while ((Invoke-RestMethod "$local/api/schedule").jobs | Where-Object { $_.running }) {
            Write-Host "waiting for the running local job to finish ..."; Start-Sleep 20
        }
    } catch { Write-Warning "local server not reachable - make sure nothing is collecting locally" }
    & (Join-Path $here "pack.ps1") -Out $out -Dump | Out-Null
    Write-Host "uploading the database ($([math]::Round((Get-Item "$out\lastwar.dump").Length / 1MB)) MB) ..."
    scp -q @SshOpts "$out\lastwar.dump" "${Server}:/opt/lastwar/lastwar.dump"
    if ($LASTEXITCODE) { throw "upload failed" }
    Remote (@'
set -e
cd /opt/lastwar/LastWar/deploy
docker compose stop app
# A fresh, empty database (same byte-order sorting) and the local copy restored into it.
docker compose exec -T db psql -U lastwar -d postgres -q -c "DROP DATABASE IF EXISTS lastwar WITH (FORCE);" -c "CREATE DATABASE lastwar;"
docker compose exec -T db pg_restore -U lastwar -d lastwar --no-owner < /opt/lastwar/lastwar.dump
rm /opt/lastwar/lastwar.dump
docker compose exec -T db psql -U lastwar -d lastwar -q -c "UPDATE lastwar.schedule_job SET enabled = __ON__ WHERE id = 'activity-pulse';"
docker compose start app
sleep 15
docker compose exec -T db psql -U lastwar -d lastwar -tA -c "SET search_path=lastwar; SELECT 'profiles restored: ' || COUNT(*) FROM player_profile;"
docker compose logs app | grep -E "FIRST ADMIN|NEW ADMIN" | tail -1 || true
'@).Replace("__ON__", [string][int]$wasOn)
    Write-Host "done - the local pulse stays paused; the server is the collector once SCHEDULER=on"
    exit 0
}

if ($PullDatabase) {
    $pg = "lastwar-postgres"                  # the local docker container (127.0.0.1:5432)
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $backups = "C:\Temp\LWC\backups"; New-Item -ItemType Directory -Force $backups | Out-Null
    # Exact row counts per table, "table|rows" lines - the same query on both sides.
    $countSql = "SELECT table_name || '|' || (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM lastwar.%I', table_name), false, true, '')))[1]::text FROM information_schema.tables WHERE table_schema = 'lastwar' AND table_type = 'BASE TABLE' ORDER BY 1;"
    $toMap = { param($lines) $m = @{}; foreach ($l in $lines) { $p = "$l".Trim().Split('|'); if ($p.Count -eq 2) { $m[$p[0]] = [long]$p[1] } }; $m }

    Write-Host "counting rows on the server and locally ..."
    $tmp = Join-Path $env:TEMP "lastwar-count.sh"
    [IO.File]::WriteAllText($tmp, "docker exec -i lastwar-db-1 psql -U lastwar -d lastwar -tA <<'SQL'`n$countSql`nSQL`n")
    scp -q @SshOpts $tmp "${Server}:/tmp/lastwar-count.sh"; if ($LASTEXITCODE) { throw "could not reach $Server" }
    $remote = & $toMap (ssh @SshOpts $Server "bash /tmp/lastwar-count.sh; rm -f /tmp/lastwar-count.sh")
    $local = & $toMap ($countSql | docker exec -i $pg psql -U lastwar -d lastwar -tA)
    if ($remote.Count -eq 0) { throw "no row counts from the server - nothing changed" }
    $rows = { param($m, $k) if ($m.ContainsKey($k)) { $m[$k] } else { 0 } }   # Windows PowerShell 5 has no ??
    $ahead = @($local.Keys | Where-Object { $local[$_] -gt (& $rows $remote $_) } | Sort-Object)
    foreach ($t in $ahead) { Write-Host ("  local has more: {0,-28} local {1,12:N0}   server {2,12:N0} - its local-only rows will be put back" -f $t, $local[$t], (& $rows $remote $t)) }

    Write-Host "dumping the server database ..."
    Remote "docker exec lastwar-db-1 pg_dump -U lastwar -d lastwar -Fc -f /tmp/pull.dump && docker cp lastwar-db-1:/tmp/pull.dump /opt/lastwar/pull.dump && docker exec lastwar-db-1 rm /tmp/pull.dump && ls -l /opt/lastwar/pull.dump"
    $dump = Join-Path $backups "server_$stamp.dump"
    scp -q @SshOpts "${Server}:/opt/lastwar/pull.dump" $dump
    if ($LASTEXITCODE) { throw "download failed - the local database is unchanged" }
    Remote "rm -f /opt/lastwar/pull.dump"
    Write-Host "downloaded $([math]::Round((Get-Item $dump).Length / 1MB)) MB"

    Write-Host "saving the local database first ..."
    docker exec $pg pg_dump -U lastwar -d lastwar -Fc -f /tmp/local.dump; if ($LASTEXITCODE) { throw "local backup failed - nothing changed" }
    docker cp "${pg}:/tmp/local.dump" (Join-Path $backups "local_before_pull_$stamp.dump"); docker exec $pg rm /tmp/local.dump
    if ($LASTEXITCODE) { throw "local backup failed - nothing changed" }

    # The local server holds connections and would start collecting from the copy; stop it, restore, restart.
    $srv = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq "LastWar.Server.exe" }
    $cmd = ($srv | Select-Object -First 1).CommandLine
    $srv | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    docker cp $dump "${pg}:/tmp/pull.dump"
    docker exec $pg psql -U lastwar -d postgres -q -c "DROP DATABASE IF EXISTS lastwar WITH (FORCE);" -c "CREATE DATABASE lastwar;"
    docker exec $pg pg_restore -U lastwar -d lastwar --no-owner /tmp/pull.dump
    $rc = $LASTEXITCODE
    docker exec $pg rm /tmp/pull.dump
    # Put back rows only the local copy had: each such table is loaded from the local backup into a
    # side schema, then whatever the server copy lacks (whole-row EXCEPT) is inserted; a key clash
    # (same id, different row - e.g. two different scans both numbered 91) is skipped and counted.
    if ($ahead.Count) {
        docker cp (Join-Path $backups "local_before_pull_$stamp.dump") "${pg}:/tmp/local.dump"
        foreach ($t in $ahead) {
            $sh = "psql -U lastwar -d lastwar -q -c 'DROP SCHEMA IF EXISTS pull_keep CASCADE; CREATE SCHEMA pull_keep; CREATE TABLE pull_keep.$t (LIKE lastwar.$t);' && " +
                  "pg_restore -a -t $t -f - /tmp/local.dump | sed '/^COPY lastwar\.$t /s/^COPY lastwar\.$t /COPY pull_keep.$t /' | psql -U lastwar -d lastwar -q -o /dev/null && " +
                  "psql -U lastwar -d lastwar -tA -c 'WITH missing AS (SELECT * FROM pull_keep.$t EXCEPT SELECT * FROM lastwar.$t), ins AS (INSERT INTO lastwar.$t SELECT * FROM missing ON CONFLICT DO NOTHING RETURNING 1) SELECT (SELECT count(*) FROM missing) || chr(124) || (SELECT count(*) FROM ins);'"
            $res = docker exec $pg sh -c $sh
            $n = "$(@($res)[-1])".Trim().Split('|')   # the counts are the last line
            if ($n.Count -eq 2) {
                Write-Host ("  {0}: put back {1:N0} local-only rows" -f $t, [long]$n[1])
                if ([long]$n[0] -gt [long]$n[1]) { Write-Warning ("{0}: {1:N0} local rows clash with server rows on a key and were NOT put back - they are in {2}" -f $t, ([long]$n[0] - [long]$n[1]), (Join-Path $backups "local_before_pull_$stamp.dump")) }
            } else { Write-Warning "${t}: could not put local rows back ($res) - they are in the local backup" }
        }
        docker exec $pg psql -U lastwar -d lastwar -q -c "DROP SCHEMA IF EXISTS pull_keep CASCADE;"
        docker exec $pg rm /tmp/local.dump
    }
    docker exec $pg psql -U lastwar -d lastwar -q -c "UPDATE lastwar.schedule_job SET enabled = 0;"
    if ($rc) { Write-Warning "pg_restore reported problems (exit $rc) - check above; the old local copy is in $backups" }
    if ($cmd) {
        # Same start as the running one had, detached, logging to data\raw\server.log.
        $exe = ($cmd -replace '^"?([^"]+?\.exe)"?.*$', '$1')
        $cmdArgs = $cmd.Substring($cmd.IndexOf('.exe') + 4).Trim().Trim('"').Trim()
        Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
            CommandLine = "cmd /c `"$exe $cmdArgs >> C:\Temp\LWC\data\raw\server.log 2>&1`""; CurrentDirectory = (Split-Path $exe) } | Out-Null
        $up = $false
        foreach ($i in 1..20) { Start-Sleep 3; try { Invoke-WebRequest "http://localhost:5183/api/auth/me" -UseBasicParsing -TimeoutSec 5 | Out-Null; $up = $true; break } catch { if ($_.Exception.Response) { $up = $true; break } } }
        if ($up) { Write-Host "local server restarted" } else { Write-Warning "local server did not come back - start it again: $cmd" }
    }    Write-Host "done - local now matches the server as of $stamp; local scheduled jobs are off"
    exit 0
}

& (Join-Path $here "pack.ps1") -Out $out | Out-Null
$version = Get-Date -Format "yyyyMMdd_HHmmss"
Remote "mkdir -p /opt/lastwar/data /opt/lastwar/backups"
Write-Host "uploading $version to $Server ..."
scp -q @SshOpts "$out\lastwar-src.tgz" "${Server}:/opt/lastwar/lastwar-src.tgz"
if ($LASTEXITCODE) { throw "upload failed" }
if ($WithData) {
    scp -q @SshOpts "$out\lastwar-data.tgz" "${Server}:/opt/lastwar/lastwar-data.tgz"
    if ($LASTEXITCODE) { throw "upload failed" }
}

Remote (@'
set -e
cd /opt/lastwar
if [ "__DATA__" = "1" ]; then tar xzf lastwar-data.tgz -C /opt/lastwar && rm lastwar-data.tgz; fi
chown -R 1654:1654 data                  # the app runs as user 1654 ("app")
rm -rf LastWar.new && mkdir LastWar.new && tar xzf lastwar-src.tgz -C LastWar.new --strip-components=1
echo "__VERSION__" > LastWar.new/VERSION
if [ ! -f .env ]; then
  tr -d '\r' < LastWar.new/deploy/.env.example > .env && chmod 600 .env   # Windows line endings off
  echo "FIRST RUN: fill in /opt/lastwar/.env (DOMAIN, POSTGRES_PASSWORD), then run deploy.ps1 again"
  exit 1
fi
sed -i 's/\r$//' .env
ln -sfn /opt/lastwar/.env LastWar.new/deploy/.env
ln -sfn /opt/lastwar/data LastWar.new/deploy/data
ln -sfn /opt/lastwar/backups LastWar.new/deploy/backups
cd LastWar.new/deploy
docker compose build app || { echo "BUILD FAILED - the running version is unchanged"; exit 1; }
cd /opt/lastwar
if [ -d LastWar ]; then rm -rf LastWar.prev && mv LastWar LastWar.prev; fi
mv LastWar.new LastWar
# app and caddy are recreated so they pick up the new image and the new Caddyfile.
cd LastWar/deploy && docker compose up -d --force-recreate app caddy && docker compose up -d
docker image prune -f > /dev/null
docker builder prune -f --filter until=72h > /dev/null   # builds run here; without this the cache grew to 30 GB
echo "live: __VERSION__"
'@).Replace("__DATA__", [string][int]$WithData.IsPresent).Replace("__VERSION__", $version)

if ($Logs) { ssh $Server "cd /opt/lastwar/LastWar/deploy && docker compose logs -f --tail 50 app" }
