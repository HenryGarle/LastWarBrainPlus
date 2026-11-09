# lastwar-client - working state (C:\Temp\LWC)

## Established-account login: WORKING (no packet capture)

Two things were needed, both now in place:

1. `dumpsession.exe` (new, cmd/dumpsession) writes ~/.lastwar_goclient_session.json
   from a GSL opt=login lookup. The README says these values need a tcpdump capture;
   they do not - GSL already returns ip/port/zone/gameUid/accessToken every run, it
   just was not persisted. A config containing "ip" makes internal/app/main.go:337
   dispatch to the cross-server path automatically, no flags.
2. "iosMode": true in that config. The account is iOS-issued; the Android identity is
   rejected with ec=28 (ep=[E005]). docs/findings.mdx documents this exact gate:
   a token is bound to the packageName/platform it was issued under.

Recipe after a token expires (symptom: ec=28 on every run, exit code 2):
    .\dumpsession.exe
    # then set "iosMode": true in ~/.lastwar_goclient_session.json
    .\lastwar-client.exe -list-buildings

Confirmed live: State#1967, 48 buildings, 5 visitors, real mail pushes.
Auth state backed up in C:\Temp\LWC\auth-backup\.

## Chat: credentials captured, transport blocked

internal/game/chatcreds.go (new) pulls chatToken + uid from the init push and writes
~/.lastwar_goclient_chatcreds.json. Confirmed: chatToken 172 chars, uid 2359053554001967.
Called from ParseInitBuildings - the one point both login paths hand over full init params.

Blocked on: chat_server_links.php request shape.
  - Endpoint confirmed live at <gateHost>/gameservice/chat_server_links.php
  - Answers {"code":-1,"message":"error params"} to ~20 param-name guesses
    (GET and POST, sec and ms timestamps, appId/appid/app_id, uid/gameuid, +serverId,
     +uuid/airKey, +lang/platform)
  - NOT the RSA+AES uuid+data envelope the sibling GSL endpoints use: chatprobe.exe
    proves this, its control probe against getserverlist.php returns a real encrypted
    payload with the same envelope code while chat_server_links.php still says error params.
  - Known from docs: sign = MD5(MD5("100017" + uid) + timestamp), confirmed by ARM64
    disassembly of CalcChatSign (docs/findings.mdx). The formula is known; which fields
    carry it is not.
  - Still unknown after that: the chat WebSocket URL, and where connectionSign comes from.

Ways forward, cheapest first:
  1. Ask the dossier author (github.com/ljagiello/lastwar-client). He holds the extracted
     reports with the 131 Chat.WebMessage.* commands and presumably the request shape.
  2. Proxy the phone (mitmproxy + trusted cert) and catch the one bootstrap request.
     Reveals the params immediately if the app does not pin certs.
  3. APK static analysis: ChatNetManager:InitWebSocketInst in the Lua bundle.
     Deterministic but heavy - multi-GB, custom-packed AssetBundle, ChaCha-obfuscated.

## Tools built
  dumpsession.exe   writes the session config from GSL
  chatprobe.exe     probes chat_server_links.php, takes uid as argv[1]
  lastwar-client.exe  the client, with the chatcreds capture patched in
