# LastWar

C# tooling for the Last War protocol: collect map, roster and power data, store it, and
work out who is actually active.

.NET 10. Build with `dotnet build -c Release -o ..\bin` from this folder; the CLI is
`lastwar.exe`.

## Projects

| Project | Holds | Depends on |
|---|---|---|
| `LastWar.Core` | Domain records, `ActivityAnalyzer`. No IO, no packages. | nothing |
| `LastWar.Protocol` | SFS2X codec, frame layer, generated protobuf classes. | Core |
| `LastWar.Client` | Login, connection, `MapScanner`, `PowerCollector`. | Core, Protocol |
| `LastWar.Storage` | SQLite, CSV, importers for the old Go/Python outputs. | Core |
| `LastWar.Cli` | Command-line front end. | all |
| `LastWar.Tests` | xunit. | all |

No project references the CLI, so a web front end or a headless worker can reference
`LastWar.Client` and `LastWar.Storage` directly and reuse everything.

## Commands

    lastwar init                                     create/upgrade the SQLite schema
    lastwar scan-map    --servers 1957-1988          sweep maps into the database
    lastwar scan-power  --server 1967                map + power snapshot for one server
    lastwar activity    --server 1967 --out a.csv    compare the two newest snapshots
    lastwar snapshots   --server 1967                list snapshot timestamps
    lastwar call        --cmd <name> --json {...}    issue one raw command
    lastwar import-map   --path <dir>                load legacy map_*.csv
    lastwar import-power --server <id> --rosters <log> --detail <csv>

`--db` overrides the database path, `--config` the session file.

In PowerShell, pass JSON after the `--%` stop-parsing token, otherwise quotes are eaten:

    lastwar --% call --cmd season.migrate.server.info --json {"serverId":1967}

## Rules that are not obvious

**One session per account.** A second login silently evicts the first. The evicted
process keeps its socket open and simply times out every request, so it looks like a
hang rather than an error. `MapScanner` gives up after 8 consecutive timeouts and says
so; do not run two commands against the same account at once.

**iosMode must match how the account was issued.** An iOS account logging in with
Android fields fails `ec=28 / ep=[E011]`, and cross-server with `ep=[E005]`. The "ta"
analytics blob must also be populated for iOS - sending `{}` fails the same way.

**Activity means a RISE in power.** Power only goes up when a human logs in and acts.
A fall proves nothing (troops die, buildings get demolished, dormant accounts get
farmed) and is never read as dormancy. Each snapshot pair is judged on its own, never
against an all-time peak.

**Cross-server map view is level-gated.** Low-level accounts get
`errorCode=cross_server_view_tips`. Guests can only scan their own server.
