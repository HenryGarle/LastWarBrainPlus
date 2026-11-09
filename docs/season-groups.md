# Season groups

Our block: servers 1957-2020 (64), two halves of 32 (1957-1988, 1989-2020) that fight 4 v 4 wars inside each half.
Season 1 2026-04-13, Season 2 2026-07-27 -> 09-20. Season 3 expected: pre-season Mon 2026-11-09, start Mon 11-23,
end 2027-01-17.

## The game's tables (no account needed)

The `game-tables` job (op `servers.tables`) fetches the client config from the game's CDN when a new version is out
and loads three tables (`ServerRegistry.IngestTables`) from `data/assets/tables`:

| Table | Our table | What |
|---|---|---|
| lw_season | game_season | One row per season CONFIG: season number, pre_start, start_time, end_time, zone_war_season_group, server_local, camp_sever_local (coded, not stored - read the json). No server list. |
| lw_zone_war | game_zone_war | 4 v 4 (type 8) and duel (type 4) wars with `server_ids` "a;b;c;d|e;f;g;h". Appear shortly before the wars. |
| yuntie_battle_server_group_new | game_event_group | weekly matchmaking pools ("1893-1956|1957-2020"). |

Season configs come in waves of 8 per 64-server block (one config = 8 servers). Our S2 = 1319-1326 (#1967 on 1322).
Tables version 40125 (2026-11-04) added 36 configs incl. S3 1497-1504 (pre 11-09, start 11-23) - almost certainly ours
(cadence: the block before us went S2 06-22 -> S3 10-12, 16 weeks).

## Reading every server's season group (the method that works)

The in-game "create a character on server X" is SFS command `new_account {Int confirm=1, Int specify=1, Int targetServer}`
(Lua `NewAccountMessage`, sent from UIRoleCreate). A server that is in a season refuses:

```
{"errorCode":"season_creat_role_tips","errorMsg":"target server not allow new user 1322 1"}
```

The number is the lw_season config the server is on now. Same config = same season group.

- Op `servers.season` (needsSession false: makes its own guest; default `@full`, gap 3 s jittered) asks every server,
  stores `server_season_cfg (server_id, cfg_id, first_seen, last_seen)`.
- Job `season-groups`, daily, enabled (hosted and local).
- Page: Explore > Servers & groups > "Season groups" card. API `/api/x/season-groups?lo=&hi=`.
- A server open to new players would answer with a loginKey (a character for the guest there). We never redeem it.
- TIMING (confirmed 2026-11-09): the refusal names the server's RUNNING season. Servers flip from the old config to
  the new one at SEASON START, not at pre-season. On 2026-11-09 (S3 pre-season, start 11-23) every 1957-2020 server
  still answered its S2 config (1319-1326). The raw reply carries nothing but the number - no pre-season group field.
  So the full S3 groups are not reachable by this method until 2026-11-23; the daily season-groups job will pick them
  up then with no change needed. The grouping visible in-game during pre-season is a per-server read (lw.season.trend.info
  season_group, or the pre-season UI) that needs an account ON that server - the tool has none on our block (Ghost
  blocked, lw_1 banned), so there is no tool-only way to read S3 groups before the 23rd.

S2 groups read 2026-11-07 (they match server_zone_sample force ranks 1-8 inside each group):

| cfg | servers |
|---|---|
| 1319 | 1959, 1961, 1969, 1971, 1974, 1976, 1984, 1988 |
| 1320 | 1960, 1963, 1968, 1972, 1978, 1979, 1980, 1986 |
| 1321 | 1957, 1962, 1965, 1966, 1975, 1981, 1982, 1987 |
| 1322 | 1958, 1964, 1967, 1970, 1973, 1977, 1983, 1985 |
| 1323 | 1989, 1992, 1995, 1998, 2011, 2015, 2017, 2018 |
| 1324 | 1991, 1993, 1999, 2000, 2006, 2013, 2019, 2020 |
| 1325 | 1990, 1997, 2003, 2004, 2005, 2009, 2012, 2016 |
| 1326 | 1994, 1996, 2001, 2002, 2007, 2008, 2010, 2014 |

## Decoding lw_season.camp_sever_local

Values look like `lw:3f133a1c44321a3736`. The 9-byte ones (248 of 719) are two 4-digit servers and a separator,
each byte XOR a fixed key per position:

```
key = 0e 2a 0d 28 ?? 03 23 02 0e     (position 4 is the separator, cipher byte always 0x44)
lw:3f133a1c44321a3736 -> 1974|1958
```

Chosen because 239/248 codes then decode to two servers in the same 64-block with no duplicates; confirmed by Henry's
lists below. S3 codes: 1497 1974|1958, 1498 1961|1975, 1499 1971|1960, 1500 1978|1964, 1501 2004|1989,
1502 2009|1991, 1503 1998|2002, 1504 2017|2013. Shorter codes (2-8 bytes) are other formats, not decoded.

What the pair means: in both lists Henry gave, it is the group's two strongest servers (top-100 power). It is not the
4 v 4 pairing. The other 6 servers are not in any table.

Henry's S3 lists (seen elsewhere, 2026-11-07):
- 1497: 1957, 1958, 1959, 1965, 1970, 1972, 1974, 1980
- 1498: 1961, 1973, 1975, 1977, 1979, 1982, 1984, 1988

Known S3 (2026-11-09): full for 1497 and 1498 from Henry's lists above; two strongest each from the camp_sever_local
decode for the rest - 1499 1971,1960; 1500 1978,1964; 1501 2004,1989; 1502 2009,1991; 1503 1998,2002; 1504 2017,2013.
The remaining 6 per group come from the servers.season read on/after 2026-11-23.

Tested and rejected as a rule for the rest: snake/round-robin drafts by any power measure we hold; "2 per strength pot of 8"
fits both lists on our 2026-09-25 data but our data starts after S2 ended and transfers began, so pots do not line up
(1978 is a seed but ranked 27th). 1497 takes exactly 2 servers from each S2 group; 1498 does not. Use the
`servers.season` reading instead of predicting.

## Throwaway tools

`C:\Temp\LWC\data\raw\guestx` (console, guests only): modes `newblank` (chosen zone, blank uid), `rawgsl` (raw gateway
reply), `newacct <a,b,c>` (new_account for each target from one guest). Outputs data\raw\guestx_*.txt.
