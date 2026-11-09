# AGENTS.md - lastwar-client working notes

Working dir: `C:\Temp\LWC`. Repo: `lastwar-client\` (github.com/ljagiello/lastwar-client).
Portable Go 1.27.1 at `gosdk\go` - not on system PATH. `. C:\Temp\LWC\env.ps1` to get a shell.

## RULES

1. **Blind command probing happens on a GUEST account, never Henry's real account.**
   Use `guestprobe.ps1`, which parks the real identity files, runs a throwaway guest,
   and restores on exit via `finally`. Unknown commands can mutate state.
2. **Never send mutating commands on the real account.** Exclude anything matching
   kick/dismiss/leave/create/apply/accept/refuse/donate/march/declare/war/buy/use/
   delete/read/claim/receive/collect/add/change/set/appoint/resign/choose/elect/send/
   operate/start/join/exchange/upgrade/repair/speed/train/heal/move/cancel/bind/gift/draw.
   `-collect` is mutating: it collects resources, greets visitors and claims rewards.
3. **Back up auth before touching it.** Real identity + session config are in
   `auth-backup\`. Restore = copy back to `C:\Users\hgarle\`.
4. Read-only recon first. Prefer commands that return data over ones that change it.

## LOGIN (established account, no packet capture)

The README claims a tcpdump capture is needed. It is not.

    .\dumpsession.exe                 # writes ~/.lastwar_goclient_session.json from GSL opt=login
    # set "iosMode": true in that file   <- REQUIRED, account is iOS-issued
    .\lastwar-client.exe -list-buildings

- A session config containing `ip` makes `internal/app/main.go:337` take the cross-server
  path automatically, no flags. That path is the one that works for an account with history;
  the plain login path is the guest/new route and is rejected with `ec=28`.
- `ec=28 ep=[E011]` = plain path rejected. `ec=28 ep=[E005]` = cross-server, wrong identity
  (usually iosMode). Exit code 2 = stale session, re-run dumpsession.
- Account: State #1967, gameUid 2359053554001967, character **Sadmin**, HQ 10, **NO ALLIANCE**.

## PROBE SEMANTICS (how to tell a real command from a typo)

| Response | Meaning |
|---|---|
| silence (waits out the timeout) | command not registered |
| `{errorCode=E000000}` | registered, but the call could not be satisfied. **GENERIC - not specifically a param error.** `al.help.all` (documented, confirmed-live, explicitly safe to call with nothing pending) returns E000000 when there is nothing to do. So E000000 usually means "nothing available right now" and only sometimes means wrong params. Do not read it as "keep guessing param names". |
| `{success=true}` | accepted; data may arrive as a separate push, or there may be none |
| real fields | hit |

Set `LW_CMD_TIMEOUT=1500ms` to cut the per-miss wait from the 8s default
(`probeCmdTimeout` in `internal/app/interactive.go`).

## CONFIRMED COMMANDS

| Command | Returns |
|---|---|
| `world.get.alliance.city.info {}` | every alliance on YOUR server: tag, name, 32-hex id, stronghold tiles (base64 protobuf). Server-scoped. |
| `al.rank {"allianceId":"<32hex>"}` | full member roster: uid, name, power, mainCityLv, armyKill, rank, join/donate times. **Works cross-server from any account, incl. a fresh guest.** |
| `get.user.info {"uid":"<uid>"}` | player profile: power, vip, level, score, battleWin/Lose, alivedays + full alliance object |
| `common.chat.room.id {}` | chat room ids for the server |
| `science.data.refresh {}` | alliance tech tree |
| `world.get.block {...}` | registered, param names still unknown (E000000 to x/y, blockX/blockY, blockIndex, pointId, id, blockId) |

Known dead: `al.info`, `al.members`, `al.member.list`, `user.info`, `world.get.new`,
`rank.*` and `leaderboard.*` first pass - all silent.

## DATA GOTCHAS

- **`online` is `true` for all 1,223 players.** Not a presence flag. Do not report it.
- **THP IS exposed** - `rank.get {"type":13}`, field `heroPower`. Distinct from `power`
  (overall) and ranks players differently. Found by guest probing. See the board map below.
- Stronghold record field 1 is NOT a pointId (values too small/clustered). x/y columns in
  `server1967_strongholds.csv` are derived from that wrong assumption - unverified.
- Player uid encodes the server: `1002324127001967` ends in the serverId.

## CHAT - blocked

Chat text is a separate JSON WebSocket, not the SFS socket. `chatToken` (172 chars) and uid
are captured to `~/.lastwar_goclient_chatcreds.json` by `internal/game/chatcreds.go`.
Blocked on `chat_server_links.php` request shape: endpoint is live at
`<gateHost>/gameservice/chat_server_links.php` but answers `{"code":-1,"message":"error params"}`
to ~20 param guesses, and it is NOT the RSA+AES uuid+data envelope the sibling GSL endpoints
use (proved with a control probe against getserverlist.php). Sign formula IS known:
`MD5(MD5("100017"+uid)+timestamp)`. Unknown: request fields, WS URL, where `connectionSign`
comes from. Next best sources: ask the repo author, proxy the phone, or read
`ChatNetManager:InitWebSocketInst` in the APK Lua.

## LOCAL PATCHES (not upstream)

- `cmd/dumpsession/` - writes session config from GSL, no capture needed
- `cmd/chatprobe/` - probes chat_server_links.php
- `internal/game/chatcreds.go` - captures chatToken/uid from init push
- `internal/app/interactive.go` - `-interactive -` reads stdin (Windows has no mkfifo);
  `LW_CMD_TIMEOUT` env
- `git config core.autocrlf false` on this clone, or two gsl tests fail
- Tests failing on Windows: `syscall.Mkfifo` is POSIX-only (interactive/code-pipe test files)

## DATA FILES

`server1967_members.csv` (1223 players, 17 alliances) - `server1967_alliances.csv` -
`server1967_strongholds.csv` (83) - `safa_members.csv` (97 enriched) - `safa_report.html`

## SERVER LEADERBOARDS - `rank.get {"type":N}`

Found by probing type values on a guest. `server.rank {}` also works but returns ONLY the
total-power board and silently ignores every param (type/rankType/id/rankId/serverId).
Types 25-44 all return an identical fallback payload, so **1-24 is the complete set**.

| type | Ranks | Rows | Metric field |
|---|---|---|---|
| 1 | Alliance - army kills | 50 | `armyKill` (array key `allianceRanking`) |
| 2 | Alliance - total power | 50 | `fightpower` |
| 3 | Player - power + kills | 100 | `power`, `armyKill` |
| 4 | Player - TOTAL POWER | 200 | `power` |
| 5 | Player - building power | 100 | `buildingPower` |
| **13** | **Player - TOTAL HERO POWER (THP)** | **200** | **`heroPower`** |
| 14 | Player - PvE max stage | 100 | `pveMaxStage`, `stageScore` |
| 15 | Player - strongest single hero | 100 | `heroHighestPower`, `heroId` |
| 18,19,20 | UNIDENTIFIED - 3 distinct boards, metric NOT returned | 100 | none (`group`=50, `order`=20 are constants) |
| 23,24 | Player power + kills variants | 100 | `power`, `armyKill` |
| 6-12,16,17,21,22 | Empty on this account | 0 | event/season gated |

Player rows carry: uid, name, level, power/heroPower, allianceId, alliancename, abbr,
country, srcServer, crossFightSrcServerId, lastOnlineTime. Array key is `serverRanking`
for player boards, `allianceRanking` for alliance boards.

**No HQ-level board exists.** `server1967_hq_levels.csv` is DERIVED by sorting all 1,223
players on `mainCityLv` from `al.rank` - label it as derived, never as a game board.

Harness: `guestprobe.ps1 -CmdFile <file> -OutLog <log>` (guest, parks/restores real identity),
`runcmds.ps1 -CmdFile <file> -OutLog <log>` (real account, confirmed read-only commands only).
Both set `LW_CMD_TIMEOUT`.

## SNAPSHOT 2026-09-19 (State 1967)

SaFa: #1 of 50 on alliance power board, 97 members, 14.06B total power, 7 strongholds.
64 of the THP top 200 are SaFa; 62 of the power top 200.
Server THP #1: Ghosⱦ 133,023,556 (also SaFa).

## BOARDS 18/19/20 - what is actually known

Do NOT call these "cosmetic" - that was a wrong inference from `chatBubbleId`, which appears on
every board as player decoration metadata, not as the board's metric.

Established:
- `group`=50 and `order`=20 on EVERY row of all three. Constants = display config, not a value.
- Three genuinely distinct boards: membership overlap is 51-76 of 100, and row order differs.
- The ranking value is simply absent from the payload. Only identity is returned; position in
  the `serverRanking` array is the rank.
- **Entry is gated to HQ 30.** All 93 matched board-18 players are mainCityLv 30, against
  687/1223 server-wide. Same on 19 and 20.
- Dominated by the top alliances: SaFa 36-41 of 100, then SaDe, BTW.

Spearman vs every metric we hold (average-rank tie handling - a naive tie-by-index ranking gives
a bogus rho=1.000 on any constant field, which is how hqLevel first looked like a perfect match):
  board 18: pveMaxStage +0.55, heroPower +0.54, armyKill +0.38, power +0.36
  board 19: heroPower +0.68, heroHighestPower +0.57, armyKill +0.48, pveMaxStage +0.48
  board 20: armyKill +0.55, heroPower +0.50, pveMaxStage +0.43
All moderate - none is a restatement of a metric we already have.

To identify them: read the in-game server rankings screen and note the board names and their
order in the tab list; 18/19/20 most likely map to three named boards there.

## SERVER ENUMERATION - solved

`gsl.GetServerList(..., zone, gameUid)` takes a zone the repo never varies. Pass `APS<id>`:

- Zone exists  -> response echoes that exact server (`ID == requested`), `status` empty
- Zone missing -> falls back to whichever server GSL would recommend a new player, `status=0`

So `ID == requested id` is the existence test. `cmd/serverscan` does this with a RANDOM device id -
no account, no game login, nothing to park or restore. `serverscan.exe <lo> <hi> <out.csv>`.

Full sweep 1-2400 (2026-09-19): **2,321 real servers, ids 3..2325**, only two gaps (4 and 7).
`port == 10000 + serverId` for all 2,321. Hosts split across three CDNs:
aws-1.lastwargame.com 800, cf-1.lastwarapp.net 796, gcp-1.lastwargame.com 725.
Files: `servers.csv` (raw probe incl. fallbacks), `servers_real.csv` (clean list).

Still NOT solved: getting an alliance list for an arbitrary server.
`world.get.alliance.city.info` is session-scoped to the server you are logged into, and guest
placement is not controllable. `al.rank` is cross-server but needs an alliance id you already hold.
Untested idea: feed a scanned ip/port/zone to the cross-server path (`-cs-ip/-cs-port/-cs-zone`)
with a guest token - the token is probably bound to the guest's own server, so expect ec=28.

## BOARD METRICS - verified by monotonicity, not assumed

Test: which numeric field runs monotonically down the returned array.
- types **3, 23, 24 are the SAME board** - identical uids, identical order, sorted by `armyKill`
- type 5 carries `buildingPower` but is NOT sorted by it (51 inversions) - real metric unknown
- type 15 sorts by the hero's own `power`; `heroId` names the hero
- types 18/19/20 have NO monotonic numeric field - value is server-side only
Henry's hypothesis for 18/19/20: stages completed / Tank / Aircraft / Missile battlefield.
Docs do carry `HeroType.Tank`, `HeroType.Aircraft`, `HeroType.Missile` - three types, three boards.

## BOARD IDENTITIES - confirmed by Henry (in-game ground truth)

| type | Board | Ordering |
|---|---|---|
| 5 | **HQ level** | Everyone is max HQ, so order = who reached max FIRST. The timestamp is never returned, which is why `buildingPower` looks unsorted. buildingPower is real data, just not the sort key. |
| 14 | **PvE max stage** | `pveMaxStage`, an internal id |
| 18 | **Tank Battlefield** | Many have maxed it - order = first to complete |
| 19 | **Aircraft Battlefield** | first to complete |
| 20 | **Missile Battlefield** | first to complete |

Recurring pattern: **where a board's value is absent or looks unsorted, the real sort key is a
completion timestamp.** For board 5 that timestamp IS returned - `updateTime`, strictly ascending
down the board (rank 1 hit HQ30 on 2025-12-30, rank 100 on 2026-04-14). For 18/19/20 no timestamp
field is returned at all, so those sort keys are genuinely hidden.

PvE stage formula confirmed by Henry across the WHOLE board, not just the top 5.

### PvE stage decoding (type 14)

    in_game_stage = pveMaxStage / 10 - 199896

Confirmed against five known values (495, 491, 491, 490, 490 -> raw 2003910, 2003870 x2, 2003860 x2),
ties included. Equivalently `(raw - 2000000)/10 + 104`; the +104 is unexplained, probably an earlier
stage-id block. All raw values are divisible by 10. Board range on 1967: stages 389..495.
UNVERIFIED at the bottom of the range - prediction to check: rank 100 "D a r k  K i n g" = stage 389.

`stageScore` = raw id + a fractional tiebreak. **Higher fraction = EARLIER completion** - verified
with zero exceptions across every tied pair on the board. Same first-to-finish rule as the
battlefield boards, but here it is actually exposed in the payload.

## FOLDER LAYOUT (since 2026-09-19)

    C:\Temp\LWC\
      AGENTS.md  SETUP-NOTES.md  env.ps1  session.template.json
      data\
        boards\        14 per-board CSVs from rank.get
        server1967\    members, alliances, strongholds, hq levels, all_rankings, cross_board_matrix
        safa\          safa_members, safa_thp, safa_combined, safa.json
        servers\       servers.csv (raw probe), servers_real.csv (2,321 real servers)
        raw\           every captured .log / .b64 / cands*.txt - the source evidence
      reports\         all HTML
      scripts\         all Python
      harness\         runcmds.ps1 (real account), guestprobe.ps1 (guest, parks/restores identity)
      lastwar-client\  repo + built exes    gosdk\ gopath\  auth-backup\  parked\

Write new outputs into `data\<area>\`, never the root. Scripts take `B=r"C:\Temp\LWC"` and build
paths from it.

Two stray duplicates (`boards\` dir, `server1967_members.csv`) could not be deleted - held open by a
background indexer process. Canonical copies are under `data\`; the root ones are stale, ignore them.

## REPORTS

| File | What |
|---|---|
| `all_boards_full.html` | every board, EVERY column verbatim, sortable + filterable |
| `safa_combined.html` | 97 SaFa members x 42 columns, everything merged |
| `all_rankings_1967.html` | curated board view with cross-board matrix |
| `leaderboards_1967.html` | THP-focused summary |
| `safa_report.html` | first SaFa report (power-based, pre-THP) |

`data\safa\safa_combined.csv` merges al.rank roster + get.user.info + all 9 distinct boards.
Board-derived columns are sparse by design: boards are top-100/200 only.

## ALLIANCE GIFTS - in progress

Registered (guest probe): `alliance.reward.list`, `alliance.reward.info`.
Unregistered: al.reward.list, alliance.gift.list, al.gift.list, alliance.reward.detail/log/history,
alliance.gift.log, gift.list, alliance.reward.record, al.gift.record.

`alliance.reward.list {"type":1}` (1=Premium, 2=Regular per docs/alliance-chat-mail.mdx) returns
E000000 on BOTH guest and the real SaFa account, across 14 param shapes (type/rewardType/giftType,
cmdBaseTime, allianceId, page, string-vs-int type).

Most likely explanation is NOT wrong params: **there are probably no unclaimed gifts to list.**
The early `-collect` run on this account claimed all mail and alliance gifts (my error), and
`alliance.reward.allreceive` is what empties the list. E000000 is generic (see probe table above).

To confirm: check in game whether SaFa has unclaimed alliance gifts pending.
 - gifts pending + still E000000 -> params really are wrong, keep looking
 - no gifts pending           -> command is correct, just needs a populated list

**Never send `alliance.reward.allreceive`** during this investigation - it claims, and it is what
destroys the evidence.

### The actual question: is the "hide name" option real anonymisation?
Unanswered - needs one populated response. What to look for in the payload:
 - a uid/name field present alongside a flag like isHide/anonymous/showName -> client-side only
 - no identity field at all when hidden                                     -> real, server-side
Do not assume either; the payload decides.

`harness\oneshot.ps1 -Command '<one command>' [-Email addr]` runs ONE command on a borrowed account:
parks every existing credential file, refuses to start if the park dir is dirty, sends exactly one
command, wipes everything the run created, restores the parked set in `finally`. Written, not yet run.

## SUNDAY EVENT SCAN - 2026-09-20

Re-ran rank.get 1-24 + 25-44 on the real account during a live Sunday boss event.
**No new boards.** Types 6-12, 16, 17, 21, 22 are still empty, byte-for-byte:
`{rankList=[], type=6}`, `{rankArr=[], type=8}`, `{selfRanking=0, serverRanking=[]}` (21),
`{self={rank=-1, idleGameLevel=0, idleGameBoss=0}, serverRanking=[]}` (22).
Types 25-44 still return the identical 28,864-char fallback. Passing `activityId`/`actId`/`id`
alongside `type` changes nothing - the param is ignored.

Probed 20 guesses for an event/boss ranking command on a guest
(activity.*/act.*/boss.*/event.*/rank.activity/rank.boss) - ALL silent, none registered.
That is ~32 failed name guesses across two families now. Stop guessing; the Activity domain has
900 commands and the dispatch table is in the APK.

### NEW CAPABILITY: init dump

`LW_INIT_DUMP=<path>` env var makes the client write the entire decoded init push
(~89 KB, 239 top-level keys) to that file. Added in `internal/game/chatcreds.go`.
This is the single best source of account/server state we have offline.

### NEW: live activity list

The init push carries `activity=[{id, needMainCityLevel, startTime, endTime, endViewTime}, ...]` -
every event currently running, with exact windows. On 2026-09-20 that was 21 entries, six of them
TODAY ONLY: **80005, 94101, 94202, 94204, 94306, 2400001** (94202/94204/94306 look like a matched
set - plausibly the three boss types). The 94xxx family clusters: 94101/94102/94202/94204/94302/
94306/94308/94310. This gives a daily event calendar without guessing anything.

Boss-related init keys: `actBerserkBoss={remainPraise=3, array=[]}`,
`actBossTrans={actBossTransTimes=0, actBossTransLastTime=0, actBossTransLastRound=1}`,
`daily_kill_boss=0`, `daily_kill_act_boss=0`, `kill_lock_hart_boss=0`,
`attack_behemoth_boss_time=0`, `daily_kill_lock_hart_boss_leader=0`.

**Every boss counter is 0 and every boss array is empty because the account has not participated
today.** type22's `self.rank=-1` says the same thing. Strong hypothesis: these boards populate only
after participation. CHEAPEST NEXT TEST: Henry kills a boss in game, then re-run the 1-24 scan and
re-dump init. If counters move and boards fill, the boards were participation-gated all along.

## ACCOUNT IDENTITY - CORRECTED 2026-09-20

The account this tooling logs into is **NOT Henry''s SaFa main**. From the init dump''s user block:

    gameUid    2359053554001967      serverId 1967
    name       Sadmin
    level      10                    (HQ 10, not 30)
    allianceId (empty)               -> NOT IN ANY ALLIANCE
    regTime    1775837563620         = 2026-04-10
    country    GB   emailConfirm 1   bindFlag true   (bound to myth.koden@gmail.com)

Cross-checked: uid 2359053554001967 appears in NONE of the 17 alliance rosters (1,223 players) and
on ZERO leaderboards. It is a small alt on the same server as SaFa.

**This retro-explains several wrong conclusions:**
- `alliance.reward.list` -> E000000 because THIS ACCOUNT HAS NO ALLIANCE. NOT because an earlier
  `-collect` claimed the gifts (that theory was wrong; the collect run only took idle rewards and
  greeted visitors, alliance actions would have no-opped).
- `al.help.all`, `al.science.refreshNum` -> E000000: same reason.
- Every boss/event board empty and `self.rank=-1`: this account participates in nothing.

**Still valid:** all SaFa and server-wide data. `al.rank {allianceId}` is addressed by alliance id
and works from any account - proven from a fresh guest on State #1281.

**Also note:** `get.user.info` on your OWN uid returns a stub `{uid=...}` only. It returns a full
profile for OTHER players. Self data comes from the init dump, not from get.user.info.

## NEXT PHASE: PACKET CAPTURE ON A DIFFERENT MACHINE

Henry is doing the capture on another PC (this one has no Wireshark, no Npcap, no game installed).
Handoff prompt for that machine''s agent: `CAPTURE-PROMPT.md` in this folder.

Built and ready here: `lastwar-client\pcap.exe` (capture decoder), `lastwar-client\lastwar-client.exe`.

The capture answers, in one session, the two things guessing could not:
 1. the boss-event ranking command names + params (3 boss types, individual + alliance)
 2. `alliance.reward.list`''s real params AND whether purchaser identity survives "hide name"

Game socket is PLAIN TCP, no TLS - no cert work needed. Chat WebSocket is TLS and needs mitmproxy
separately; out of scope for the first capture.

## CROSS-SERVER ENUMERATION - SOLVED 2026-09-20

**`rank.get {"type":N,"serverId":<id>}` works cross-server.** Proven from a fresh guest sitting on
server 1303: passing serverId=1967 returned 1967''s alliance board (leader Ghos), 1967''s alliance
kill board (SaFa''s 32-hex id) and 1967''s 200-deep power board. `serverId` is the ONLY param name
that works - `srcServer`, `server` and `zone` are silently ignored (response echoes your own server).

Combined with `al.rank {"allianceId"}` (already known cross-server), this gives a full pipeline for
ANY server from ANY account, including a throwaway guest:

    phase 1  rank.get {"type":1|2,"serverId":S}  -> 51-59 alliance ids for server S
             rank.get {"type":4|13,"serverId":S} -> 200-deep power / THP boards for S
    phase 2  al.rank {"allianceId":A}            -> full roster for each alliance

Validate every cross-server response by checking the echoed `serverId` matches what you asked for -
a mismatch means the server ignored the param and answered for your own server.

**Coverage and its limit.** This captures every player who is in an alliance, plus the top 200 by
power and top 200 by THP. It does NOT capture unaffiliated players outside the top 200 - the account
`Sadmin` is exactly such a player and appears nowhere. True map enumeration would need
`world.get.block`, still E000000 across 11 param shapes, and `world.get.detail.new {pointId}` also
E000000. So "full list of players" means "all alliance members + top 200s", not literally everyone.

**uid encodes the origin server.** `1002324127001967` -> last 6 digits zero-padded = 1967. Compare
`originServer` (from uid) against the board server and `curServerId` to spot transfers. This is the
transfer-tracking primitive and it works retroactively on data already collected.

## SNAPSHOT STORAGE - NEVER OVERWRITE

    data\snapshots\<YYYY-MM-DD>\
      raw\                 phase1_boards.log, p2_chunk*.txt, p2_rosters_*.log, p2_progress.txt
      alliance_ids.json    server -> [alliance ids]
      phase1_summary.csv   per-server board/alliance counts
      servers\server_<id>_players.csv
      all_players.csv
      summary.csv

Each run writes a NEW dated folder. Nothing under data\server1967\ or data\safa\ is ever touched -
those stay as the 2026-09-19 baseline. Re-run `scripts\parse_phase2.py <YYYY-MM-DD>` to rebuild a
snapshot''s CSVs from its raw logs.

Rate note: ~3.4s per al.rank in practice (large responses), so ~1,765 rosters takes roughly
100 minutes across 4 chunked sessions. Chunking by 8 servers means a dropped connection costs one
chunk, not the run.

## MAP ENUMERATION - partially cracked, blocked on world.get.block

Probed on an ISOLATED guest (see harness note below), concurrently with a real-account scan.

**Working from a plain guest, no alliance, HQ 1:**

| Command | Returns |
|---|---|
| `find.enemy.point {"level":N}` | `{uid, pointId, uuid}` of one nearby enemy base. Different `level` values return DIFFERENT players, so it is a crude walk, not a census |
| `find.monster {"level":N,"pointId":P}` | `{pointId, uuid}` |
| `world.get.march.infos {"x":X,"y":Y}` | `marchInfos` with raw `_proto` protobuf; player names are readable as ASCII inside the byte arrays |
| `lw.req.world.occupy.info {}` | whole-server occupation dataset, base64 protobuf. Server-scoped |

**Still blocked:** `world.get.block` and `world.get.detail.new` answer E000000 to EVERY param shape
tried (~15): pointId, pId, pIds, x/y, x1y1x2y2, startX/startY/endX/endY, minX/minY/maxX/maxY,
blockX/blockY, blockId, blockIndex, index, size, range, w/h, worldPos (serverId<<32|pointId),
points, ids, with and without serverId. Feeding a REAL occupied pointId (8030, from
find.enemy.point) does not help, so it is not a valid-point problem. `world.get.new` is not
registered at all despite being named in the docs.

Stop guessing. This is now the TOP priority for the packet capture - scrolling the world map in the
real client will reveal the params in one go. CAPTURE-PROMPT.md updated to make it priority 1.

## ISOLATED GUEST HARNESS - use this, not the parking one

`harness\guestprobe_iso.ps1` runs a guest with its own `USERPROFILE`/`HOME`
(`C:\Temp\LWC\guesthome`), so it writes its own identity files and NEVER touches the real
account''s. Safe to run CONCURRENTLY with a scan on the real account - no parking, no restore, no
window where a crash leaves credentials displaced. Prefer it over `guestprobe.ps1`.

It wipes `guesthome\.lastwar_goclient_*` on every start: a leftover gameuid with no loginkey forces
GSL `opt=fix`, which the server rejects and the session dies silently after the server list.

## TRANSFER TRACKING ALREADY WORKS ON EXISTING DATA

uid suffix = ORIGIN server (zero-padded). Server 1967''s 1,223 rostered players break down as:

    origin 1967  1035      origin 1944     8
    origin 1957   101      origin 1939     5
    origin 1963    41      origin 1934     1
    origin 1951    31      origin 1929     1

and `curServerId` shows 10 of them currently sitting on another server
(1983 x2, 1960 x2, 1975 x2, 1986, 1971). So inbound migration history and current whereabouts are
both readable from data already collected - no new commands needed for that part.

Caveat seen live: a guest on server 688 got `find.enemy.point` uids ending `000367`, i.e. players
whose origin is server 367. Consistent with merged worlds. Do not assume a player found on server X
originated there - always read the uid suffix.

## SNAPSHOT 2026-09-20 COMPLETE - 32 servers, 51,697 players

Phase 2 finished cleanly: 1,765/1,765 al.rank calls across 4 chunks (22:39 -> 23:20 local).
1,764 alliances returned a roster; 1 returned nothing.

    players from rosters   51,475
    added from boards      222   (141 from board 4, 81 from board 13 - not in any roster)
    TOTAL                  51,697
    integrity              0 blank names, 0 blank power, 0 duplicate uids

Per-server range 1,137 (1976) to 2,456 (1961). Server 1967: 1,654 players vs the 1,223 baseline of
2026-09-19 - **445 newly seen**, because yesterday only had the 17 stronghold-holding alliances
while the board gives all 59. 14 baseline players were not seen today.

### curServerId is TRANSIENT - do not use it for transfer tracking

Today every single player has `curServerId == homeServerId == boardServer` (0 mismatches in
51,697). Yesterday''s 1967 pull had 10 players showing a different curServerId (1983 x2, 1960 x2,
1975 x2, 1986, 1971). Same field, same command. So curServerId reflects **where a player is right
now** - almost certainly off visiting another server for a cross-server event - not where they live.
It is a good "currently away" signal and a bad migration signal.

**The durable migration signal is the uid suffix (originServer) versus the server they are rostered
on.** That is stable and readable retroactively.

Across the 32-server range: 68 distinct origin servers seen, and **3,521 players originated outside
1957-1988 entirely**. Inbound migration is already substantial before the announced transfer.

### Files (never overwritten; new dated folder per run)

    data\snapshots\2026-09-20\   47 files, 62.9 MB
      all_players.csv                51,697 rows x 16 cols
      servers\server_<id>_players.csv  32 files
      summary.csv                    per-server players / alliances / non-native counts
      alliance_ids.json  phase1_summary.csv
      raw\  phase1_boards.log, p2_chunk1-4.txt, p2_rosters_1-4.log, p2_progress.txt

Rebuild any snapshot''s CSVs from its raw logs: `python scripts\parse_phase2.py <YYYY-MM-DD>`.

## COVERAGE REALITY CHECK - the snapshot is NOT a full census

Measured 2026-09-20 on the real account (Sadmin, server 1967). Sampled the live map with
`find.enemy.point {"level":N}` for N=1..34, got 25 responses / 22 distinct players, and checked
each uid against the 51,697-player snapshot:

    already in the snapshot :  1
    NOT in the snapshot     : 21
    apparent coverage       : ~5%

The 21 misses are not exotic - most are natives of 1967 sitting on the map right now.

**Caveat on the number.** `find.enemy.point` means "find me an enemy", so it plausibly favours
non-allied or weaker targets, which is exactly the population the roster+board method misses.
So ~5% is a LOWER BOUND on true coverage, not a measured coverage figure. But 1 hit in 22 still
says the missing population is large, not marginal. Do not quote 5% as if it were the real number;
quote it as "one hit in twenty-two on a biased sample".

**What the 51,697 snapshot actually is:** a solid census of ORGANISED players - everyone in an
alliance across 32 servers, plus the top-200 power and THP boards. Good for tracking alliance
movement through a transfer. It is NOT the server population, and must not be described as
"all players".

Only `world.get.block` closes this. That makes it the single highest-value unknown in the project -
above the boss boards, above gift anonymisation, above chat.

## lw.req.world.occupy.info — DECODED (2026-09-22)

Earlier note called this an "occupation dataset" without decoding it. Now decoded.

**Accepts `serverId`** — same param name as `rank.get`. Any other param name
(`zoneId`, `type`, `seasonId`) is ignored and you get your own server back, so
always check the echoed `serverId` in the response, not just the content.

### Wire schema (reverse engineered, no .proto)

    response.content = base64(protobuf)
    top field 1, repeated  AllianceOccupation
        1  string  allianceId (32 hex)
        2  string  tag
        3  uint32  memberCount
        5  string  alliance name
        6  uint32  homeServerId
        7  repeated { 1: uint32 tileId }     tile set A
        8  repeated { 1: uint32 tileId }     tile set B
    top field 2, repeated  ServerOccupation
        1  uint32  serverId
        2  packed repeated uint32 tileId

Tile ids run 1..~2165 and are globally unique within a payload; sets A and B
never overlap (0 shared ids in both samples) => two distinct tile categories,
category meaning still unknown. These are NOT 1000x1000 map pointIds.

Decoder: `scripts/decode_occupy.py` (has a self-check; writes
`data/occupy/occupy_<srv>_alliances.csv` and `_servers.csv`).

### What it actually gives

Alliance-level war-map occupation for a whole **cross-server war group**, not
individual players. No uid, no player name, no coordinates.

    queried 452 -> 118 alliances, 12,476 members, group = 432 450 452 460 463 464
                   467 481 510 514 515 516 529 536
    queried 688 ->  77 alliances,  7,045 members, group = 679 685 687 688 689 690
                   700 745 762 782 792 794

Useful for: full alliance list per war group with exact member counts and each
alliance's home server (no top-N truncation, unlike `al.rank`), and war-group
composition itself.

Useless for: the individual-player census. Does not help the map problem.

### Availability is season-gated

    1957..1988  ALL return content="" (empty)   [32/32 probed 2026-09-22, guest]
    452, 688    return full payloads

Empty is not an error and not a param problem — those servers simply have no
occupation season running. Re-probe the 19xx band when a season is active
before assuming the command is useless there.

## APK DECOMPILE — 2026-09-22 (com.fun.lastwar.gp 1.0.363-1884, APKM from APKMirror)

This closed most of the open questions. No packet capture was needed.

### Where things live

    apk/base.apk                     Android shell, 7 dex, Unity data
    apk/split_config.arm64_v8a.apk   native libs (Mono + xLua, NOT il2cpp)
    apk/split_install_time_pack.apk  assets: Assemblies/*.mdl, lwScripts/LWScripts.data
    apk/lua/                         18,733 unpacked Lua 5.3 bytecode files
    apk/asm/Assembly-CSharp.dll      recovered .NET assembly
    apk/src/                         3,637 decompiled C# files (ilspycmd)

**Assembly-CSharp.mdl is NOT encrypted.** Only the first 8 bytes are XOR 0x13;
everything after is a plain PE. Patch those 8 bytes and it opens in ILSpy/dnSpy.
`scripts/fix_mdl.py` does it. `dotnet tool install -g ilspycmd` then
`ilspycmd -p -o <dir> Assembly-CSharp.dll`.

**LWScripts.data is a plain container**, no encryption:

    "LWLF" u32 ver=1  u32 821  u32 fileCount=18733
    per file: varint nameLen, name, u32 size, Lua 5.3 bytecode

`scripts/lwlf_unpack.py`. The luac header is modified (format byte 1, 4 size
bytes not 5), so stock unluac will not load it; `scripts/luac_consts.py` pulls
the constant pool instead, which is all that was needed.

### THE COMMAND REFERENCE — data/api/commands.csv

3,094 commands, 1,786 with request fields, built by `scripts/build_api_ref.py`
from `Net/Config/MsgDefines.luac` (name->command), `Net/Config/MsgMap.luac`
(name->module) and each `Net/Msgs/*.luac` constant pool. **Check this file
before probing anything.** Guessing param names is over.

Protobuf schemas: `Net/Proto/*.luac` embed the literal .proto SOURCE as a
plaintext string. 8 of them extracted to `data/api/proto/`.

### Answers to the questions that were open

`world.get.block` EXISTS but is C#-only, so it is absent from the Lua table.
Request (from `apk/src/WorldGetBlockMessage.cs`):

    _id          int    futureId, client-generated request id  (REQUIRED)
    bigMap       int    1 normally, 2 in single-server mode
    x, y         int    camera tile pos
    serverId     int    default -1
    worldId      int
    type         int    0
    viewLvl      int    server LOD, clamped to <= 2
    timeStamp    int    server time
    index        int[]  AOI block indices  (PutIntArray, MAX 160 per request)
    blockSize    int    _lwAoiBlockSize, 4 at init
    clearUuidSet int    1 only on the first request
    force        bool   only on battlefield first
    leftBottom   int    tile index of view corner
    rightTop     int    tile index of view corner

Every earlier probe failed because `_id`, the `index` ARRAY, `blockSize` and
the two corners were missing. E000000 was the generic "unsatisfiable", as
corrected earlier.

AOI math (`apk/src/WorldPointManager.cs`):

    AoiBlockToIndex(x,y) = y * _lwAoiBlockCount + x
    AoiBlockToTilePos(b) = (b.x * blockSize * 2, b.y * blockSize * 2)
    corner tile index      = x + y*3000 + 1        (note 3000, not 1000)
    tile grid              kTileCountX/Y = 1000; season big map = 3000

Response (`ParseWorldGetBlock`), either the message itself or each element of
`serverPointArr`:

    serverId int, worldId int
    points   SFSArray of ByteArray -> WorldPointInfo protobuf   <- PLAYER BASES
    lands    SFSArray of ByteArray -> LandPointInfo
    deserts  SFSArray of ByteArray -> DesertInfo
    alInfos  SFSArray of ByteArray -> WorldPointInfo
    alAssistanceInfo ByteArray, worldFlags, effectObj, protectedBuildingInfos

`WorldPointInfo.buildInfo` carries **ownerUid, name, allianceId, alAbbr,
level, buildId, protectEndTime** - i.e. the full per-tile owner record. This
is the route to a true server census, including players in no alliance.

Also pushed unsolicited as `push.world.get.block`.

### Other confirmed shapes (from the reference, several verified live on guest)

    world.get.detail.new   point, serverId, worldId, previewAssistance, pointType, uid
                           (the field is `point`, NOT pointId, and worldId is required)
    rank.get               type, global, serverId   <- `global` was unknown; global=1
                           returns a genuine cross-server board
    alliance.reward.list   index, len, type   (pagination)
    alliance.reward.hide.name  openHide       (setter)
    find.enemy.point       type               (I had been sending `level`)
    find.resource.point    type, level, param
    find.monster           pointId, level, isBoss, searchRange
    world.get.march.infos  x, toInt, y, needCross
    world.get.alliance.city.info / .city.stronghold.info / .supplies.city.info   serverId
    al.rank                allianceId
    user.remark.list       page, pageSize
    season.migrate.*       see below

### Premium gift anonymisation

`alliance.reward.list {index,len,type}` returns `alGiftHideName` as a FIELD
alongside the list, and `alliance.reward.hide.name {openHide}` is the setter.
So the hide flag is server-side state that is sent to the client. Whether the
uid survives in a hidden record is STILL UNKNOWN - the guest has no alliance,
so the list came back empty. Needs one run on an account inside a real
alliance, one command, per Henry's standing rule.

### Server transfer / migration

    season.migrate.server.info        serverId   -> WORKS cross-server. Returns
       serverState, applyPower/applyAutoPower/applyAutoLevel/applyAutoSwitch,
       president + firstLady (uid, name, abbr, serverId).
    season.migrate.mark.search.name   content    -> errorCode 370100
                                                    errorMsg "migration not open"
    season.migrate.apply.list.search  name
    season.migrate.apply.list.info    startIndex, number
    season.migrate.al.market.list     startIndex, num, searchName, searchTag,
                                      searchLanguage, saveTag, comprehensiveScore, desertTime
    season.migrate.server.star.market.list  -> 370100 "act not open"

Note 370100 is a NAMED error with a human errorMsg, unlike E000000. When the
transfer window opens these become the direct route to who is moving where.
Re-probe then.

## MIGRATION WAVE 165 — 1957..1988, captured 2026-09-22

Guests CANNOT be used for this. `LW_ZONE=APS<id>` (new, in internal/auth/login.go)
places a fresh guest on any chosen server and GSL obliges, but 1957..1988 refuse new
account creation: `ec=28 ep=[E013]`. Verified APS2321 succeeds, APS1988/APS1967 fail.
So the migration commands can only be run from an account that already lives there.

Caller-gated vs cross-server (guest, own server 74/1204):

    WORKS cross-server   season.migrate.server.info {serverId}
                         season.migrate.apply.panel {serverId}   -> top 3 alliances
    370100 "act not open" activity.info, market.list, apply.list.info,
                          mark.search.name, score.info, star.market.list

Run on Sadmin (State #1967, gameUid 2359053554001967, HQ 10, NO alliance, not
president) — read-only commands only, no writes, `mark.player` deliberately excluded:

    season.migrate.apply.list.info   WORKS. No alliance and no rank needed - the
        R4/R5/president check in ActMigrationManager is CLIENT-side UI gating only.
        Returns applicants TO this server, each with their SOURCE serverId.
        startIndex/number appear to be ignored: 0/50/100 all returned the same 61.
        Fields: uid, name, power, serverId(source), alName, abbr, applyScore,
                applyState, identity, time, message, flag.
        applyState was 2 for all 61. identity spans 0..3 = the
        SuperLow/Low/Normal/High buckets (enum order not yet confirmed).
    season.migrate.market.list       WORKS. Players on THIS server advertising to
        leave: uid, name, power, lv, abbr, gender, countryflag, bInvited, time.
    season.migrate.activity.info     WORKS. migrateOpenId=165 plus migrateServerList,
        the authoritative 32-server wave list.
    season.migrate.score.info        WORKS. This account's own migration score.
    season.migrate.al.market.list    E000000
    season.migrate.get.alliance.market  310079 "not join alliance"
    season.migrate.server.star.market.list  E100008 "season is min cfg"

Note dumpsession.exe failed here ("GSL returned no servers" on opt=login) but was
not needed: the session.json accessToken still worked for a direct cross-server dial.

Outputs in data/migration/: applicants_1967.csv (61), market_1967.csv (7),
migrate_wave_servers.csv (32), servers.csv (32, guest), top_alliances.csv (96, guest).

## MAP SCAN — world.get.block SOLVED, 2026-09-22

`cmd/mapscan` (built as `mapscan.exe`) does the whole job. Two modes:

    mapscan -config <session.json> -servers 1957-1988 -out data\map
    mapscan -config <session.json> -servers 1967 -detail data\map\map_1967.csv -out <snapshotdir>

Scan mode sweeps AOI blocks and writes one CSV per server:
serverId,pointId,x,y,pointType,ownerUid,name,allianceId,alAbbr,level,buildId
391 requests per server, roughly 63 seconds each, ~5k players per server.

### What made it work

Three things no amount of blind probing would have found (all from the decompile):

1. `_id` — a client-generated futureId. Without it the server never answers.
2. `index` is an SFS int ARRAY of AOI block ids, max 160 per request. `-interactive`
   only speaks flat scalars, which is why this command could never be probed there.
   Added `sfs.PutIntArray` (the wire encoder already handled type 12 both ways;
   only the setter was missing).
3. `blockSize` + `leftBottom`/`rightTop` tile-index bounds.

AOI math: size = lwAoiBlockSizeArray[lod] (4 at lod 0), count = 1000/size = 250,
index = y*250 + x, tile index = x + y*worldSize + 1.

Response: `serverPointArr` -> per server { serverId, worldId, points[], lands[],
deserts[], alInfos[], greenPoints[], worldFlags, effectObj, triggers }. `points` is
an array of BYTE ARRAYS, each a WorldPointInfo protobuf. Field map in
data/api/proto/WorldPointInfo.proto; the parser lives in cmd/mapscan/main.go.

### Access rules (learned the hard way)

- **Guests cannot scan other servers.** Cross-server map view is level-gated:
  `errorCode=cross_server_view_tips, errorMsg="level limit"`. A guest can only
  scan the server it lives on.
- **LW_ZONE guests are useless for this.** `LW_ZONE=APS<id>` does place a fresh
  guest on a chosen server, but the character is never provisioned there: no init
  push ever arrives, the account is not "in world", and world.get.block is ignored
  (no response at all, not even an error). Only auto-assigned guests work.
- **Sadmin passes the cross-server gate.** The range scan runs on it. The gate is HQ 13
  (locale cross_server_view_tips: "Headquarters has not reached Lv.13"; confirmed 2026-09-30 when
  the HQ 10 service account lastwar_1 / lw_1 was still refused).
- Sadmin's loginKey no longer resolves through GSL ("no servers returned"), so
  mapscan grew a `-config` flag that reuses the saved session's direct dial.
- A guest home dir must be WIPED before each run: a persisted gameUid with no
  loginKey forces opt=fix, which the server rejects with ec=28 ep=[E011].

## POWER / ACTIVITY TRACKING — 1967

The map carries NO power. `world.get.detail.new` does, one request per occupied tile:

    world.get.detail.new {"point":<pointId>,"serverId":1967,"worldId":0,
                          "previewAssistance":0,"pointType":6,"uid":""}
    -> uid, name, power, killArmy, careerLv, careerType, allianceId, afn, alName,
       srcServer, vl, rank, plunderResRate, alIcon, pic/picVer

`uid` can be EMPTY — point + serverId + pointType is enough. Confirmed live.

Why per-tile and not `al.rank`: on 1967, **69.4% of players (3,800 of 5,476) are in
NO alliance**, so roster commands miss more than two thirds of the server. The map
plus per-tile detail is the only complete route.

`mapscan -detail` does this: ~5,476 requests, roughly 10 minutes for a full 1967
snapshot. Output: power_1967.csv with a scanTime column.

### Activity rule

Power going UP proves activity. Power going DOWN proves nothing — troops die,
buildings get demolished, a dormant account gets farmed. So `scripts/power_diff.py`
compares each account against the HIGHEST power it has ever shown across all prior
snapshots, not merely the previous one, and only a positive delta counts as ACTIVE.
Statuses: ACTIVE / flat / down / new, plus a list of uids that vanished.

    python scripts\power_diff.py <old power_1967.csv> [...] <new power_1967.csv>
    -> activity.csv next to the newest snapshot

Snapshots belong in data/snapshots/<YYYY-MM-DD>/ and are NEVER overwritten.

### uid encodes origin server

Last 6 digits of a uid, zero-padded, are the account's HOME server. The 1967 map
already shows migration history without any migration command at all:

    1967 4187 | 1957 645 | 1963 264 | 1951 186 | 1944 95 | 1939 43 | 1934 24 | 1955 23

### RULE: one session per account at a time

Logging in a second time on the same account silently kills the first session.
Confirmed the hard way on 2026-09-22: an `al.rank` probe run through
harness/runcmds.ps1 while a range scan was mid-flight on Sadmin killed the scan's
connection. The process stayed alive with its TCP socket in ESTABLISHED and simply
timed out every subsequent request (6s each, no error logged), stalling silently
after server 1978. CPU frozen at a constant value is the tell.

Never run runcmds.ps1, mapscan, or any other real-account tool concurrently.
Guest harnesses are fine alongside a real-account run, since they use separate
identities in isolated home directories.

Mitigation worth adding to mapscan if this bites again: treat N consecutive
timeouts as a dead session and exit non-zero instead of grinding on.

### Power collection: hybrid route (28% fewer requests)

`al.rank` power is IDENTICAL to `world.get.detail.new` power - verified on 4
overlapping SaFa accounts, exact match on all four. So the two sources can be mixed
in one snapshot safely.

    167 al.rank requests    -> 1676 players in alliances
    3800 detail requests    -> 3800 players with no alliance
    3967 total, vs 5476 all-detail

`scripts/split_power_routes.py` writes both inputs:
    data/raw/alrank_1967.txt            command file for harness/runcmds.ps1
    data/raw/map_1967_noalliance.csv    filtered map, feed to mapscan -detail

al.rank also cross-validates the map sweep: it returned 97 SaFa members and the map
found exactly 97 SaFa bases on 1967.

## C# MIGRATION — started 2026-09-22

New solution at `C:\Temp\LWC\LastWar`, .NET 10, built to `C:\Temp\LWC\bin\lastwar.exe`.
Decisions: SQLite as the store, CLI now with libraries kept web-ready, Google.Protobuf
with classes generated from the .proto files recovered from the APK.

    src/LastWar.Core       domain records + ActivityAnalyzer, NO IO, no dependencies
    src/LastWar.Protocol   SFS2X wire + GSL crypto + protobuf        [NOT YET PORTED]
    src/LastWar.Client     login, session, command send/await        [NOT YET PORTED]
    src/LastWar.Storage    SQLite (LastWarDb) + CSV + legacy importers
    src/LastWar.Cli        verbs: init, import-map, import-power, snapshots, activity
    tests/LastWar.Tests    xunit; 10 tests, all green

`TreatWarningsAsErrors` is on everywhere. Microsoft.Data.Sqlite pulls a
SQLitePCLRaw with a high-severity advisory, so `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5
is pinned explicitly - do not remove that reference.

### Status: what is and is not migrated

DONE in C#: the domain model, the activity rule, SQLite storage, CSV read/write,
importers for the Go/Python era artefacts, and the CLI.

SUPERSEDED (kept for history): this said the protocol, GSL login and the scanners were
still Go. All three are now C#. See "What is actually left in Go" at the end of this file
for the current answer.

### ACTIVITY RULE — corrected

Power only rises because a human logged in and did something, so ANY rise against the
PREVIOUS snapshot is an activity flag. A fall proves NOTHING: troops die, buildings get
demolished, a dormant account gets farmed. Each step is judged on its own, never
against an all-time peak: 1000 -> 500 is "down", and the next 500 -> 600 is ACTIVE even
though 600 is well under the old 1000. `ActivityAnalyzer` implements exactly this and
`RecoveryAfterDropCountsAsActive` pins it.

`activeEver` is a separate, weaker flag: rose at some step in the whole history.

## BASELINE — 1967, 2026-09-22

    map scan      32 servers, 169,943 players, all imported to SQLite
    1967 power    5,581 accounts  = 1,785 from al.rank + 3,796 from tile detail
                  (rosters add ~105 accounts that have no base on the map)

Hybrid route cost 3,967 requests instead of 5,476. Both sources report the same power
number - verified exactly on four overlapping accounts.

    lastwar import-power --server 1967 --at <iso> --rosters <al.rank log> --detail <csv>
    lastwar activity --server 1967 --out activity.csv

A second snapshot is needed before `activity` says anything. Take one on a later day.

## C# PORT COMPLETE — 2026-09-23

The protocol layer is ported and proven live. `lastwar.exe` logs in, runs commands and
scans maps with no Go involved.

    lastwar call --cmd season.migrate.server.info --json {"serverId":1967}   -> real data
    lastwar call --cmd world.get.block --json {...,"index":[0..9],...}       -> serverPointArr, _id echoed
    lastwar scan-map --servers 1970                                          -> 4,024 players

Cross-check: the C# scanner found 4,024 players on 1970 where the Go scanner found
4,017 hours earlier. 0.2% apart, i.e. ordinary churn, not a decoding difference.

### What the port needed that the Go source alone did not make obvious

`ta` must carry a populated iOS analytics blob when iosMode is set. Sending `{}` gives
`ec=28 ep=[E005]` on an iOS-issued account - exactly the error that was previously
blamed on iosMode not being set at all. `LoginParamsTests` pins the blob's `lw_zone`
and `#bundle_id`.

### Protobuf

`Net/Proto/WorldPointInfo.proto`, recovered verbatim from the APK, is compiled by
Grpc.Tools into real classes. No hand-rolled field walking in C#.

### Tests: 27, all green

The SFS codec is checked against a golden vector computed by hand from the wire format,
not produced by the encoder, so a bug in the writer cannot make the test agree with
itself. Frame tests cover the XOR-by-length obfuscation at awkward payload sizes and
the zlib threshold.

### Still Go, and why it no longer matters much (SUPERSEDED - see the end of this file)

`gsl.go` - the RSA+AES `getserverlist` bootstrap. Only needed to create a FRESH guest
identity. Established accounts reconnect through the saved session's direct dial, which
C# does. Port it when guest scanning is needed again.

`mapscan.exe` stays on disk as a reference implementation; `lastwar scan-map` replaces it.

## CHAT — 2026-09-23. Signature algorithm cracked; transport mapped.

Chat is NOT on the SFS2X game socket. It is a separate WebSocket, driven from Lua
(`Chat/*`, 180+ files) through a native bridge, so none of the game-command work
applies to it.

### Endpoints (production, from Chat/Controller/ChatNetManager.luac)

    https://lastwar-serverlist-cf.lastwarapp.net/gameservice/chat_server_links.php
    https://lastwar-serverlist-us-aws-ali.lastwargame.com/gameservice/chat_server_links.php
    https://lastwar-serverlist-us-gcp-ali.lastwargame.com/gameservice/chat_server_links.php
    new-line variants:  https://lastwar-chat-cf.lastwarapp.net/server/links   (and -us-aws-ali / -us-gcp-ali)

CONFIRMED LIVE 2026-09-23: a bare GET to chat_server_links.php answers
`{"code":-1,"message":"error params","data":null}`. The endpoint is reachable and only
the parameters are missing. ChatNetManager requires `AESHelper` at the top of the file,
so the params are very likely AES-wrapped the same way getserverlist.php wraps its
`uuid`+`data` pair. Response fields are `code`, `data`, `data_new`.

### Connection headers

    APPID  100017
    TIME   ChatInterface.getServerTime()
    UID    ChatInterface.getPlayerUid()
    SIGN   cutils.CalcChatSign(...)

then `setdelegate`, `setrecvtable`, `enableping`, `autoclose`.

### SIGN algorithm — REVERSED

`cutils` is a native Lua module in **libxlua.so**; `CalcChatSign` is at VA 0x16dc68.
Its PLT calls resolve to lua_gettop, luaL_checklstring, luaL_checkinteger, sprintf,
memcpy, md5_str, strcpy, strcat, strlen, lua_pushstring, which pins the body exactly:

    CalcChatSign(s1, s2, n):
        if argc <= 2            -> push ""            (error)
        if #s1 > 128 or #s2 > 128 -> push ""          (error)
        a = md5_hex(s1 .. s2)
        b = a .. string.format("%u", n)
        SIGN = md5_hex(b)

So **SIGN = md5( md5(s1 .. s2) .. tostring(n) )**.

No salt is embedded in the function - there is no secret in the .so. Combined with the
128-byte cap this rules the chatToken OUT as an argument: the captured token is 172
chars and would trip the length check. So the arguments are short values (APPID, UID
and/or serverId) and the real authentication must happen in the socket's own login
message, not in the handshake headers.

Reproduce in C# with `GameIdentity.Md5Hex` - no native code needed.

### Per-message signing (Lua side, reproducible)

`WriteSign`/`GetSign` builds `sign` from the message's `params`, `sendTime` and `seqId`,
with keys sorted, hashed via `GetMd5Hash`, mixed with `logInSign`. seqId must be a whole
number (the code warns when it has a fraction).

### Message surface

`Chat/WebMessage/*` holds ~100 commands and pushes (send, history, rooms, moments,
red packets) and `Chat/WebMessage/Config/ChatMsgDefines.luac` is their dispatch table -
the chat equivalent of Net/Config/MsgDefines.

### What is still unknown

1. Argument ORDER and identity of s1/s2/n in the CalcChatSign call. The constant pool
   proves which values are fetched (server time, player uid, APPID 100017) but not the
   order. Three or four candidates; cheapest to settle by trying them against the live
   handshake.
2. chat_server_links.php parameter shape and whether it is AES-wrapped.
3. The socket login message that carries chatToken (see Chat/WebMessage/Push/PushLoginSuccess).

Item 1 needs either a proper decompile of ChatNetManager.luac (blocked: the luac header
is modified, so stock unluac will not load it) or trial against the server. Items 2 and
3 fall out of the same file.

## CHAT — WORKING, 2026-09-23. Connected and authenticated.

Live proof:

    wss://lastwar-chat-gateway-cf.lastwarapp.net:443/
    <- {"cmd":"login.success","data":{"uid":"2359053554001967"},
        "clientid":"a4e6242c_...","server":"chat-gateway:6091","serverTime":1790124058075}

Chat is a plain JSON WebSocket. Messages are `{"cmd": "...", "data": {...}}`.

### Step 1 - server list (HTTP POST, NO encryption)

`ChatService.RequestServerList` in the decompiled C#:

    POST https://lastwar-serverlist-cf.lastwarapp.net/gameservice/chat_server_links.php
      t        = server time in SECONDS, as a string
      s        = md5( APPID + uid + md5( md5(t[0..3]) + md5(t[-3..]) ) )
      a        = "100017"          (CHAT_APP_ID)
      u        = uid
      serverId = self server id
      f        = "cn"              (only when isChina)

Returns `{"code":1,"message":"success","data":[...],"data_new":[...]}`, each entry
`{ip, port, protocol}`.

**Use `data_new`.** The `data` (old-line) hosts accept TCP on 443 but never complete the
WebSocket handshake. `data_new` works first time. The switch between them is the
`CheckChatNewLine` DataConfig flag in ChatNetManager.

### Step 2 - WebSocket handshake

URL is `{protocol}://{ip}:{port}` with NO path. Headers (`WebSocketItem.Connect`):

    APPID     100017
    TIME      server time (milliseconds)
    UID       player uid
    SIGN      md5( md5(APPID .. UID) .. TIME )
    CHATTOKEN chatToken from the game login's init push

The server drops bad handshakes silently rather than returning 401, so a hang means
wrong headers or the wrong host line - not a network problem.

### SIGN - reversed twice, independently

From libxlua.so `cutils.CalcChatSign` at VA 0x16dc68, whose PLT calls resolve to
md5_str/strcpy/strcat/sprintf:

    SIGN = md5( md5(s1 .. s2) .. sprintf("%u", n) )

and the Lua bytecode gives the arguments, `CalcChatSign(CHAT_APP_ID, uid, serverTime)`.
`WebSocketItem.CalcSign` in the decompiled C# says the same thing in one line:

    StringUtils.GetMD5(StringUtils.GetMD5(appId + uid) + time)

Note there is NO secret in the signature. CHATTOKEN is the actual credential, and it is
a header - an earlier note in this file guessed it was sent in a login message; that was
wrong.

### Command surface

`Chat/WebMessage/Config/ChatMsgDefines.luac` lists 102 commands. Reading ones worth
knowing: `chat.room`, `history.roomsv2`, `history.roomv2`, `history.roomlast`,
`history.roomgoto`, `chat.recv`, `chat.recvsync`, `get.user.info.multi`,
`search.player`, plus the `push.*` stream (`push.chat.room`, `push.chat.up.room`).
Writing ones to avoid unless intended: `chat.room.send`, `chat.share.send`, `chat.ban`,
`chat.room.mk.v2`, `chat.room.dismiss`.

Per-message signing (ChatNetManager `WriteSign`/`GetSign`) builds a `sign` from the
message `params`, `sendTime` and `seqId` with keys sorted, md5'd and mixed with
`logInSign`; seqId must be a whole number. Needed for sends, not for reading pushes.

### The tool that unlocked this

`lastwar lua-dump --file <x.luac> [--find <string>]` (src/LastWar.Lua) reads this
build's modified Lua 5.3 bytecode, which stock unluac refuses: format byte 1, and only
FOUR size bytes after LUAC_DATA (int, Instruction, lua_Integer, lua_Number) where stock
Lua writes five. `--find` prints the instructions around every use of a string
constant, which is how the CalcChatSign argument order was read off.

## CHAT CLIENT — streaming and history working, 2026-09-23

    lastwar chat --listen [--seconds N] [--raw]        stream live chat into SQLite
    lastwar chat --history --room <id> [--pages N] [--count 50]
    lastwar chat --script <file.jsonl>                 raw {cmd,params} probes, one session

Live capture proven: messages arrive, parse and store, with Unicode names intact
(console output is forced to UTF-8 - without that every non-ASCII name renders as ?).

### The missing piece was the per-message signature

A connected, authenticated socket still refuses EVERY command with
`{"code":1003,"msg":"无权限"}` (no permission) until requests are signed. From
ChatNetManager's GetSign:

    logInSign = WebSocketInst:GetSign()      -- the handshake SIGN, reused as a salt
    params    = serialize_sorted(params)     -- keys sorted, recursively
    sign      = md5( uid .. rapidjson.encode(params) .. sendTime .. logInSign )

and the envelope is `{cmd, params, sendTime, sign}`. Unsigned sends look identical on
the wire but are rejected, which is what made this look like a permissions problem.

### Request shapes

`history.roomsv2` / `history.roomlast` take `rooms` as a MAP, not an array:

    {"rooms": {"<roomId>": "0"}}

and the value is a FLAG, not a cursor - "0" newest, "1" oldest (Lua `setRoomIds`
writes "0"; `SetGetOldestMsgRoom` writes "1"). Passing a seqId there returns the
room's oldest messages, which looks like working pagination and is not.

`history.roomgoto` is the paging call: `{roomId, seqId, sort, count}` with
sort 1=JumpTop, 2=HistoryMessage, 3=NewMessage. It answers with `result.msg[]`
(singular), unlike the `result.rooms[].msgs[]` of the other history commands.

OPEN: sort=2 returned the same newest window rather than older messages in testing, so
deep backfill beyond the first block is not solved. `lastwar chat --history` detects
this (it stops when a page fails to reach further back) instead of looping. Worth
trying sort=1, or `history.roomgoto2` / `GetHistoryMsgsByTimeCommand`, next.

### Room membership is the real limit on what can be read

Pushes and history only ever cover rooms the account has JOINED. Sadmin is in exactly
one room (`custom_season_sustain_1322`, a cross-server season room) because it has NO
alliance, so alliance chat and the server/world room are not reachable from it.
Every guessed id - `country_1967`, `country1967`, `100017_country_1967` - returned 1003.

Room ids are not built client-side for the main rooms: ChatRoomManager has
`UpdateCountryRoomId`/`GenCountryRoomId` driven by the server, and the id never appears
in the game's init push (checked all 239 keys).

**To read SaFa alliance chat, the account must be in SaFa.** Group prefixes for
reference (Chat/Constant/ChatGroupType): `alliance`, `country`, `warzone`, `language`,
`custom_season_sustain_`, `crossbattle_`.

### Storage

`chat_message(room_id, seq_id, grp, sender_uid, sender_name, sender_al, msg, sent_at,
msg_type, raw)`, primary key `(room_id, seq_id)` - so re-running a capture or a history
pull is idempotent. `ChatStore.Insert` reports how many rows were genuinely new.

## WORLD CHAT — SOLVED, 2026-09-23

World/server chat room id is **`country_<serverId>`** - for 1967 that is `country_1967`,
with 400,614 messages of history going back to 2025-11-24.

    lastwar chat --history --room country_1967 --pages 10 --count 50
    lastwar chat --listen --seconds 120

### How the id is built (ChatRoomManager.GenCountryRoomId, closure #67)

    prefix = ChatInterface.GetRoomIdPrefix()      -- "" in production, "test_" offline/debug
    id     = seasonConfigId
             and string.format("%scountry_%d_%s", prefix, serverId, seasonConfigId)
             or  string.format("%scountry_%d",    prefix, serverId)

`GetSeasonConfigId` returns seasonInfo.seasonConfigId only when seasonId > 5 (season 5
has extra time conditions), otherwise nil.

IMPORTANT: for 1967 the live room is the PLAIN `country_1967`. The season-suffixed
`country_1967_1322` also exists but is empty (firstSeqId -1, owner CHAT_SYSTEM), as is
`warzone_1967`. So generate both and prefer whichever reports a real lastSeqId.

`1322` is the seasonConfigId, which is also the suffix of the season room
`custom_season_sustain_1322`.

### Why country_1967 looked forbidden earlier

It returned 1003 "no permission" only because those probes were UNSIGNED. Once the
per-message signature was added, the same room id worked immediately. An unsigned
request and a genuinely forbidden room produce the identical error, which is a very
easy wrong conclusion to draw - as happened here. Sign first, then judge permissions.

Membership is NOT required to read world chat from this account.

### History paging - use roomgoto2, not roomgoto

    page 1 : history.roomsv2  {"rooms":{"country_1967":"0"}}     newest block (~40)
    page n : history.roomgoto2 {roomId, seqId:<oldest seen>, sort:2, count:50}

`history.roomgoto` (no "2") accepts the same parameters and returns the SAME window
every time - it never advances, which looks like "no more history". `roomgoto2` moves
back ~49 messages per page and overlaps the previous window, so the
`(room_id, seq_id)` primary key absorbs the duplicates: a 99-message page yields 49 new
rows. Measured: seq 400614 down to 400134 in 10 pages.

Both goto commands answer with `result.msg[]` (singular), unlike `history.roomsv2`
which uses `result.rooms[].msgs[]`. The parser handles both.

### Practical note on a full backfill

400k messages at ~49 per request is roughly 8,000 requests. Fine to run, but pace it
and expect it to take a while; the newest few thousand are usually what matters.

## World chat backfill (paced)

`country_1967` holds 400,614 messages. Backward paging returns ~49 new per
request, so a full pull is ~8,000 requests - paced, not fired off in one go.

Runner: `harness\chatbackfill.ps1` (supervisor loop, started detached).
Each pass is a fresh `lastwar.exe` process:

    lastwar chat --history --room country_1967 --pages 150 --count 50 --resume --delay 1500

- `--resume` starts below `MIN(seq_id)` already stored (`ChatStore.OldestSeqId`).
- `--delay` is the gap between history pages, in ms.
- Storage is idempotent on `(room_id, seq_id)`, so any pass can be killed and
  re-run without duplicating rows.
- Process-per-batch is deliberate: a restart re-issues the chat token, so a
  long backfill never dies on token expiry. The supervisor stops when the
  oldest stored seq reaches 1.

RULE (extends the one-session-per-account rule): while a backfill is running,
do NOT check on it with anything that logs in. A second login on the same
account evicts the first - I did exactly this and killed a running worker
mid-pass. Check progress with the read-only verb, which never connects:

    lastwar chat --stats --room country_1967

At 1500 ms pacing the full 400k pull is roughly 9 hours.

### Backfill scope (decision)

Full 400k pull cancelled. Henry asked for the newest 10% only, so the
backfill stops at seq 360,553 (400,614 x 0.9). Stored: 38,412 rows covering
seq 360,004..400,614.

The supervisor loop is only needed for a full pull. For a bounded slice, one
process is enough - `--resume` makes it restartable either way:

    lastwar chat --history --room country_1967 --pages 275 --count 50 --resume --delay 700

Row count is slightly below the seq span (38,412 rows over a 40,611 span):
the gateway does not return every seqId - deleted and system entries are
skipped server-side.

## Dashboard (LastWar.Server + React)

A control surface over everything the CLI can do. Backend is ASP.NET Core minimal
API, front end is React built by Vite straight into the host's wwwroot, so ONE
process serves both:

    C:\Temp\LWC\dashboard.cmd          (starts the host and opens the browser)
    http://localhost:5183

Build:

    dotnet publish src\LastWar.Server\LastWar.Server.csproj -c Release -o C:\Temp\LWC\bin\server
    cd web && npm run build            (writes ..\src\LastWar.Server\wwwroot)
    npm run dev                        (port 5173, proxies /api to 5183, for UI work)

### Fleet model

A runner is one logged-in game session. Each is declared by a JSON file in
`data\runners\`, so adding to the fleet is dropping a file in and pressing
Reload - no code change:

    { "id": "sadmin", "name": "sadmin", "configPath": "C:\\Users\\hgarle\\.lastwar_goclient_session.json",
      "note": "real account - one session at a time" }

`RunnerRegistry.HolderOf` refuses to start a second runner pointing at the same
session file, because a second login silently evicts the first.

### Panels

- Fleet - start/stop each runner, state, server, uid, call and frame counters.
- Commands - all 3,094 commands, searchable; picking one builds a parameter
  skeleton from the catalogue's `Int:name` spec; send it and read the reply.
- Raw TCP - every frame both directions as it crosses the socket, with a hex +
  ASCII dump, filters and pause. Server pushes show their decoded payload.
- Data - the SQLite store: table list, ten saved queries, and free SQL.

### API

    GET  /api/runners                     POST /api/runners/{id}/start | /stop
    POST /api/runners/{id}/call           { command, params, timeoutSeconds }
    GET  /api/runners/{id}/events         server-sent events: frame | push | call | reply | state | error
    GET  /api/commands?q=                 GET  /api/db/tables | /api/db/presets
    POST /api/db/query                    { sql, limit }   SELECT/WITH only

### Two things worth remembering

`GameConnection.Frame` is a TAP, not a consumer. The first version of the runner
watched pushes by draining `Pushes()`, which reads the SAME channel that
`WaitForCommandAsync` reads - so the dashboard stole every reply and all commands
timed out. Observers must never consume that channel.

The API host pins `ContentRootPath` to `AppContext.BaseDirectory`. Without it,
wwwroot is resolved against the working directory and the UI 404s when the exe is
launched from anywhere else.

The "Active accounts" saved query returns nothing until a SECOND power snapshot
exists - there is one so far (5,581 accounts on 1967, 2026-09-22). That is the
rule working, not a bug: a rise against the previous sample is the only evidence
of a login.

## Sessions: guest bootstrap and email login (both now C#)

The Go client is no longer needed for either. `LastWar.Client.Auth` holds it all.

### GSL envelope (GslCrypto.cs)

Every account-gateway call is wrapped:

    salt = 20 chars from "abc..XYZ..089!@#$%^&*()_+-/=<>?{}[]"
    uuid = urlsafe_b64( RSA_PKCS1v15( salt ) )
    data = urlsafe_b64( AES256_ECB_PKCS7( form ) )   key = md5hex(salt) AS 32 ASCII BYTES

Three details fail silently if wrong, and are pinned by `GslCryptoTests`:

- the AES key is the lowercase hex STRING's bytes, not the 16-byte digest
- ECB with no IV, not CBC; RSA is PKCS#1 v1.5, not OAEP
- `uuid`/`data`/`bin` are URL-safe UNPADDED base64, but `airKey` is STANDARD base64 and
  its padding must survive into the plaintext form

The same salt decrypts the response, so one GslCrypto instance per request/response pair.

### Two HTTP calls, in order (GslClient.cs)

1. GET `/gameservice/getlsu3dversion.php` - walk the three hosts until one answers with
   `code == ""`. `resMsg` is the RSA public key (base64 DER SPKI). That host then STICKS.
2. POST `/gameservice/getserverlist.php` - body `data=..&uuid=..`. The PLAINTEXT form is
   `k=v&k=v`, not percent-encoded, in a fixed field order, and empty values are still
   emitted. `opt` is the whole story: `new` bootstraps a guest, `fix` re-resolves a device
   that already has a gameUid, `login` redeems an email login key.

Every scalar in these responses may be a string or a bare number - parse flexibly.

### Guest bootstrap

    lastwar guest --home C:\Temp\LWC\data\sessions\<name> [--zone APS1967]

Each guest owns its folder; the device identity lives beside the session, which is what
keeps a guest away from the real account's credentials. Verified live: a fresh guest came
up on APS1482 and answered `new.get.info`.

### Email login - it is all on the GAME SOCKET, no account web service

    -> account.login.send.verify.code  { mail, lang }
    <- push.account.send.verify.code   { errorCode?, emailExpireTime, mail }
    -> account.login.new               { type=0, mail, verifyCode, pf, deviceId, airKey }
    <- push.account.login.new          { errorCode?, loginKey, accountArr[] }

Notes that cost time if missed:

- the UI calls it `code`, the wire calls it `verifyCode`
- `type` 0 = email (2 Google, 4 pfId, 5 Play Games)
- `pf` is "market_global" on Android, "AppStore" on iOS
- commands.csv row 17 lists only `type; idToken; pgs_id` - that row is INCOMPLETE, the
  Lua message class is authoritative
- nothing in this exchange is signed
- `loginKey` is a ONE-SHOT bearer: redeem it at the gateway with `opt=login` for the
  chosen role, which returns the access token the saved session needs

Both halves must run on the SAME socket, so the server holds a throwaway guest connection
open between them while the code is fetched from the inbox.

Confirmed live (this was an open question): these commands ARE accepted on a guest socket
that has never logged into an account. `nobody@example.com` came back with
`errorCode 280048`, the server's own address rejection - so the path works and 280048
means the address was refused.

### Session store

`data\sessions\<id>\` holds the session json and the device identity; `data\runners\<id>.json`
is the spec that puts it in the fleet. Kinds: `guest`, `account`, `external` (a session
file owned by something else, like the profile one - forgetting it never deletes it).

API: POST /api/sessions/guest, /email/start, /email/verify, /email/save;
DELETE /api/sessions/{id}?files=true|false.

These files hold live access tokens in plain text. Same exposure as the Go client's
session file next to them, but treat the data folder accordingly.

## What is actually left in Go (audited 2026-09-23)

The C# port covers the protocol, login, scanners, storage and analysis, and adds chat,
Lua, SQLite and the dashboard on top. Two areas have NO C# counterpart at all:

1. `internal\game\` - the account AUTOMATION layer, 13 commands, the only code in either
   tree that acts on an account rather than reading it: `al.help.all`,
   `alliance.reward.allreceive`, `science.data.refresh`, `al.science.donate`,
   `building.production.collect`, `lw.pve.idle.reward`, `vip.add.login.score`,
   `vip.get.every.day.reward`, `visitor.operate`, `chat.get.system.mails`,
   `mail.read.status.betch`, `mail.reward.batch`, plus the `push.init.build` /
   `push.add.building` inventory parser. Two pieces of live-tested knowledge in there are
   NOT re-derivable from the APK: the bId -> collect-command whitelist
   (`buildings.go:683`, 13 types confirmed live) and mail's byte-aware batching
   (`mail.go:648`).
2. `internal\pcap\` + `-decode-stream` - the only way to read a packet capture in this
   repo, and how several protocol facts here were established.

Smaller gaps: session regeneration from a persisted loginKey (`cmd\dumpsession`) - C#
stores deviceId only, so a stale session means redoing email verification by hand;
`RedeemAsync` also hardcodes `iosMode=false`, which fails ec=28 for an iOS-issued
account. The refresh-token flow is half-built (`GslClient` speaks `opt=refresh`, but
`SessionConfig` has no `rt` field and `RedeemAsync` drops the token). Mid-login server
redirect is unhandled - Go recovers silently, C# will just fail. `cmd\serverscan` (zone
enumeration) has no equivalent. `EmailLogin` is reachable only over HTTP, not from the CLI.

Safe to lose: `cmd\chatprobe` (dead spike - `ChatDirectory.cs` answered its question),
`cmd\mapscan` (superseded, parity confirmed 4,024 vs 4,017 on 1970), `cmd\lastwar-client`
(a shim), and `internal\sfs` / `crypto` / `gsl` / `session` / `testutil` / `app\config.go`.

### What breaks the moment the Go tree is deleted

Six harness scripts shell out to `lastwar-client.exe -interactive`:
`guestprobe.ps1`, `guestprobe_iso.ps1`, `oneshot.ps1`, `pushwatch.ps1`, `runcmds.ps1`,
`initdump.ps1`. Their replacement is the dashboard's runner: a persistent logged-in
session plus `POST /api/runners/{id}/call`, which is strictly better than the FIFO.
`buildpcap.ps1` and `initdump.ps1` rebuild from Go source. `env.ps1` puts the Go tree on
PATH. All six compiled binaries live INSIDE `lastwar-client\`, so deleting the folder
deletes them too.
## Operations: one registry, three front ends (2026-09-23)

The game automation layer is ported and everything the tooling can do now lives in
`LastWar.Operations` as a described operation, not as a script or a hand-wired page.

    lastwar ops                     list everything, grouped
    lastwar op --op base.buildings  run one; parameters come from the registry
    POST /api/runners/{id}/operation
    the dashboard's Operations tab

An operation declares its id, group, title, description, whether it needs a live session,
whether it CHANGES the account, and its parameters. Both front ends render from that, so
adding a capability is one entry in `Operations.cs` - no endpoint, no page, no flag parsing.
Every operation answers in one shape (a sentence, plus optional columns and rows), which is
why one table renders all of them.

Groups: Base, Alliance, Mail, Daily, Scan, Lua, Chat. `daily.all` is the old `-collect`
mega-flag: each step independent, a failure reported in its row rather than stopping the run.

### Three bugs this shook out, all the same shape

The inbound channel is a queue, and anything that READS it takes envelopes away from
whoever else is waiting. Three different symptoms, one cause:

1. The dashboard's push viewer drained `Pushes()` and stole every command reply - every
   call timed out.
2. Buildings and visitors are pushed once at login and never offered again. Reading them
   off the channel worked for the first operation and left the next one with an empty base.
3. Mail paging alternated 44 / 0 / 25 / 0 across runs. A page that timed out left its push
   in the queue, and the NEXT run consumed that stale empty page and stopped immediately.

The fix in all three is the same: observe, do not consume. `GameConnection.Frame` is a tap.
`LoginPushRecorder` attaches at connect - before the login request goes out, since the burst
starts arriving immediately - and keeps the buildings, the visitors AND the chat token for
the life of the connection. `PushWatch` opens before a request goes out and only sees frames
from that moment, so a leftover can never be read as this request's answer.

If a future operation needs to wait for a push, use `PushWatch`. Do not call `Pushes()`.

### The wire view

Frames are decoded in BOTH directions now and shown as JSON, with the hex dump behind a
toggle rather than as the only view. System frames (controller 0, the heartbeat) genuinely
carry no extension payload and say so.

### Scripts

`_retired\` holds the 86 python and PowerShell files the app now does itself, with a README
saying what replaced each. Two capabilities were python-only and are NOT ported:
`decode_occupy.py` (the occupy.info protobuf decoder) and `parse_safa.py`. `harness\buildpcap.ps1`
stayed - packet capture decoding is still the one Go-only capability.

## Activity check 2026-09-24 (1967)

Second power snapshot taken 2026-09-23 23:31Z (5,593 accounts) against the 22 Sep baseline
(5,581): 734 active, 4,304 flat, 542 down, 13 new, 1 gone. Output with alliance tags joined
from the map: data\activity\activity_1967_2026-09-24.csv. Now an operation: `activity.check`.

Two data faults found while doing it, both fixed for the next snapshot:

- The al.rank roster route stored alliance ID but never the TAG (al.rank does not return it;
  the tiles it was handed do). `PowerCollector` now stamps the tag from the tiles. For the two
  existing snapshots, tags have to come from a join on map_tile.
- `scan.power` swept the whole map and threw it away. It now stores the sweep, so map_tile
  stays as fresh as the power data. The latest 1967 map is still the 22 Sep one.

### Applicants, second capture 2026-09-24

Now an operation: `migration.applicants` (Migration group). Needs a resident account - run it
on Sadmin. Each run writes a NEW dated file, `data\migration\applicants_<server>_<utc>.csv`;
the 22 Sep `applicants_1967.csv` is untouched.

1967: 61 -> 74 applicants, 13 new, none gone, all still applyState 2. Five of the new are
AWKN from 1971 arriving as one block. The array key in the reply is still not pinned down;
the operation takes the first array of objects in the response.

### rank.get answers CROSS-SERVER (2026-09-24)

`rank.get {type, serverId}` returns the board for ANY server, from an account on another one -
checked on Sadmin (1967) against 1971, 1964 and 1978, each returning its own 200 rows with
matching srcServer. (`server.rank` is the one that ignores serverId, not rank.get.) So boards
need no resident account; `board.get` exposes it for any type and server range.

`migration.applicants` now adds `heroPower` + `thpRank` from each applicant's SOURCE server's
type-13 board. The board is the top 200, so a blank means "below that server's cut-off" (about
75M THP on 1964/1978), not a failed lookup - verified for 1978 by name as well as uid.
Applicant uids don't always end in the fromServer: some 1978 applicants carry 1972 and 1940
suffixes, i.e. they have migrated before.

## Wave baseline - how to collect 32 servers at once (2026-09-24)

The map view is level-gated for guests (`cross_server_view_tips level limit`) but the PLAYER
calls are not: `world.get.detail.new` and `al.rank` both answer a fresh guest cross-server.
So the job splits cleanly:

    1. Sadmin sweeps the maps:   op scan.map   --servers 1957-1988            (~1.2 min/server)
    2. N guests collect power:   op scan.power --servers <every Nth> --fromMap true
                                     --mapMaxAgeHours 2 --waitForMapMinutes 150 --config <guest>
    3. Sadmin saves boards:      op board.snapshot --servers 1957-1988 --types 13,4

`--waitForMapMinutes` lets collectors start alongside the sweep and pick each server up as its
map lands. Four guests take ~12 min per server each, so 32 servers is ~1.5 h instead of ~6.

Two rules that bit this time, both already written further up and both worth repeating:

- **A guest logs in ONCE.** A second login on the same guest is ec=28 E011. Guest creation is
  HTTP only (no game login), so create it and spend its one login on the real run - never on a
  test call first.
- **The gate is not absolute.** Guests that GSL happened to place on servers near the range
  (1943, 1952) were ALLOWED to sweep 1958/1959. Distant ones (32, 146, 610...) were refused.
  Not relied on - just noted.

### migration.compare

Follows every uid across a server range from before a cutoff to after it. The after side
uses ONLY data taken after the cutoff, and a server with no after-scan is reported by name
with its players marked "not rescanned" - never silently counted as "stayed". Statuses:
moved, stayed, gone, arrived (from outside the range), not rescanned. Views: moved, flows
(from -> to with counts, power and alliances), gained, lost, gone, arrived, all.

Hero power comes from `board_sample` (new table, `board.snapshot`), top 200 per server.

Timestamps compare as ISO text. All writers use the "O" format with +00:00, which orders
correctly; a legacy import in a different format could misorder within one second only.

### Baseline run 2026-09-24 - what actually happened

Every collector lost its connection after one or two servers of detail requests: first-round
guests, second-round guests, AND Sadmin (a transport timeout, not an ec code). Whether it is
per-connection lifetime or aggregate rate from one IP is not settled - five concurrent
collectors all died; one collector on its own (the first 1967 snapshot) and Sadmin's 40-minute
map sweep did not.

`scan.power` now:
- stops on a lost connection and lists what was never attempted, instead of failing every
  remaining server in milliseconds on the dead socket;
- RECONNECTS and redoes the server when the front end supplies a way to log in again. The CLI
  does. Sadmin reconnects fine; a guest cannot (E011), so a guest still just stops.
- takes `--gap` (ms between player requests, default 60).

Run the post-transfer round as ONE Sadmin collector with `--gap 100`, not a guest fleet.

### Correction and the safer route (2026-09-24 04:50Z)

The reconnect handler described above was NOT actually in the build that ran - multi-line
text replaces silently match nothing once a file has CRLF line endings, and my check matched
an unrelated line. It is in now, applied by line and verified by structure. Lesson: after an
edit, check the edited STRUCTURE, not that some pattern appears somewhere.

The single Sadmin collector then went silent mid-server (a TCP timeout - the server stopped
answering). That looks like throttling, and repeatedly re-logging a real account into a
server that has just gone quiet on it is how accounts get flagged. So the long collection now
runs on `scan.power.guests`: a fresh throwaway guest per attempt, never a real account.
PowerCollector takes an `into` dictionary, so an attempt that drops keeps what it fetched and
the next guest asks only about players still missing. Guest homes go under
data\sessions\auto and are deleted after use.

Use this for the post-transfer round too:
    op scan.map          --servers 1957-1988                      (Sadmin; ~40 min; never failed)
    op scan.power.guests --servers 1957-1988                      (guests; hours; resumable)
    op board.snapshot    --servers 1957-1988 --types 13,4          (Sadmin; a minute)

### Guest collector result so far (06:08Z): zero drops

One guest at a time, 100 ms between requests, 20 s between servers: 1965, 1969, 1970, 1971,
1973 all completed on their FIRST guest, no connection lost, ~16 min per server. Set against
five collectors dropping within one or two servers each, that points at aggregate request
rate from one machine rather than a per-connection lifetime. Keep collection single-file.

## Explorer (dashboard landing page) - 2026-09-24

Server, alliance and player pages over the whole store, every name a link, with one search
box for players / alliance tags / uids / server ids. Routes are hash links, so any page can
be bookmarked: #/x, #/x/server/1967, #/x/alliance/<allianceId>, #/x/player/<uid>.

Relationships: everything already joined on the player uid (map_tile.owner_uid,
power_sample.uid, board_sample.uid, chat_message.sender_uid, migration_applicant.uid) and on
alliance id / tag (map_tile.alliance_id, chat_message.sender_al, migration_applicant.abbr).
`LastWar.Storage.Insights` is the one place those joins are written; it adds the indexes
they need (ix_power_uid, ix_tile_alliance, ix_scan_server, ix_chat_al). Origin server comes
from the uid's last six digits and does not change on transfer.

Applicants are now in the database (`migration_applicant`). The host imports every
data\migration\applicants_*.csv at startup (idempotent) and `migration.applicants` writes
each new capture straight in.

Two front-end rules this surfaced:
- a page must only ever render the data fetched for ITS route - on navigation React renders
  the new page once with the old payload before the fetch effect runs. Data is keyed to the
  route; an error boundary stops one failing page from blanking the dashboard.
- chart palette on the dashboard surface (#161b22) validated with the dataviz validator:
  blue #3987e5 / orange #d95926 / aqua #199e70 pass every check in dark mode.

### Server page: full player list (2026-09-24)

`GET /api/x/server/{id}/players` - every player on the server's newest map with power, change,
hero power, HQ and origin (~200 ms for 5.5k). The page's "All players" card filters by origin
(here / elsewhere / a specific server), alliance and name, and copies the filtered set as CSV.
Clicking an origin bar or the "Created elsewhere" tile applies that filter and scrolls to it.
"Created elsewhere" means transferred in at ANY time - the uid keeps its creation server - so
on 1967 it is 1,292 players from earlier waves, before this one has run.

Table fix: numeric headers were left-aligned over right-aligned values (only td.num was
styled), which read as every column being pushed over. th.num now aligns right too.

## LIVE CHAT IN THE DASHBOARD - 2026-09-24

Chat tab (web/src/panels/Chat.jsx) over ChatHub (LastWar.Server/ChatHub.cs) and
ChatListener (LastWar.Client/Chat/ChatListener.cs).

- ChatListener is the ONLY reader of a ChatConnection. Pushes go to a callback; replies
  to requests are routed back by command name. Requests are serialized (replies carry
  no correlation id), so a poll and a catch-up never take each other's answer.
- World chat is readable CROSS-SERVER: a guest on server 832 reads country_1967 and
  country_1972 fine (signed history.roomsv2). Pushes only come for joined rooms, so
  the hub POLLS followed rooms every 8s - one history.roomsv2 with a map of up to 10
  rooms per request.
- Followed rooms persist in data/chat/follow.json. Following a room that already has
  stored messages catches up from the newest stored seq first; the poll skips the room
  meanwhile (otherwise the newest block lands first and the hole is never seen).
  Hole filler: POST /api/chat/rooms/{room}/catchup?after=<seq>.
- Storage goes through ChatStore.InsertNew (INSERT OR IGNORE, per-row result), so
  push/poll/catch-up duplicates are stored and shown once. Existing rows never change.
  Insert() no longer does two COUNT(*) scans per batch.
- Seq ids are NOT contiguous (country_1967: ~39k messages over a ~41k seq span), so
  "gap = missing seq" detection does not work. Don't build on it.
- Empty-text messages from sender "system" are chat GIFTS: extra.customJsonParam is a
  JSON string with sendName/sendUid/targetInfo{name,uid}/num/isAnonymous.
  "<lwSticker:a:b:>" text is a sticker.
- Use a GUEST to go live ("Go live on a fresh guest" button). Guests log in once only
  (E011 on reuse), so each server restart needs a fresh guest; old guest runners are
  left in data/runners. Starting any runner from the dashboard auto-attaches chat;
  non-guest accounts also auto-follow their own country_<server>.
- API: GET /api/chat/status, /api/chat/rooms, /api/chat/rooms/{room}/messages?before=&limit=,
  POST /api/chat/follow {room,on}, POST /api/runners/{id}/chat/start|stop,
  GET /api/chat/live (SSE, all rooms, only NEW messages).

## CHAT TRANSLATION + SENDER SERVERS - 2026-09-24

### Translation (the game's own service, plain HTTPS)
    POST https://lastwar-us-translate.lastwargame.com/api/translate/context
         (fallback https://lastwar-api-translation.lastwargame.com/)
    { tf:"en", ch:"lastwar", t:"<unix s>", uid, sig, sf:<extra.srcLang>, tk:"", ul:"",
      ign_bad:0, sid:"<server>", messages:[{id:"<seq>", text, translate:true|false,
      sender:<uid>, role:<name>, time}] }
    sig = lower(md5(sf + tf + ch + t + tk + uid))
    -> { code:0, translations:[{ id, translateMsg, originalLang, targetLang, model, hit }] }
- From Chat/Controller/TranslateManager (BuildContextRequestBase / RequestContextTranslate)
  and ChatService.RequestContextTranslate (a plain PostJson).
- tk = player.translateKey in the client. It is NOT in the init push or any early frame,
  and "" works fine. Don't go looking for it again.
- messages with translate=false are context (the client sends up to 20 / 4000 chars); only
  translate=true ones come back. The service is an LLM ("c-gpt-...") with a cache.
- Stored in chat_translation(room_id, seq_id, lang, text, source_lang, at); chat_message is
  never modified. API: POST /api/chat/rooms/{room}/messages/{seq}/translate?lang=en

### Who is speaking - get.user.info.multi (game socket, cross-server from a guest)
    get.user.info.multi { allservers:"1", uids:[...] } -> { uids:[{ uid, name, serverId,
      srcServer, currentServer, crossFightSrcServerId?, allianceAbbrName, power, level,
      armyPower, armyKill, lang, country, ... }] }
- What the game prints next to a name in chat = crossFightSrcServerId when > 0, else
  serverId (Chat/Model/ChatUserInfo). That follows a player who teleports over for a war;
  the uid suffix (creation server) does NOT - e.g. a 1972 chatter created on 1958 shows
  serverId 1972, srcServer 1972.
- Command id lives in Chat/WebMessage/Config/ChatMsgDefines ("GetUserInfoMulti"), but it is
  an SFS command on the GAME socket, not the chat gateway.
- ChatHub looks senders up in batches of 50 per poll via the live runner's call lock, keeps
  6h freshness, stores every lookup as history in chat_user(uid, fetched_at, ...), and
  pushes an SSE "users" event so names on screen gain their server.

### Fixed along the way
- LastWarClient.WaitForChatCredentialsAsync used to read Pushes() directly - the
  reply-stealing mistake. It now waits on LoginPushRecorder's tap.
- JsonNode.ToString() on a string node KEEPS the quotes; use GetValue<string>().

## TRANSFER DAY - HOME SERVER IS A PROFILE FACT, NOT A MAP FACT (2026-09-24)

Three different servers, never to be confused:
- HOME ("from") = profile serverId, or crossFightSrcServerId when set. Changes on a TRANSFER.
- BASED ("current") = profile currentServer / the map a base sits on. Changes on a weekend or
  season teleport to fight. A map sighting on another server is NOT a move.
- ORIGIN = uid suffix, the creation server. Never changes.

### scan.profiles (the profile sweep)
    lastwar op --op scan.profiles --servers 1957-1988        (fresh guests only)
- Asks get.user.info.multi about EVERY uid ever tied to the range (map tiles, power and board
  samples, applicants, earlier profiles) - so people who left are still asked, and a move out
  of the range shows as a move, not a disappearance.
- 1,000 uids per request (2,000 answered in <300 ms), 1 s gap: ~178k players in ~4 min.
- Every row stamped with the sweep's start time -> a sweep is a snapshot.
  Table player_profile(uid, taken_at, home_server, server_id, current_server, src_server,
  cross_src_server, alliance_*, power, army_power, army_kill, level, lang, country, source),
  PK (uid, taken_at), append-only. source = 'sweep' | 'chat'. (chat_user was merged in and
  dropped.)
- Also writes power_sample rows (source Profile) filed under HOME server, for home servers
  INSIDE the range only (a lone out-of-range player must not become another server's
  "latest snapshot"). Profile power == detail power (500 checked, 357 identical).
- The first call on a brand-new guest sometimes times out; the op moves to a new guest.

### Nothing historical is overwritten
map_scan/map_tile, power_sample, board_sample, player_profile, migration_applicant are all
keyed by time and append-only. Server strength over time = power_sample grouped by
(server_id, taken_at) - old snapshots keep their server even after players leave.

### migration.compare now
- Home server from the newest profile in each window; map + power only for players with no
  profile in the window. A sweep in a window covers the whole range.
- show = moved | flows | servers | alliances | gained | lost | gone | arrived | stayed |
  visiting | all. "servers" = per-server before/after players & power, who left/arrived and
  with how much power. "alliances" = per alliance id: members & power before/after, left it,
  joined it, moved server.
- "based on now" column / "visiting" status: base is on another server but home unchanged.

### Transfer-day runbook (repeat for every future window)
1. BEFORE (done 2026-09-24 ~19:30Z): scan.profiles 1957-1988 = the authoritative "from".
2. After the transfer completes:
   a. scan.profiles --servers 1957-1988   (minutes; who is from where now + power)
   b. scan.map for 1957-1988 (Sadmin as before - guests cannot view other maps) to find
      ARRIVALS from outside the range, then scan.profiles again so they get profiled.
   c. board.snapshot for hero power.
3. migration.compare --cutoff <moment between the two sweeps> --show flows | servers |
   alliances | moved.

## BOARD SWEEP + COMPARE (2026-09-24)

### board.snapshot now takes every useful board
Default types 1,2,3,4,5,13,14,15,18,19,20 (Boards.cs is the catalogue). Rows now also carry
alliance_id, src_server (crossFightSrcServerId when set) and extra ("nums=89;armyKill=..."),
added as columns with defaults - older rows untouched. Alliance boards (1, 2) store the
ALLIANCE id in uid. Numbers go through Scalar: armyKill is a STRING on alliance boards.
Board 15's sort key is "power" - heroHighestPower is MISSING on ~3% of rows (a first pass
stored those as 0; that snapshot was deleted and retaken at 19:02Z).
Pre-transfer sweep: 1957-1988 all 11 boards at 2026-09-24 18:55:43Z (board 15 at 19:02:42Z),
35,200 + 3,200 rows, no gaps. Run on a guest (rank.get is cross-server).

### Explorer compare - #/x/compare/{servers|alliances|players}/{a,b,c,d}
API /api/x/compare/{kind}?ids=... (max 4). Same shape for every kind: entities + metrics, each
metric a time series per entity; the page shows latest values (best marked, change vs previous
snapshot) and charts the picked one on ONE shared axis.
- servers: accounts & total power (power snapshots); per board 4/13/3/15: top-N total, #1,
  #N entry bar, "risen since last board" (ACTIVITY - count of top-N players whose value went up
  between consecutive boards); PvE #1/#100 (decoded stage); top-10 alliance power/kills; top
  alliances on the newest alliance board.
- alliances: members / member power / avg / kills per power snapshot; alliance board power,
  rank, kills; members in the server top 200 power and hero power (needs board alliance_id,
  so only from this sweep on).
- players: power, kills, value + rank on every player board, home/based/origin.
Every entity page links "compare with others".

Chart palette for 4 series: #3987e5 #d95926 #199e70 #c98500 - validated on #161b22.
ticks() used to stop at the last tick BELOW the max, so the top points sat outside the plot;
fixed (it now runs to a tick at or above the max).

## TRANSFER DAY LIVE - migration.leavers (2026-09-25)
    lastwar op --op migration.leavers --server 1967 [--baseline "2026-09-25 00:00"]
Profile-sweeps one server (fresh guest, ~20 s incl. login) and lists who was homed there at the
baseline and is homed elsewhere now (with destination, alliance before/after, power), plus KNOWN
players who arrived. Each run is stored as a normal sweep, so repeating it through the day builds
a timeline; the baseline stays fixed. Unknown arrivals need a map scan of the server first.
- Gotcha: a request sent straight after login is never answered (every first call timed out on
  transfer morning). scan.profiles now waits for the login burst (LoginPushRecorder.WaitAsync)
  plus 3 s before its first request.
- First run 06:29Z: 16 left 1967 (2.18B), 20 known arrivals.

## NAME CHANGES (2026-09-25)
name_change(kind player|alliance, id, field name|tag, old_value, new_value, last_seen_old,
first_seen_new = detection time, source). DERIVED: NameHistory.Rebuild() walks every timed
observation (map tiles, power, boards, profiles, chat senders, applicants) per id in time order
and logs each change; rebuilt from scratch (~7 s) so late-arriving older data (chat backfill)
lands in the right place. Observations themselves are never modified.
Runs after every scan.*/board.*/chat.backfill/migration.applicants op (Operations.RunAsync),
at server start and hourly (live chat). Op: names.changes. API: /api/x/names?kind=&q=&limit=.
Player/alliance pages show the history; search also finds people by an old name; a player's
displayed name is the newest from any source. First build: 417 changes.

## SERVER HISTORY + ALLIANCE MEMBERSHIP (2026-09-25) - both DERIVED, rebuilt with names
Derived.RebuildAll = NameHistory + ServerHistory + AllianceMembership (~15 s), after every
scan/board/backfill op, at server start and hourly. Observations are never modified.

server_history(uid, seq, kind, from_server, to_server, from_at, to_at, estimated, evidence):
- HOME evidence: profile home_server, board src_server, applicant from_server.
  BASED evidence: map tiles, non-Profile power rows, chat in country_<n> rooms.
- created (uid suffix, no date) | early (first known home != creation; dated 2025-11-01,
  estimated=1 - game rebalanced early players, or an older window) | transfer (home A->B,
  from_at = last seen at A, to_at = first seen at B; base sightings in the gap tighten both) |
  visit (base on X while home is the SAME on both sides of the sighting; first..last seen).
- A base sighting after the last home evidence is not judged until the next sweep.
- First build: 177,832 created, 44,706 early, 65 transfers.
Op: history.servers. Player page "Server history".

alliance_stint(uid, seq, alliance_id, tag, first_seen, last_seen, left_at, joined_known,
next_alliance_id, next_tag): unbroken runs in one alliance from map/power/profile/board.
Empty id = "no alliance" EXCEPT board rows and early AllianceRoster power rows, where it
only means not recorded; a tag with no id is skipped (migrated chat profiles).
Alliance page roster = open stints only (members since, "before tracking" when the stint
starts at the player's first sighting); "Past members" = closed stints with left date and
where they went. Player page profile table collapses unchanged sweeps.

## MAP VIEW + DENSITY (2026-09-25)
- #/x/map/{server}[/{x},{y}[,{uid}]][?scan=N] - canvas map of one scan's bases. Tile index
  = x + y*1000 + 1 (point_id), so x = (p-1)%1000, y = (p-1)/1000; y drawn DOWNWARD (game
  orientation not verified - flip if it looks mirrored against the game). Bases drawn 2x2.
  Pan by drag, zoom by wheel/pinch/double-click, grid 100/10/1 by zoom, top-8 alliances get
  the 8 validated slot colours, click a base -> player. API /api/x/map/{server}?scan=.
- Player page: "on the map at x:y" link (newest map sighting), map-sighting rows link to the spot.
- Player page tables collapse unchanged consecutive rows (profiles, map sightings); boards
  show one row per board with rank movement. Alliance compared by id when both rows have one,
  else by tag (early chat profiles carry only the tag - that was the "duplicates").
- A bad string Replace had put the Server-history card INSIDE the hero card; rewritten.
- .cards = auto-fill columns (min 520px) - 3 across at 1920. >=1400px tightens padding.

## MAP SCAN IS 100x FASTER - BLOCK SIZE (2026-09-25)
- world.get.block honours whatever `blockSize` is sent. We sent 4 (the pre-init client field);
  the client's real table is LWAoiBlockSizeArray = {10, 20, 1000} (Global/ConstDefine.luac).
- scan.map.bench on a guest's own server (1465, 3,425 bases, IDENTICAL base set every run):
    4 = 391 req / 35 s | 10 = 63 / 5.6 s | 20 = 16 / 1.5 s | 50 = 3 / 0.5 s | 100 = 1 / 0.3 s
    1000 = nothing back (server cap somewhere between 100 and 1000).
  Pipelining (MapScanner.InFlight, replies echo `_id`) barely helps - 10x4 4.9 s vs 10x1 5.6 s -
  because the send gap dominates. Default stays InFlight=1.
- Default is now blockSize 50 (MapScanOptions, scan.map param). A 32-server sweep is ~100 requests
  in total instead of ~12,500 - also far less load on Sadmin, who still has to run it (guests cannot
  view other servers' maps).
- Timed-out batches are now retried (MapScanner.Retries=2) instead of silently skipped;
  LastMissedBatches reports any still lost.
- Rerun `op --op scan.map.bench` (guest, its own server) if a game update changes this.

## REFRESH = TWO OPERATIONS (2026-09-25)
- refresh.stats  (guests only, no session): board.snapshot on a throwaway guest, then scan.profiles.
  Profiles give power, alliance, home/based server for EVERY known player, so the per-tile detail
  sweep (scan.power / scan.power.guests, hours) is no longer needed for stats.
- refresh.all    scan.map on the session account (Sadmin), then the same as refresh.stats. The map
  first so new arrivals become known uids for the profile sweep.
- Steps run through StepAsync: failures are recorded not thrown, and Derived.RebuildAll runs ONCE
  at the end. The "Refresh" group is listed first on the Operations page.
- Measured 2026-09-25, 1957-1988: board.snapshot 127 s (352 boards), scan.profiles 227 s
  (177,806 players, 0 missed), history rebuild after. ~6 min end to end, zero drops.

## UI (2026-09-25)
- allianceColour(id) in explorer/format.js: FNV-1a hash of the alliance ID -> HSL. Used by Tag
  (left stripe via --al), map bases/legend, server alliance bars, alliance comparisons, chat tags.
  Hash the ID, never the tag.
- Search lives in the top banner (explorer/Search.jsx) on every tab. "/" focuses, Enter opens the
  first hit. Header has z-index 30 so results sit above page content.

## MIGRATION REPORT BETWEEN TWO DATES (2026-09-25)
- migration.compare now takes `until`: after side = newest data in (cutoff, until]. Before side = newest
  data <= cutoff. Mid-transfer scans are safe: a later scan inside the window simply supersedes them.
- Sept 2026 window: cutoff "2026-09-24 19:05" (after the 18:30 profile + 18:55 board baseline).
  Mid-transfer check at 2026-09-25 10:46 sweep: 993 moved, 54 arrived. Final report = rerun
  refresh.stats after tonight's reset, then pin `until` to just after that sweep.
- Choosing a cutoff AFTER a mid-transfer scan would make half-moved players look like "before" -
  always cut at the pre-transfer baseline.

## GUEST SESSIONS ARE SINGLE-USE - CLEANED UP AUTOMATICALLY (2026-09-25)
- Verified on stored guests: a second login is refused (chat_tr1, boards_0924: ec=28 E011; test: E013).
  A guest runner that has been started is spent.
- Stop on a guest runner (Runner.Spent = kind guest + login attempted) deletes its spec and folder.
  A guest whose start is refused (LoginFailedException) is deleted too.
- Backstop: the hourly server loop (also runs at startup) calls SessionStore.Cleanup(minAge 1h).
- Sessions page "Clean up unused guests..." = POST /api/sessions/cleanup?apply=false (preview), then
  apply=true. minAge 0, but sessions\auto still needs 2 h (running operations use it).
- NEVER removed: specs not kind "guest" (default = Sadmin, kind external, lives in the user profile),
  any folder outside data\sessions, any session file with a gameUid, anything running / chatting /
  mid email sign-in. Unreadable session file = kept.
- First run removed 27 dead guest sessions (chat_*, boards_*, probe, test, wave*, auto leftovers).

## REFRESH SPLIT + RUNNER PICKER (2026-09-25)
- refresh.all is gone. refresh.stats = everything guests can do (boards + profiles, no runner).
  refresh.map = scan.map only, needs a REAL account (cross-server map view is level-gated).
- Runner selection moved out of the top bar into RunnerPicker (Commands, Raw TCP with Start/Stop;
  Operations shows it only for ops that need a session).
- POST /api/runners/{id}/operation on a STOPPED runner for a session op: starts it, SettleAsync
  (login burst + 3 s), runs the op, stops it. No chat attach. One-time run for a real account.
  A guest used this way is spent and deleted after. HolderOf still blocks a second login.

## ACTIVITY = A RISE IN THE LAST 7 DAYS (2026-09-25)
- Flaw: "active" compared only the two newest snapshots, so two sweeps an hour apart read ~1%
  (1967: 51 of 5,662). Now: a rise between ANY two consecutive readings of a player, with the later
  reading inside 7 days of the snapshot being looked at. 1967 now 1,319 of 5,485 (24%).
- player_activity (PlayerActivity.cs, derived, part of Derived.RebuildAll, ~3 s for 757k rows):
  uid, samples, last_at, last_rise_at. One reading per moment (MAX power per uid+taken_at).
  Window SQL: PlayerActivity.WindowStart(iso) = strftime('%Y-%m-%dT%H:%M:%S', iso, '-7 days'),
  compared as text against the "O"-format timestamps.
- Used by: server page + alliance rows (relative to the snapshot), overview (per server's newest
  snapshot), alliance page + player page (relative to now), activity.check (days param) and the CLI
  `activity` verb (ActivityAnalyzer.Compare(history, window)). Rows carry active7 / lastRise; the UI
  statusOf(now, prev, active7) shows active when active7 = 1. Change columns stay last-snapshot.
- Compared = 2+ readings with one inside the window.

## LIVE MIGRATION REPORT - POWER, HERO POWER, TOTALS (2026-09-25)
- migration.leavers tables add "power" (now) and "hero power"; both in the default columns.
- Hero power only exists on the hero board (type 13, top 200) and transfer applications:
  WaveStore.LatestHeroPower(uids) takes the newest of either. 1967 run: 19 of 30 leavers, 16 of 36
  joiners known - the footer says "N of M known".
- OperationTable.Totals = columns summed into a footer (Ops page tfoot, CLI "TOTAL" row). Blank
  cells are skipped. Summed columns render with thousands separators.

## HOME SERVER ON TELEPORT DAY = srcServer (2026-09-26) - FIXED A WRONG RULE
- Teleports for server wars: serverId AND currentServer follow the base, crossFightSrcServerId stays 0,
  srcServer keeps the real home. e.g. 1410633061001967 Silent Trigger (SaFa): serverId 2010, src 1967.
  The old rule (cross > 0 ? cross : serverId) logged these as TRANSFERS.
- Transfers move srcServer too (girlsgirl 1979 -> 1967 changed both). Pre-teleport sweep: src == serverId
  for 177,783 of 177,810; the 27 others were parked bases whose alliances belong to srcServer.
- Rule now: ProfileLookup.HomeOf = crossFightSrc > 0 ? crossFightSrc : srcServer > 0 ? srcServer : serverId.
- Stored rows: one-off ProfileStore.FixHomeServers (runs from EnsureSchema, marker schema_fix
  'profile_home_src'). 92 rows re-derived; old/new recorded in profile_home_fix; matching Profile power
  rows refiled. Raw server_id/src_server/cross_src_server never touched. Derived histories rebuilt.
- migration.report (Migration group): all servers in the range, one table from -> to, alliance before/now,
  power before/now, hero power, based now, applied to, bracket times; totals; teleporters counted
  separately and NOT as moves. Fresh guest sweep first unless sweep=false. Ops tables now also have
  "Download CSV" (every column, UTF-8 BOM for Excel).

## RANKINGS PAGE (2026-09-26)
- #/x/rankings/<alliances|players|servers>/<metric>?at=&vs=&server=&limit=&chart=a,b ; API /api/x/rankings.
  Linked from every Explorer page header ("rankings across all servers").
- Insights.RankMetrics is the catalogue: source "profile" (full sweeps, >50k rows, every player by HOME
  server) or "board" (leaderboard snapshots covering at least half the range). Alliance hero power =
  sum over members on hero boards (top 200/server - undercounts small alliances). Battlefield boards
  (18-20) and HQ race (5) are not offered: no comparable value.
- Default comparison = newest snapshot 20h+ before "as of" (else oldest); "none" turns it off.
  Rank = ROW_NUMBER over the whole range; "new" = not ranked in the compared snapshot.
- Charts (here and Compare) use the 4 series slots, not alliance hash colours - ZRL/PEC/NDEV all
  hashed to green. Alliance colour stays on the tags.
- ~2 s for profile measures (5 sweeps x 178k rows), ~0.3 s for boards.

## BOARD ALLIANCE BACKFILL (2026-09-26)
- The 2026-09-24 02:21 board snapshots (types 4 and 13) predate the alliance_id/tag columns, so every
  alliance hero-power ranking compared against them showed "new" for all rows.
- WaveStore.FillOldBoardAlliances (one-off, schema_fix 'board_alliance_fill', runs from EnsureSchema,
  which Insights.EnsureSchema now calls at server start along with ProfileStore's): only snapshots
  where EVERY row is blank; alliance from the nearest map sweep within 12 h (same night), else the
  nearest power reading. 12,712 of 12,800 rows filled (12,705 map, 7 power); audit in
  board_alliance_fill. Values/ranks untouched.
- Every other ranking already had comparable history (checked all entity/metric pairs).

## NAVIGATION / URLS (2026-09-26) - web/src/nav.js
- Every tab is a URL section: #/x (Explorer) #/chat/<room> #/ops/<op>?<non-default args>
  #/commands/<command> #/fleet #/sessions #/wire #/data. App derives the tab from the hash; clicking a
  tab returns to that tab's last address (clicking the current tab goes to its start).
- Path = where you are (a history entry). Query = view state on that page, written with
  history.replaceState (useHashParam / replaceQuery) so filters never flood Back: server page
  ?origin=, player list ?alliance=&name=, overview ?names=. Map/rankings queries are data (fetched).
- Explorer: sub-nav Servers / Rankings / Compare; breadcrumbs All servers > #server > alliance;
  page links (map, server page, player/alliance rankings, compare). document.title per page.
- The page scrolls inside <main>, NOT the window - window.scrollTo never did anything. Explorer
  saves main.scrollTop per route in the hashchange handler (before the loading state collapses the
  page) and restores it, re-applying as late content loads; main has overflow-anchor: none.

## ACCOUNT TIERS - guest / service / privileged (2026-09-26)
- AccountTier (Operation.cs). Guest = throwaway; Service = generic work account (Sadmin now);
  Privileged = a player's own main (alliance access) - used ONLY by operations that list it.
- Operations.AccountPolicy (Operations.cs) is the whole policy in one table. Default for a session op
  = guest|service. Service only: scan.map, scan.power, refresh.map, migration.applicants, kingdom.queue.
  Service|privileged: base.*, alliance.help/gifts/tech, mail.*, vip.*, visitors.greet, daily.all.
  Privileged is never a default. AccountPolicyTests pins this (no scan may accept privileged).
- RunnerSpec.Tier (data/runners/*.json "Tier"); unset -> guest for guests, PRIVILEGED for email
  sign-ins (kind account), service otherwise. Sessions page sets it (PUT /api/runners/{id}/tier).
- Enforced server-side before anything connects (operation endpoint 403 with the reason) and in the
  CLI `op` verb (tier from the runner spec owning --config, --tier overrides). Privileged: no chat
  auto-attach on start, chat/start refused, every raw /call needs confirm=true (UI tick per call).
- 2026-09-26 13:38Z: an untested curl of scan.map on "default" really ran - Sadmin logged in once,
  swept 1967 (5,496 bases in ~3 s at blockSize 50 - confirms the fast map cross-server), logged out.
  Test policy refusals on GUEST runners only.

## MINISTRY / KINGDOM POSITIONS (2026-09-26)
- get.kingdom.positions {serverId} - GUEST, cross-server: 7 holders per server (10001 = President,
  GovernmentConst.King_Position_id; 10002-10007 ministers - in-game names not in the Lua, shown as
  ids). Fields: uid, name, abbr, allianceId, power, appointTime (ms), srcServer, careerLevel.
  get.king.info {serverId, toInt:0} - president + occupying alliance.
- kingdom.position.appointment.list / apply.list / history.list {positionId}: answer about the
  CALLER'S OWN server only (serverId is ignored - a guest on 113 got 113's history). So the queue
  for 1967 needs an account living on 1967 -> op kingdom.queue is service-only. history = last 100
  holders with appointTime/fireTime. 10001 has no queue ("positionIds error").
- Ops: kingdom.positions (guest, stores kingdom_position snapshots; 32 servers, 224 positions),
  kingdom.queue (service, generic columns until entries are seen), kingdom.probe (raw dump, research).

## Game assets (assets.extract) - 2026-09-26
- Source: `apk\split_install_time_pack.apk`. `lastwar op --op assets.extract` writes `data/assets/`:
  `locale/en.json` (from `assets/locale/23545/en.bin`: gzip, int32 header, then BinaryReader strings key/value),
  `tables/*.json` (from `assets/table/table_39280_*.data`: zip of 1,291 Lua 5.3 constant chunks
  `{index={col={n,type}}, data={[id]={...}}}`), `mail_types.json` (MailEnum MailType, 148 entries),
  `reward_types.json` (Global__EnumType RewardType), `lwemoji.ttf` (CBDT colour emoji font).
- `LastWar.Storage.GameText` reads them: T(dialogId,args), ItemName, PositionName, MailTemplate,
  StickerName, MailTypeName, Reward. Most `goods` rows have NO name column: the goods id itself is
  the locale key (200411 -> "Gun Magazine").
- Chat emojis are Private Use Area chars U+E001..; the web app loads lwemoji.ttf as font "LWEmoji"
  (unicode-range U+E000-F8FF, first in the body stack). Chromium renders the CBDT glyphs in colour.
- Stickers `<lwSticker:id:param:>`: names from table `sticker` (`/api/game/stickers`); 5 = Dice,
  param = the roll. Animated sticker art lives in Unity bundles inside BundleFragment0.bytes (539 MB);
  not extracted yet - needs a Unity bundle reader (e.g. AssetsTools.NET, a new NuGet dependency).

## Mail - 2026-09-26
- MailType 45 = PRESIDENT_SEND; 2 SYSTEM, 3 SYSTEM_EXTRA, 20 ALLIANCE_ALL, 22 USER ... (mail_types.json).
- Title/contents are JSON naming dialog ids; `MailFormat.View` resolves them (mailId -> mail table template).
- Single claim = the client's MailRewardMessage: `mail.reward {uid}`. Replies E100147
  "mail reward cann't receive again" when already taken. Batch = `mail.reward.batch {uids,type}`
  (success:true even when nothing is claimable). Mark read = `mail.read.status.betch {uids}`.
- The list (`chat.get.system.mails` -> push) keeps rewardStatus=0 / status=0 after a claim within
  the same login, so claim state from the list LAGS; trust the claim reply.
- Web: #/mail (Mail tab) - runner picker, type filter, expand for content/attachments/raw, per-mail
  Claim (confirm prompt on privileged), mark read. API: GET /api/runners/{id}/mail,
  POST /mail/{uid}/claim, POST /mail/read {uids}. Tested on guests only.
- Guests: restarting the server stops their runners; they are single-use, so run sessions cleanup after.

## Ministry queue (kingdom.queue) - 2026-09-26, run on Sadmin (service) at Henry's request
- 1967: 33 queued (10005: 8, 10006: 11, 10007: 14; 10002-10004 empty), slots every 5 min,
  0 applications, 600 history rows. Queue/apply/history only answer for the caller's own server.


## Player current values - 2026-09-26
- Player page and search take server/alliance from the NEWEST reading of any source (map sighting, power_sample, profile), not the map alone; history keeps the old ones. TileDetail power samples never carry an alliance, so an empty alliance there is ignored. Breadcrumb uses the home server.


## Search: player_latest / alliance_latest - 2026-09-26
- Derived tables (PlayerLatest.Rebuild, part of Derived.RebuildAll: startup, hourly, after scans; ~12 s). Name/alliance = newest of map sighting, AllianceRoster sample, profile (ties -> profile). SERVER = newest profile home_server (Henry: search shows the server they are FROM, never where the base sits); map server only when no profile exists. alliance_latest.server = home server most members share.
- Search reads only these tables: ~35-200 ms (was ~5.7 s scanning map_tile). Players known only from profiles are now findable. UI shows 'Searching...' and aborts stale requests.


## Chat stats - 2026-09-26
- #/chat/stats?days=7|30|90|0 (Chat sub-tab); API GET /api/chat/stats?days= (Insights.ChatStats, all stored rooms, ~0.4 s for 51k msgs).
- Emoji kinds: game = PUA U+E000-F8FF (lwemoji.ttf); unicode = block ranges, regional-indicator PAIRS counted as one flag, skin tones (1F3FB-1F3FF) not counted alone; stickers = whole message <lwSticker:id:param:>, id 5 = dice, param = roll (7,094 rolls, average 3.50 - fair).
- sender_uid 'system' (gift notices) counts in volume, excluded from people lists. Empty chat names/tags filled from player_latest.
- Rooms are held over different spans (1967 since 7 Jul, others since followed): compare by 'per day held'. Hours are UTC.


## Guest intel exploration - 2026-09-27 (all on throwaway guests)
- Tool: `lastwar op --op probe.run --set <name> [--server N --uid U --alliance A]` runs data/probes/<name>.json
  on a new guest, typed from data/api/commands.csv, saves replies + pushes to data/raw/probe_<name>_<ts>.json.
  Sets so far: player, alliance, server, lastupdate, inactive.
- **get.user.info.multi lastUpdateTime** (unix s) = when the game last refreshed the player = LAST SEEN.
  Validated: 100 players whose power never rose -> 1 h..14 days old; 100 who rose that day -> mostly < 12 h.
  Online players refresh about every 2 h. Stored as player_profile.last_seen_at (+ svip_level,
  month_card_end = paying if in the future, thumbs_up). player_activity.last_seen_at / last_active_at
  (= max(rise, seen)); all "active" views now use last_active_at.
- **al.rank {allianceId}** works for ANY alliance: per member rank R1-5, power, kills, HQ, todayProgress /
  weeklyProgress (= tech donation points, Lua donateTodayProgress), donateTime (last donation, ms),
  weeklyDonateTime, joinTime, rankTime, monthCardEndTime, curServerId; allianceOfficialArr (types 1-4,
  probably Warlord/Recruiter/Muse/Butler = strings 391063-6); groupDescription = custom rank names.
  online/offLineTime are MASKED for outsiders (all online=true).
- **get.al.info**: fightPower, allianceEngagementPoint, giftLevel, createTime, createServer, language,
  leader, joinApproval, levelLimit, powerLimit, announce (often lists allies).
- **city.war.get.info {serverId}**: 85 cities per server: holder alId/alAbbr, firstOccupyTime, protectTime.
  (world.get.alliance.city.info returns the same as base64 protobuf - not needed.)
- **season.migrate.server.info**: serverState, serverScore, president + first lady, migration inflow by tier.
- **get.history.king.record {serverId,page}**: every president, 10/page (22 terms on 1957-1988).
- Also answer on a guest, not stored yet: weapon.info.view {uid,serverId} (drone lv/power/chip lv),
  get.new.user.info {uid,...} (careerLv, allianceRank, badge; battleWin/Lose/armyDead read 0 = hidden),
  server.rank / kill.rank {ismerge:0} (global top 200 power / top 100 kills), alliance.rank / kill.al.rank
  (global top alliances), server.badges.get (war-zone groupings + their king), lw.season.trend.info,
  get.season.city.info, world.get.city.stronghold.info (protobuf), get.cross.server.king.info,
  get.server.zone.completeness, social.status.view.
- Refused/empty on a guest: arena/gale/score arena previews ("owner is null"), champion.duel.* (empty),
  season tower, alliance boss, zombie rush, lw.camp.* (no act), player.rank/player.info (no reply).
- **intel.sweep** (guest, part of refresh.stats): cities + state + presidents per server, get.al.info +
  al.rank for every alliance with >= 10 members (966 on 1957-1988, ~6 min). Tables: alliance_member_sample,
  alliance_official, alliance_info_sample, city_sample, server_state_sample, president (IntelStore).
- Views: /api/x/alliance/{id}/intel, /api/x/server/{id}/intel, /api/x/matchup?a=&b= (Compare two
  alliances). Matchup model: only members seen/donated in 3 days; attack = attacker top-10 vs defender
  top-20 at half weight, defender +10%; chance = 1/(1+(def/att)^3). Heuristic, stated on the page.
- For later, needs the account's OWN alliance (service/privileged, not explored): view.alliance.log,
  real online/offLineTime in al.rank, lw.get.alliance.alert.info / get.alliance.alert.march (incoming
  attacks), alliance.team.ls (rallies), battle-report mail, world.march.simulate (server-side battle sim
  with own formations), al.member.point.get, alliance.declare.war.get.


## Intel, part 2 - 2026-09-27
- intel.sweep now also stores: world.get.city.stronghold.info per server (base64 protobuf, repeated
  field 1 = {1 id, 2 tag, 3 allianceId, 4 level, 6 name, 7 server}; LastWar.Client.Game.Proto reads it)
  -> stronghold_sample; get.cross.server.king.info {serverIds:"a,b,.."} (20 a request) -> server_card_sample
  (serverInfo.createTime = when the server's current badge was awarded - NOT its opening date; + current president); server.rank / kill.rank / alliance.rank / kill.al.rank
  {ismerge:0} -> global_rank_sample (whole-game boards: 200 / 100 players, ~29 alliances).
- intel.players (guest): weapon.info.view {uid, serverId} (drone lv, power, chip lv, props) for the top N
  per server by newest profile power -> player_detail_sample; card=true adds get.new.user.info (careerLv,
  careerType, allianceRank, giftLevel, badgeId; battle counters read 0 for others). ~0.2 s a call.
  refresh.stats runs it with playerDetails=50 (drone only).
- Not stored, by design: server.badges.get and lw.season.trend.info describe the GUEST's own server/season
  only; get.season.city.info / get.server.zone.completeness duplicate city.war.get.info.
- Views: drone + presidencies on player pages,
  drone column + strongholds on alliances (matchup compares AVERAGE drone power - only each server's top N are read), strongholds and badge date on servers.

## Chat search - 2026-09-27
- GET /api/chat/search?q=&room=&before= searches ALL stored messages (text, sender name, stored
  translations; exact tag or uid), newest first, 100 a page; `before` = the previous page's oldest sent_at.
- POST /api/chat/rooms/{room}/backfill?pages=20 runs chat.backfill.guest (chat.backfill on a throwaway
  guest, below the oldest held). Verified on country_1963: 3 pages = ~110 older messages.
- Chat UI: typing searches stored history (this room / all rooms), "Search older" pages back, at the end
  "Fetch older history from the game" backfills then searches again; clicking a hit opens it in context
  (highlighted) with "Back to the newest messages".


## Whole-game boards on Sadmin - 2026-09-27 (Henry asked; read-only calls, runner stopped after)
- On Sadmin (1967) server.rank / kill.rank / alliance.rank / kill.al.rank {ismerge:0} = 1967's own top 200 / 100 / 50 / 50 - the account's OWN server, same as rank.get global:0 which board.snapshot already stores. So on a guest they were the guest's server.
- rank.get {global:1} is identical on Sadmin and guests: a fixed group of servers 3-23 with far smaller numbers (top alliance 3.1B vs SaFa 14.8B) - not a whole-game board. No whole-game leaderboard found.
- intel.players --missing true reads only players without a drone read in the last day (finished the 400 lost to a dropped connection: 3,200 of the top 100 per server now read).


## Chat backfill to a date + matchup v2 - 2026-09-27
- chat.backfill until=YYYY-MM-DD pages back until the oldest message is at/before the date. Server queue (ChatBackfillJobs, in memory): POST /api/chat/backfill {rooms, until}, GET status, POST /cancel; one room at a time on throwaway guests, retried (20 s pause) on failure, finished only by reaching the date or a CLEAN run that found nothing older. ~1.5 days of a busy room per minute. The game keeps room history well past 8 days (1957 reached 24 Aug). All Storage connections now set busy_timeout=120000 (the hourly rebuild locked out chat inserts).
- /api/chat/rooms has firstAt; the Chat sidebar shows 'from <date>' per room and a 'Backfill history back to' box (defaults to the oldest date any room is held from).
- Matchup v2 (Henry: hero power is the differentiator): members count by HERO power (board value; off-board = power x median board share, at most 85% of the server's 200th place), weighted by recency (1 / 0.75 / 0.35 / 0.1), compared by SQUARED strength (Lanchester), chance = 1/(1+(def/att)^2). SaDe vs SaFa: SaFa 63% attacking, 62% open (was 52% / 56%).


## Hero power for everyone - 2026-09-28
- hero power = k x (power - armyPower) from the profile (get.user.info.multi; armyPower = troops). k = 0.780 overall (per server 0.773-0.788). Back-test on all 32 servers' newest top-200 hero boards (6,398 players, profile within 3 h): median error 1.0%, 90% within 2.4%, worst 6.9%, ranks 181-200 1.0%, no bias. Beats power x share (10.6%), max power seen (7.8%), min power seen (10.7%), log fits (7.5-9.4%), multi-feature fit (5.3%). Research scripts: data/raw/hero_backtest.py, hero_backtest2.py.
- HeroModel (Storage) rebuilds hero_fit with every history rebuild; op hero.fit re-runs it and prints the per-server table; Explorer home shows it. Rankings gained 'Hero power (estimated, everyone)' for players and alliances; rosters/matchup/player page use it (≈ marks an estimate).
- Chat: some history messages carry sendTime in ms, not s - ChatMessageParser reads > 1e11 as ms (it crashed backfill of 1963/1972).
- Matchup has a steepness knob (steep=1-4; default 2 Lanchester). SaDe vs SaFa (hero 7.49B vs 8.50B; members with hero >= 90M: 12 vs 30, >= 80M: 33 vs 62): SaFa attacking 54/62/70/77% for steep 1/2/3/4.


- Player page (2026-09-28): stats Hero power (real = newest hero board), Hero power (estimated, with the % difference against the board using the profile nearest the board time), Troop power (profile armyPower, % of power); chart 'Hero and troop power over time' = board vs estimate vs troops per profile. 1963 chat history ends 2026-09-03 in the game (clean runs find nothing older).


### Player activity report + drone tab (2026-09-28)
- Player page now has tabs (?tab=overview|activity|drone). /api/x/player/{uid}/activity -> Insights.PlayerActivityReport,
  /api/x/player/{uid}/drone -> Insights.PlayerDrone (+ stat names from lw_effect_number via GameText "effectnumber_name_<id>").
- Evidence kinds: seen (distinct profile lastUpdateTime), donated (distinct al.rank donateTime), chat, rise (power up between
  readings; counted into an hour only when the gap <= 3 h), troops/kills/hq (profile deltas), moved (map point, alliance, home).
  Score 0-100 = recency 40 + donating vs alliance median 20 + days with evidence (last 7) 20 + growth 10 + monthly card 10.
- Finding: lastUpdateTime moves on EVERY power change (37,199/37,199 between the 09-27 and 09-28 sweeps) plus 7,552 more with
  no power change -> it is real "in the game" evidence. One sweep keeps only the newest moment per player, so hourly sweeps
  would give the time-of-day picture (scan.profiles is ~4 min for 178k players).
- Finding: donateTime is exact to the second; three rosters 30 min apart on 09-27 caught 3 separate donations for one player.
- Finding: drone power is INSIDE total power, not hero power. hero ~= 0.829 x (power - troops - drone): median 0.98%, worst 6.3%
  vs 1.04% / 7.3% without it (2,824 players with both). Small gain, only where a drone read exists - not wired in.
- Drone level and chip level each correlate 0.90 with hero power; drone power is ~4% of total power (median).
- Server is now started with `--dev true` (auth middleware is live; localhost = "Local development" admin). Login UI not built yet.

### Alliance Activity tab (2026-09-28)
- /api/x/alliance/{id}/activity?days=7|14|30 -> Insights.AllianceActivity. Members = newest al.rank roster, else player_latest.
- Per member, moments in the window: profile last-seen, donation times, chat, power rises with readings <= 3 h apart (midpoint).
  "On at hour h" = any moment in hour h on any day. Strength at h = sum of hero power (profile estimate, else hero board).
- `sampled` = hours we took readings in. KNOWN BIAS: last-seen/donation moments pile up just before a reading, so those
  hours read high (PEC: 10:00 and 14-15:00 UTC spikes = sweep times). Hourly profile sweeps would remove it.

### Scheduled (2026-09-28)
- Top-level "Scheduled" tab (#/scheduled, admin only). LastWar.Operations.Scheduler runs ops on a timetable inside the
  server; ScheduleStore tables schedule_job / schedule_run. Jobs never overlap themselves; next due = run start + interval.
  Pause = no new runs; Stop = cancel the current run; a run cut off by a server stop is marked "interrupted" at start-up.
- Seeded jobs (added once, user edits win): activity-pulse = scan.profiles changes=true every 60 min (ON);
  alliance-rosters = intel.sweep every 6 h (OFF).
- scan.profiles `changes=true`: rows stored with source 'pulse' only when last seen / power / troops / kills / HQ / server /
  alliance / name / svip / card moved vs the newest profile (last 10 days); power_sample only when power moved.
  First pulse: 178,735 asked in ~2.5 min, 27,278 changed and stored (vs 178k for a full sweep).
- Finishing can wait on the hourly Derived.RebuildAll holding the DB (first run finished 3 min after its last batch).

### Postgres (2026-09-28) - the database is now Postgres, SQLite is the archive
- Local: `docker compose up -d` in LastWar\ (docker-compose.yml; password in LastWar\.env POSTGRES_PASSWORD).
  Postgres 18, database/user `lastwar`, schema `lastwar`, data in the docker volume lastwar_lastwar-pg.
  Initialised with the builtin C.UTF-8 locale: text sorts by code point like SQLite, so ISO timestamps compare
  correctly as text. A language collation would ignore '-', ':' and '.' and mis-order timestamps - keep C.UTF-8 on any host.
- Connection: PgConfig.Resolve - `--pg "<conn string>"`, else env LASTWAR_PG, else LastWar\.env. Search Path defaults to
  lastwar. Server and CLI take `--root` (folder holding data\) for files: sessions, assets, raw, runners.
  OperationContext: Database = connection string, DataDir = data folder.
- Moving data: `lastwar op --op db.postgres.copy --sqlite <file> [--replace true]` copies every table with binary COPY
  (types chosen from what SQLite really stores) and verifies row counts + per-column checksums (670 checks on the
  2026-09-28 snapshot, all identical). `db.postgres.verify` re-runs the checks. SQLite is opened read-only.
- Port rules used everywhere (see Db.cs): @params; INSERT ... ON CONFLICT DO NOTHING / upsert on the PK; camelCase
  aliases must be double-quoted (Postgres folds unquoted names to lower case and the pages read camelCase keys);
  COUNT(*) FILTER (WHERE ...) instead of SUM(boolean); GREATEST for two-argument MAX; ILIKE where SQLite LIKE was
  case-insensitive; DISTINCT ON instead of SQLite's bare columns next to MAX(); NULLS LAST on DESC sorts of nullable
  columns; LIMIT 1 on scalar subqueries; Db.AgoIso / MinusIso / HourOf / DaysBetween for time maths on ISO text.
- EnsureSchema DDL runs once per process (Db.FirstTime): even CREATE INDEX IF NOT EXISTS takes table locks in Postgres.
- Rebuilt history tables load with Db.Copy (binary COPY).
- Parity check before the switch: old SQLite server on the snapshot vs new Postgres server on the same snapshot, 62
  endpoints (data\raw\cmp_api.py): identical except row order among ties (now broken deterministically) and
  timestamps of rebuilds; hero model values identical; derived tables equal in size and content length.
- Tests use a throwaway schema per test in the local Postgres (tests\TestDb.cs).
- Backups kept: backups\sqlite_20260928_1544 (the files as they were), backups\code_before_pg_* (source before the port).
- CUTOVER done 2026-09-28 16:37-16:46 local: server stopped after the 16:31 pulse, files copied to
  backups\sqlite_final_20260928_1637 (hash-checked), checkpointed snapshot copied into Postgres with --replace
  (5,390,692 rows, 670 checks identical), then db.postgres.verify against the live data\lastwar.db itself: identical.
  The server (bin\server, --dev true) now runs on Postgres. data\lastwar.db is the read-only archive - nothing writes it.
- cmp\ holds the side-by-side check copies (old binary + snapshot copy); safe to delete.

## Server seasons and groups - research 2026-09-30 (guests only)

Probe sets data/probes/servergroups.json + servergroups2.json (probe.run), dumps data/raw/probe_servergroups*_20260930_*.json.

Guest, any server:
- cross.server.ls -> all 2,574 servers {id,name,hot,server_type}: type 0 = 2,513 normal, type 8 = 9001-9060, type 6 = 9000 (cross/centre servers).
- get.other.server.info {server} / get.cross.server.info {serverId} -> openTime (1957 2025-11-18, 1988 2025-12-04, 2020 2025-12-17).
- get.cross.server.king.info {serverIds:"a,b,..."} (136 ids in one call OK) -> each server's king + current badge {cfgId, createTime}. Servers badged at the same moment took part in the same event: 511020 @ 2026-09-14 02:00 on 16 servers spread over 1957-2018 (= S2 settlement day of cfg 1322), 511026 @ 2026-07-06 on 19 servers over 1959-2019.
- get.zone.intelligence {server} -> serverForceRank / totalNum (169 = S2 zone count), top-5 alliances. get.season.city.info {targetServer} -> 84 season cities with holders. get.cross.declare.war.server.info {targetServerId} -> campId (2021: camp 2).
- lw.req.world.occupy.info {serverId} -> war-group composition only while an occupation season runs (1967 empty).

Guest, OWN server only (so useless unless the account sits on the server): get.season.infos (seasonArr S1-S6 dates, cfgIds), lw.season.trend.info (season_group), get.player.cross.server.list, server.badges.get (servers in the badge's war zone), season.migrate.activity.info (migrateServerList; "act not open" now). A 1967 account (Sadmin/Ghost) would answer these for 1967 - NOT run (real accounts).

Static tables (data/assets/tables, APK table pack 39280) - the best source:
- lw_zone_war.json (3,679 rows): every zone-war matchup {server_ids "a;b|c;d", open_time, end_time, season_tag_for_alert "10008,S1", medal_reward}. 1957-1988: S0 pairs Dec 2025-Jan 2026, S0 4v4 Feb-Mar 2026, S1 4v4 2026-06-15..07-05 (factions mix 1957-1988 only). S2 4v4 rows exist for 1925-1956 (Aug 31) but none for 1957+.
- yuntie_battle_server_group_new.json (173 rows): event match groups per season as ranges: season 1 and 2 "1893-1956|1957-2020" (64-server groups paired), "2021-2084|2085-2148". So 1957-2020 is already ONE 64-server group for that event.
- lw_season.json (1,526 rows): season configs by cfgId. 1322 (1967's seasonConfigId) = S2: pre 2026-07-13, start 07-27, settlement 09-14, end 09-20. No server list in the row; server -> cfgId comes only from the server itself at login.
- Tables only change with a new table pack (hot update / new APK); lw_zone_war runs to 2026-09-28 in this pack.

## Meteorite Iron War (yuntieBattle) - 2026-09-30
Commands (commands.csv): meteorite.act.info, meteorite.score.count, meteorite.rank.person.info / meteorite.rank.alliance.info (Int grabTimes = round, Int startRank, Int count), meteorite.enter.world (Int targetServerId), meteorite.random.point / meteorite.drop (Int id), meteorite.get.reward (Int rewardId); pushes push.meteorite.*.
Guest probe (random new server, not in an event): every read replies {success:true} only (rank replies echo grabTimes). Tables: yuntie_battle_entity (point rates), _refresh / _reigger_refresh (spawn waves over the 1800 s round), _reward (9 chests 15k-600k; rank bands), _server_group_new (weekly pools, e.g. S2 "1893-1956|1957-2020" 2026/9/21-9/28; the extracted pack ends 9/28).

## Guest on a chosen server - 2026-09-30
GSL opt=new with zone=APS<id> now succeeds for 1957/1967/1975/1988/1989/2020/2300 (E013 on 1967 on 2026-09-22). The system login that follows replies
`{"p":{"eu_state":0,"errorMessage":"E002"},"zn":"APS1967","un":"Empire10337","rl":[]}` - no init, commands unanswered. A normal (unzoned) guest's login reply carries serverVersion/randKey/xmlVersion. E002 happens for every zone tried, so the missing piece is on our side.
activity.calendar.view {} -> {dataArr:[]}, get.activity.clock.info {} -> {list:[]} on a fresh guest (server 231).
