using LastWar.Client.Scanning;
using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Operations;

public static partial class Operations
{
    /// <summary>
    /// Secret Tasks (hero dispatch missions, map point type 17) on one server, read live off a map
    /// sweep. Nothing is stored - tasks last hours, so the Scans page asks again when it wants them.
    /// Not "scan.*" on purpose: that prefix triggers the history rebuild, and this stores nothing.
    /// </summary>
    static void AddSecretTaskOperations()
    {
        Add("secret.tasks", "Scan", "Secret Tasks on a server",
            "Sweeps a server's map and lists every Secret Task on it: rarity (SR/SSR/UR), stars, whose it is, "
            + "time left and how many times it has been plundered. Stores nothing. Other servers need an "
            + "account past the cross-server map gate (HQ 13); a guest can read its own server.",
            async c =>
            {
                var client = c.Require();
                var server = c.Int("server", client.Server.Value);
                var tasks = new Dictionary<int, SecretTask>();
                // Closest zoom (lod 0, blocks of 10): at lod 1 the server sends no secret-task points at all (#679: 0 vs 24).
                var scanner = new MapScanner(client, MapOptions(c) with { BlockSize = 10, Lod = 0 })
                {
                    OnReply = r => { foreach (var t in SecretTasks.Read(r, server)) tasks[t.PointId] = t; },
                };
                var tiles = await scanner.ScanAsync(new ServerId(server), null, c.Cancellation);
                var bases = tiles.GroupBy(t => t.OwnerUid.Value).ToDictionary(g => g.Key, g => g.First());
                var hero = new Dictionary<string, long>();
                var uids = tasks.Values.Select(t => t.OwnerUid).Where(u => u.Length > 0).Distinct().ToArray();
                if (uids.Length > 0)
                {
                    using var db = Db.Open(c.Database);
                    foreach (var r in Db.Rows(db, "SELECT uid, hero FROM player_latest WHERE uid = ANY(@u);", ("@u", uids)))
                        hero[(string)r["uid"]!] = r["hero"] is long h ? h : 0;
                }
                var known = KnownTaskRewards(c.Database);
                var now = DateTimeOffset.UtcNow;
                // completionTime is unix SECONDS (probe 2026-10-02, #1559); the ms branch is only a guard. secondsLeft < 0 = finished that long ago, not yet collected.
                static DateTimeOffset? At(long v) => v <= 0 ? null : v > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(v) : DateTimeOffset.FromUnixTimeSeconds(v);
                var rows = tasks.Values.Select(t =>
                {
                    var cfg = Text(c).DispatchTask(t.CfgId);
                    var done = At(t.CompletionTime);
                    bases.TryGetValue(t.OwnerUid, out var b);
                    return new object?[]
                    {
                        t.X, t.Y, Rank(cfg.Rarity, cfg.Stars, cfg.Red), cfg.Rarity, cfg.Stars, cfg.Red ? "red" : "", cfg.Name,
                        done is { } d ? (long)(d - now).TotalSeconds : "", done?.ToString("u") ?? "",
                        b?.Name ?? "", t.OwnerUid, b?.AllianceTag ?? "", hero.GetValueOrDefault(t.OwnerUid) is > 0 and var hp ? hp : "",
                        $"{t.Steals}/{cfg.MaxSteals}", t.Assists, t.Rewarded ? "yes" : "", t.CfgId, t.PointId, known.GetValueOrDefault(t.CfgId, ""), Text(c).DispatchRow(t.CfgId),
                    };
                }).OrderBy(r => r[6] is long s ? s : long.MaxValue).ToList();
                var miss = scanner.LastMissedBatches > 0 ? $" ({scanner.LastMissedBatches} map batches unanswered - some may be missing)" : "";
                return OperationResult.Table($"#{server}: {rows.Count} secret tasks{miss}",
                    ["x", "y", "rank", "rarity", "stars", "red", "task", "secondsLeft", "completes", "owner", "uid", "tag", "heroPower", "plundered", "assists", "rewarded", "cfgId", "pointId", "contents", "cfgRow"], rows);
            },
            ps:
            [
                new OperationParam("server", "Server", "number", Required: true, Help: "The server to look at; a guest can only read its own."),
                new OperationParam("gap", "Gap between requests (ms)", "number", "250"),
            ]);

        // One task's rewards: the same world.get.detail.new the game sends when you tap a task on the map
        // (UIWorldPoint/WorldDispatchTask.lua: requestWorldDetail -> WorldGetDetail, reply reward[] + extraRewards).
        // ONE request, only when someone clicks - never in a sweep (Henry 2026-10-30: keep the requests down).
        Add("secret.task.rewards", "Scan", "One Secret Task's rewards",
            "Asks the game for one Secret Task's detail (what tapping it on the map shows) and lists its rewards. One request.",
            async c =>
            {
                var client = c.Require();
                var server = c.Int("server", client.Server.Value);
                var point = c.Int("point", 0);
                if (point <= 0) throw new ArgumentException("point is required");
                // Henry 2026-10-30: the game will not show a task's contents before it finishes - never ask for an unfinished one.
                if (DateTimeOffset.TryParse(c.Text("completes", ""), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var completes) && completes > DateTimeOffset.UtcNow)
                    throw new InvalidOperationException($"not finished yet (completes {completes:u}) - the game only shows the contents once it has");
                var p = new LastWar.Protocol.Sfs.SfsObject();
                p.PutInt("point", point);
                p.PutInt("serverId", server);
                p.PutInt("worldId", 0);
                p.PutInt("previewAssistance", 0);
                p.PutInt("pointType", 17);   // HERO_DISPATCH
                p.PutUtfString("uid", "");
                var reply = await client.TryCallAsync("world.get.detail.new", p, TimeSpan.FromSeconds(10), c.Cancellation)
                    ?? throw new TimeoutException("no reply from the game");
                if (reply.GetString("errorCode") is { Length: > 0 } err) throw new InvalidOperationException($"the game refused: {err}");
                var json = LastWar.Protocol.Sfs.SfsJson.Write(reply);
                // Kept for checking the reward format (first sightings, 2026-10-30).
                try { File.WriteAllText(Path.Combine(c.DataDir, "raw", $"task_detail_{server}_{point}.json"), json.ToJsonString()); } catch (IOException) { }
                // Re-read from text: the live SFS nodes read the nested value.id as empty and listed each reward twice (2026-10-30).
                var rows = TaskRewards(System.Text.Json.Nodes.JsonNode.Parse(json.ToJsonString()), Text(c));
                // Same task config, same rewards (2026-10-30: two tasks each of 60000401 and 60000501, different owners, identical):
                // keep them by cfgId so every later scan shows the contents without asking again.
                if (rows.Count > 0 && reply.GetInt("cfgId") is > 0 and var cfgId)
                    SaveTaskRewards(c.Database, cfgId, server, rows);
                return OperationResult.Table(rows.Count > 0 ? $"#{server} task {point}: {rows.Count} reward(s)" : $"#{server} task {point}: no rewards found in the reply",
                    ["reward", "kind", "type", "id", "count"],
                    // Last row: the raw reply, for the page's debug view.
                    [.. rows, ["(raw reply)", "raw", "", "", json.ToJsonString()]]);
            },
            ps:
            [
                new OperationParam("server", "Server", "number", Required: true),
                new OperationParam("point", "Task point id", "number", Required: true),
                new OperationParam("completes", "Completes at (UTC)", "text", "", Help: "From the scan; an unfinished task is refused without asking the game."),
            ]);
    }

    /// <summary>
    /// Reward lines anywhere in a detail reply: every array whose name mentions "reward" (reward, extraRewards...), each entry
    /// read the forgiving way the game's own reward lists vary - type/rewardType; id/itemId/value.id; num/count/value.num.
    /// </summary>
    static List<object?[]> TaskRewards(System.Text.Json.Nodes.JsonNode? root, GameText text)
    {
        var rows = new List<object?[]>();
        static string S(System.Text.Json.Nodes.JsonNode? n) => n is System.Text.Json.Nodes.JsonValue v ? v.ToString() : "";
        static System.Text.Json.Nodes.JsonNode? F(System.Text.Json.Nodes.JsonObject o, params string[] keys)
        {
            foreach (var k in keys) if (o[k] is { } n) return n;
            return null;
        }
        void Walk(System.Text.Json.Nodes.JsonNode? n, string kind)
        {
            if (n is System.Text.Json.Nodes.JsonArray a) { foreach (var x in a) Walk(x, kind); return; }
            if (n is not System.Text.Json.Nodes.JsonObject o) return;
            var type = F(o, "type", "rewardType");
            var value = o["value"] as System.Text.Json.Nodes.JsonObject;
            var id = F(o, "id", "itemId", "param") ?? (value is null ? null : F(value, "id", "itemId"));
            var num = F(o, "num", "count") ?? (value is null ? (o["value"] is System.Text.Json.Nodes.JsonValue ? o["value"] : null) : F(value, "num", "count", "value"));
            if (kind.Length > 0 && type is not null && int.TryParse(S(type), out var ty))
            {
                var count = long.TryParse(S(num), out var cn) ? cn : 1;
                rows.Add([text.Reward(ty, S(id), count), kind, ty, S(id), count]);
                return;
            }
            foreach (var (k, v) in o)
                Walk(v, k.Contains("reward", StringComparison.OrdinalIgnoreCase) ? k : kind);
        }
        // Top level only: stealInfoList[].reward repeats it per plunderer (2026-10-30, #1964 task 878119).
        if (root is System.Text.Json.Nodes.JsonObject top)
            foreach (var (k, v) in top)
                if (k.Equals("reward", StringComparison.OrdinalIgnoreCase) || k.Equals("extraRewards", StringComparison.OrdinalIgnoreCase)) Walk(v, k);
        return rows;
    }

    static void SaveTaskRewards(string database, int cfgId, int server, List<object?[]> rows)
    {
        Db.Exec(database, """
            CREATE TABLE IF NOT EXISTS secret_task_reward (cfg_id BIGINT PRIMARY KEY, rewards TEXT NOT NULL, server BIGINT NOT NULL, seen_at TEXT NOT NULL);
            INSERT INTO secret_task_reward (cfg_id, rewards, server, seen_at) VALUES (@c, @r, @s, @t)
            ON CONFLICT (cfg_id) DO UPDATE SET rewards = EXCLUDED.rewards, server = EXCLUDED.server, seen_at = EXCLUDED.seen_at;
            """, ("@c", (long)cfgId), ("@r", string.Join("; ", rows.Select(r => (string)r[0]!))), ("@s", (long)server), ("@t", DateTimeOffset.UtcNow.ToString("O")));
    }

    /// <summary>Rewards seen so far, by task config ("Mystery Supply Box x12; Gear Material Chest x125; ...").</summary>
    static Dictionary<int, string> KnownTaskRewards(string database)
    {
        using var c = Db.Open(database);
        if (Db.Rows(c, "SELECT to_regclass('secret_task_reward') IS NOT NULL AS ok;")[0]["ok"] is not true) return [];
        return Db.Rows(c, "SELECT cfg_id, rewards FROM secret_task_reward;").ToDictionary(r => Convert.ToInt32(r["cfg_id"]), r => (string)r["rewards"]!);
    }

    /// <summary>
    /// One number for how good a task is (Henry 2026-10-30: the stars alone were unclear). lw_dispatch_tasks: rarity (color
    /// SR/SSR/UR), red stars (is_special, the never-refreshed best ones) and level 1-7 (the stars shown). SR 1-7, SSR 8-14,
    /// UR 15-21, red UR 22-28: higher is better.
    /// </summary>
    static int Rank(string rarity, int stars, bool red) =>
        (red ? 21 : rarity switch { "SR" => 0, "SSR" => 7, "UR" => 14, _ => 0 }) + Math.Clamp(stars, 0, 7);
}