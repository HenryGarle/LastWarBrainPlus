# Makes what the server needs, in C:\Temp\LWC\deploy-out:
#   lastwar-src.tgz    the code (the server builds the image from it)
#   lastwar-data.tgz   reference files the app reads: game text (assets), command list, migration CSVs
#   lastwar.dump       the database (only with -Dump) - pause the local scheduler first (README.md)
#
# Never packed: data\runners and data\sessions (the real accounts' sessions), .env, the old SQLite
# file. The hosted copy works with guests only.
#
#   powershell -File deploy\pack.ps1            code + data files
#   powershell -File deploy\pack.ps1 -Dump      ...and the database
param([switch]$Dump, [string]$Out = "C:\Temp\LWC\deploy-out", [string]$Root = "C:\Temp\LWC")
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null

tar -czf "$Out\lastwar-src.tgz" -C $Root `
    --exclude=.git --exclude=node_modules --exclude=bin --exclude=obj --exclude=.env --exclude=wwwroot `
    --exclude=deploy/data --exclude=deploy/backups LastWar
Write-Host "code:  $Out\lastwar-src.tgz"

$files = @("data/assets", "data/api/commands.csv", "data/migration", "data/chat/follow.json") |
    Where-Object { Test-Path (Join-Path $Root $_) }
tar -czf "$Out\lastwar-data.tgz" -C $Root @files
Write-Host "data:  $Out\lastwar-data.tgz"

if ($Dump) {
    docker exec lastwar-postgres sh -c "pg_dump -U lastwar -d lastwar -Fc -f /tmp/lastwar.dump"
    docker cp lastwar-postgres:/tmp/lastwar.dump "$Out\lastwar.dump"
    docker exec lastwar-postgres rm /tmp/lastwar.dump
    Write-Host "db:    $Out\lastwar.dump"
}
Get-ChildItem $Out | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
