# Scans > Secret Tasks

Op `secret.tasks` reads every Secret Task on one server's map (MapScanner, gap 250 ms). Page: `web/src/panels/Scans.jsx`.

## Rank 1-28

From lw_dispatch_tasks: color 3/4/5 = SR/SSR/UR, level = stars, is_special = red.
SR 1-7, SSR 8-14, UR 15-21, red UR 22-28 (`Operations.SecretTasks.Rank`). Default sort: rank, then time left.

## Rewards

- Not in the map point or client tables. The client asks per task: `world.get.detail.new {point, serverId, worldId 0,
  previewAssistance 0, pointType 17, uid ""}` (WorldDispatchTask.lua).
- Op `secret.task.rewards {server, point, completes}`: ONE request per click of a row's button (Henry: "reducing the
  amount of spam"). Refuses an unfinished task. GUEST TIER ONLY since 2026-11-02 (lw_2 ban); real accounts see "guest only".
- Reply: top-level `reward[]` / `extraRewards[]` entries `{type, value:{num,id}}` or `{type:1|20, value:<number>}`.
  `stealInfoList` repeats rewards per plunderer - ignore it. Parse the JSON text (live SfsJson nodes lost ids).
- Same cfgId = same rewards: cached in `secret_task_reward` by cfgId; every later scan shows them without asking.
- Names (GameText): goods via T(name), type 27 via aps_resource_item, unnamed goods type 2 = speed-ups
  (para3 = seconds; type2 1 universal / 3 training / 4 healing / 6 research / 7 construction).
- Raw replies: data/raw/task_detail_<server>_<point>.json.

## Rules (Henry)

- "Do not scan for contents of a task that has not finished as the game does not let you do that." Page shows
  "when finished".
- Never request a point that is not a task in the latest scan.
- The list "updating" is a 15 s live countdown, not a re-scan.

## Page features

- Coordinates are clickable: a dialog with a map centred on the task (MapPage with route extra `x,y`) - Henry did not
  want chat links ("Sending messages with links seems like it might cause problems").
- Debug checkbox: scan reply, cfg/point, task table row, raw reward reply.
- Rows keyed by pointId (duplicate keys had broken filtering/sorting); trucks keyed by uuid or server:marchUid.
- Not built: a value score across rewards.
