# Game client research (decompiled app)

## Where it is

`C:\Temp\LWC\apk` (extracted 2026-09-22, app 1.0.351):
- `src\` - ILSpy C# of the Unity code (~3,600 .cs). Login, gateway and network live here
  (NetworkManager.cs, LoginMessage.cs, LoginState.cs, AccountSelectState.cs, AccountCredentialManager.cs).
- `lua\` - ~18,700 xLua bytecode files named by path (`UI__UISetting__UIRoleCreate__View__UIRoleCreateView.luac`).
  Lua 5.3 with a modified header (format byte 01, four size bytes) - unluac does not read it and no decompiler is
  installed. What works: pull printable strings out of a file and read function names, constants and command names in order.
- `Net__Config__MsgDefines.luac` maps message names to command ids (e.g. `NewAccount` -> `new_account`).
  `data/api/commands.csv` (the catalogue) misses Lua-only commands like new_account.

String dump used (PowerShell):

```
$b=[IO.File]::ReadAllBytes($file); $sb=New-Object Text.StringBuilder; $o=New-Object Collections.Generic.List[string]
foreach($x in $b){ if($x -ge 32 -and $x -lt 127){ [void]$sb.Append([char]$x) } else { if($sb.Length -ge 3){ $o.Add($sb.ToString()) }; [void]$sb.Clear() } }
```

Run big searches over apk\ in a background script (they take minutes).

## Login flow (what we know)

1. Gateway `getlsu3dversion.php` gives the RSA key; `getserverlist.php` with an encrypted form (uuid, airKey, loginFlag,
   country, is3D, lang, simOp, platform, isSimulator, zone, gameuid, newServer=1, openCountry, opt, loginKey, rt).
   `opt`: new (no uid), fix (uid, no rt), refresh (rt), login (loginKey from email login or new_account), omitted while
   the access token is fresh.
2. Default `opt new` reply: `serverList` (one server with `status:"0"`, the gateway's pick), `loginServer` (port 6088,
   used by the loading screen's account select), at/rt tokens.
   With `zone=APSxxxx`: only that server in serverList, no `status`, no loginServer.
3. SFS `login` (LoginMessage.CSSetData): gameUid "" means a new player and adds country=1, suggestCountry=false,
   timeoffset, gcmRegisterId, referrer. Our `LoginParams.Build` matches this.
4. A chosen zone + blank uid -> `E002` (the app maps E001/E002 to "account does not exist") - tried on #1967 and on
   #1738 right after a default guest was created there. So the app never creates a first character on a picked server
   this way.
5. In-game new character on a picked server: Settings > Roles > choose server (UIChooseServer, list from
   `account.get.all.server {type, page}` - type 0 recommended, 1/2 every server with openTime/status) -> UIRoleCreate
   -> `new_account {confirm, specify, targetServer}` -> reply `loginKey` -> client clears credentials, saves the
   loginKey, reloads -> gateway `opt login`. Only the refusal path is proven (servers in a season refuse, naming their
   season config - see season-groups.md). The loginKey path is untested.

## Commands proven on guests (own server)

get.season.infos, lw.season.trend.info, server.badges.get, season.migrate.activity.info ("act not open" off-season),
get.player.cross.server.list, get.zone.intelligence, account.get.all.server, new_account (refusal).
activity.selectcamp.info and lw.camp.battle.vs.simple.info answer `{success:true}` with nothing off-season.

## Chat gateway: reading a room by id (tested 2026-11-09, guest)

- A logged-in session gets a chat token in its login push (LastWarClient.ChatCredentials); ChatListener.StartAsync opens
  a chat connection via ChatDirectory for that uid/server. The chat cluster is GLOBAL: a guest on #1482 reached
  `wss://lastwar-chat-gateway-aws.lastwargame.com` fine - the server a room belongs to does not gate the connection.
- `history.roomsv2 {rooms:{<room>:"0"}}` (newest page) and `history.roomlast` pull a room by id. Tested against an
  alliance main room (alliance_<server>_<hex>) from a guest that is NOT a member: both returned EMPTY
  (roomsv2 `result.rooms:[]`, roomlast `result:{}`) - no messages, no error. So the chat server serves a room's history
  only to members; a non-member gets nothing back.
- Net: reading an alliance main room needs a member account on that server. A guest can't be placed on a chosen server
  (random landing; chosen-zone = E002), so a guest can't be made a member of a specific alliance. The read works from
  an account that is genuinely in the alliance (Inbox chat-link path: member account -> history.roomsv2 for its own rooms).
- guestx mode `chatroom <roomId>` reproduces the test (guest, read-only). DMs / R4 channels are out of scope (see
  account-safety.md).

## Throwaway harness

`C:\Temp\LWC\data\raw\guestx` (references LastWar.Client): `guestx.exe <home> <zone|list> <mode>`, homes under
guestx\homes. Modes: first run (default guest), same/refresh/fix/zone/newzone (re-login and chosen-zone tests),
newblank, rawgsl, newacct. Guests only.
