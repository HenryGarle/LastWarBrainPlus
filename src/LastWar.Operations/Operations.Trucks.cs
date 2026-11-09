using System.Text.Json;
using LastWar.Client;
using LastWar.Client.Game;
using LastWar.Protocol.Sfs;

namespace LastWar.Operations;

public static partial class Operations
{
    // Truck resources (baseGoods type): reward_types says 1 METAL, 20 FOOD, 31 WOOD; the game's words are Iron,
    // Food and Coin. ponytail: 31 = Coin is inferred (Last War has no wood), confirm when a truck mail shows it.
    static readonly Dictionary<int, string> TruckResources = new() { [1] = "Iron", [20] = "Food", [31] = "Coin" };

    /// <summary>
    /// Intercity trade trucks (internally "train": train.list, the "Others' Trucks" screen). The list covers the
    /// account's own server and the servers matched with it (trainServers), 15 random trucks per call; each
    /// isRefresh=true call deals another 15, so a few calls collect most of the ~100. Positions: get.march.pos
    /// {serverId, worldId 0, marchUuid, marchType 2 = NewMarchType.TRAIN} -> positionId, as the list's "go to"
    /// button does (RailwayUtil). Stores nothing.
    /// </summary>
    static void AddTruckOperations()
    {
        Add("trucks.list", "Scan", "Trade trucks",
            "Lists the intercity trade trucks this account can see (its server and the servers matched with it): "
            + "owner, quality, level, cargo, times looted, arrival and where each one is now. Stores nothing.",
            async c =>
            {
                var client = c.Require();
                var refreshes = Math.Clamp(c.Int("refreshes", 5), 0, 10);
                var trucks = new Dictionary<string, JsonElement>();
                string groupServers = "";
                for (var i = 0; i <= refreshes; i++)
                {
                    var p = new SfsObject();
                    p.PutBool("isRefresh", i > 0);
                    var reply = await client.TryCallAsync("train.list", p, TimeSpan.FromSeconds(10), c.Cancellation);
                    if (reply is null) break;
                    if (reply.GetString("errorCode") is { Length: > 0 } ec) throw new InvalidOperationException($"train.list refused: {ec}");
                    var j = Json(reply);
                    groupServers = Str(j, "trainServers");
                    var before = trucks.Count;
                    if (j.TryGetProperty("ls", out var ls))
                        foreach (var t in ls.EnumerateArray()) trucks[Str(t, "uuid")] = t;
                    c.Report($"call {i + 1}: {trucks.Count} trucks");
                    if (i > 0 && trucks.Count == before) break;   // nothing new: seen them all
                    await Task.Delay(Jitter.Of(TimeSpan.FromSeconds(2)), c.Cancellation);
                }

                var rows = await TruckRowsAsync(c, client, trucks.Values, c.Text("positions", "true") != "false");
                return OperationResult.Table($"{rows.Count} trucks on servers {groupServers}", TruckColumns, rows);
            },
            ps:
            [
                new OperationParam("refreshes", "Extra list calls", "number", "5", Help: "Each deals another 15 random trucks; stops early when nothing new comes."),
                new OperationParam("positions", "Look up positions", "text", "true"),
            ]);

        Add("truck.share", "Chat", "Send a truck's position",
            "Sends a trade truck to a player by DM as the game's own truck share: tapping it goes to wherever the truck is by then.",
            async c =>
            {
                var client = c.Require();
                var uid = c.Text("uid").Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(uid, "^[0-9]{6,20}$")) throw new ArgumentException("uid: digits only");
                var server = c.Int("server", 0);
                var march = c.Text("marchUid").Trim();
                var pointId = march.Length > 0 ? await TruckPositionAsync(client, server, march, c.Cancellation) : 0;
                int x = c.Int("x", -1), y = c.Int("y", -1);
                var fresh = pointId > 0;
                if (fresh) (x, y) = ((pointId - 1) % 1000, (pointId - 1) / 1000);
                if (server <= 0 || x < 0 || y < 0) throw new ArgumentException("no position for that truck (has it arrived?)");
                // The game's own truck share (post 46): tapping it follows the truck, so it stays right as the truck moves.
                var r = long.TryParse(march, out var mu) && mu > 0
                    ? await DirectMessage.ShareTruckAsync(client, uid, server, x, y, mu, c.Int("quality", 5), c.Text("owner", $"#{server}"), c.Cancellation)
                    : await DirectMessage.SharePointAsync(client, uid, server, x, y, c.Text("owner", $"Truck #{server} {x}:{y}"), null, c.Cancellation);
                return OperationResult.Text($"{r.Summary} ({(fresh ? "position looked up now" : "position from the scan")}: #{server} {x}:{y})");
            },
            writes: true,
            ps:
            [
                new OperationParam("uid", "Recipient uid", "text", Required: true),
                new OperationParam("server", "Truck's server", "number", Required: true),
                new OperationParam("marchUid", "Truck march uid", "text"),
                new OperationParam("x", "Fallback x", "number"), new OperationParam("y", "Fallback y", "number"),
                new OperationParam("quality", "Truck quality (1-5, 10)", "number", "5"),
                new OperationParam("owner", "Owner label (#server [tag]name)", "text"),
            ]);
    }

    /// <summary>Research: sweeps the account's own map and saves every inbound frame by command (pushes included),
    /// to find where moving marches (trucks) arrive. data/raw/map_frames_<server>.json. Stores nothing.</summary>
    static void AddMapFramesProbe() =>
        Add("probe.map.frames", "Research", "Map sweep: every frame that comes back",
            "Sweeps a server's map and saves every inbound frame by command (pushes too) with samples - to find march/truck data.",
            async c =>
            {
                var client = c.Require();
                var server = c.Int("server", client.Server.Value);
                var counts = new Dictionary<string, int>();
                var samples = new System.Text.Json.Nodes.JsonObject();
                void Seen(FrameRecord f)
                {
                    if (f.Outbound) return;
                    counts[f.Command] = counts.GetValueOrDefault(f.Command) + 1;
                    if (f.Command != "world.get.block" && (samples[f.Command] as System.Text.Json.Nodes.JsonArray ?? (System.Text.Json.Nodes.JsonArray)(samples[f.Command] = new System.Text.Json.Nodes.JsonArray())) is { Count: < 5 } a)
                        a.Add(SfsJson.Write(f.Params));
                }
                var keys = new Dictionary<string, int>();
                client.Connection.Frame += Seen;
                try
                {
                    var scanner = new LastWar.Client.Scanning.MapScanner(client, MapOptions(c))
                    {
                        OnReply = r =>
                        {
                            foreach (var b in r.TryGetArray("serverPointArr", out var arr) ? arr.Objects().ToList() : [r])
                                foreach (var k in b.Keys) keys[k] = keys.GetValueOrDefault(k) + 1;
                        },
                    };
                    await scanner.ScanAsync(new LastWar.Core.Model.ServerId(server), null, c.Cancellation);
                    await Task.Delay(TimeSpan.FromSeconds(c.Int("listen", 10)), c.Cancellation);
                }
                finally { client.Connection.Frame -= Seen; }
                var path = Path.Combine(c.DataDir, "raw", $"map_frames_{server}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(path, new System.Text.Json.Nodes.JsonObject
                {
                    ["counts"] = System.Text.Json.JsonSerializer.SerializeToNode(counts), ["blockKeys"] = System.Text.Json.JsonSerializer.SerializeToNode(keys), ["samples"] = samples,
                }.ToJsonString());
                return OperationResult.Text($"{string.Join(", ", counts.Select(kv => $"{kv.Key} x{kv.Value}"))} | block keys: {string.Join(" ", keys.Keys)} -> {path}");
            },
            ps: [new OperationParam("server", "Server", "number"), new OperationParam("listen", "Listen after (s)", "number", "10")]);

    static readonly string[] TruckColumns = ["uuid", "server", "owner", "uid", "tag", "quality", "level", "type", "completeness", "robbed", "power", "arrivesIn", "goods", "resources", "x", "y", "marchUid", "cargo"];

    /// <summary>The cargo as data for the page (value and content filters): [{id, name, n}] items, resources by name.</summary>
    static string CargoJson(LastWar.Storage.GameText text, JsonElement t)
    {
        var items = new System.Text.Json.Nodes.JsonArray();
        var res = new System.Text.Json.Nodes.JsonObject();
        foreach (var key in new[] { "extraGoods", "baseGoods" })
        {
            if (!t.TryGetProperty(key, out var g) || !g.TryGetProperty("cur", out var cur)) continue;
            foreach (var e in cur.EnumerateArray())
            {
                if (!e.TryGetProperty("value", out var v)) continue;
                var type = (int)Num(e, "type");
                if (v.ValueKind == JsonValueKind.Object)
                {
                    var id = Str(v, "id");
                    var have = items.FirstOrDefault(i => (string)i!["id"]! == id);
                    if (have is not null) have["n"] = (long)have["n"]! + Num(v, "num");
                    else items.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["name"] = text.Reward(type, id, 1), ["n"] = Num(v, "num") });
                }
                else
                {
                    var name = TruckResources.GetValueOrDefault(type, $"resource {type}");
                    res[name] = (res[name] is { } x ? (long)x : 0) + v.GetInt64();
                }
            }
        }
        return new System.Text.Json.Nodes.JsonObject { ["items"] = items, ["res"] = res }.ToJsonString();
    }

    /// <summary>One row per truck (train.list entry or a map march's "train" object - the same shape), position looked up live.</summary>
    static async Task<List<object?[]>> TruckRowsAsync(OperationContext c, LastWarClient client, IEnumerable<JsonElement> trucks, bool withPos)
    {
        var text = Text(c);
        var rows = new List<object?[]>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var t in trucks)
        {
            var server = (int)Num(t, "serverId");
            var march = Str(t, "marchUid");
            var cfg = text.TruckConfig(Num(t, "cfgId"));
            t.TryGetProperty("marchInfo", out var mi);
            var pointId = withPos && march is { Length: > 0 } and not "0" ? await TruckPositionAsync(client, server, march, c.Cancellation) : 0;
            if (withPos) await Task.Delay(Jitter.Of(TimeSpan.FromMilliseconds(400)), c.Cancellation);
            rows.Add([
                Str(t, "uuid"), server, Str(t, "name"), Str(t, "uid"), Str(t, "abbr"),
                cfg.Quality, Num(t, "level"), Num(t, "type"),
                Math.Round(t.TryGetProperty("completeness", out var cp) ? cp.GetDouble() : 1, 2),
                mi.ValueKind == JsonValueKind.Object ? Num(mi, "robTimes") : 0,
                mi.ValueKind == JsonValueKind.Object ? Num(mi, "power") : 0,
                (Num(t, "arriveTime") - now) / 1000,
                Goods(text, t, "extraGoods"), Goods(text, t, "baseGoods"),
                pointId > 0 ? (pointId - 1) % 1000 : "", pointId > 0 ? (pointId - 1) / 1000 : "", march, CargoJson(text, t),
            ]);
        }
        return rows;
    }

    /// <summary>
    /// Trucks on ONE server's map, from a client-shaped map sweep: every world.get.block also brings
    /// push.world.march.world.get.new, and trucks are its proto marches (isProto, mt 14) carrying the full "train" object
    /// (owner, cargo, quality... as in train.list). Trucks from other servers driving across this map count too
    /// (probe.map.aoi, 2026-10-07). Another server's map needs an account past the HQ 13 cross-server gate.
    /// </summary>
    static void AddTruckMapOperation() =>
        Add("trucks.map", "Scan", "Trucks on a server's map",
            "Sweeps one server's map the way the game client does and lists every trade truck driving on it, with cargo, "
            + "quality, owner and where it is. Stores nothing.",
            async c =>
            {
                var client = c.Require();
                var server = c.Int("server", client.Server.Value);
                var trucks = new Dictionary<string, JsonElement>();
                var seenTypes = new Dictionary<long, int>();
                var pushes = 0;
                void Seen(FrameRecord f)
                {
                    if (f.Outbound || f.Command != "push.world.march.world.get.new") return;
                    pushes++;
                    foreach (var s in Json(f.Params).GetProperty("serverMarchArr").EnumerateArray())
                        if (Num(s, "serverId") == server && s.TryGetProperty("marchInfos", out var mi))   // this server's map only
                            foreach (var m in mi.EnumerateArray())
                            {
                                seenTypes[Num(m, "type")] = seenTypes.GetValueOrDefault(Num(m, "type")) + 1;
                                if (m.TryGetProperty("train", out var t)) trucks[Str(t, "uuid")] = t.Clone();   // proto marches (mt 14) carrying a "train" = trucks
                            }
                }
                client.Connection.Frame += Seen;
                try
                {
                    // Closest zoom (lod 0, blocks of 10): the server sends far fewer marches at lod 1 (#390: 9 trucks at lod 0, 1 at lod 1).
                    await new LastWar.Client.Scanning.MapScanner(client, MapOptions(c) with { BlockSize = 10, Lod = 0 }).ScanAsync(new LastWar.Core.Model.ServerId(server), null, c.Cancellation);
                    await Task.Delay(TimeSpan.FromSeconds(2), c.Cancellation);   // the last pushes trail the last reply
                }
                finally { client.Connection.Frame -= Seen; }
                var rows = await TruckRowsAsync(c, client, trucks.Values, c.Text("positions", "true") != "false");
                return OperationResult.Table($"#{server}: {rows.Count} trucks on the map ({pushes} march pushes: {string.Join(", ", seenTypes.Select(kv => $"type {kv.Key} x{kv.Value}"))})", TruckColumns, rows);
            },
            ps:
            [
                new OperationParam("server", "Server", "number", Required: true),
                new OperationParam("positions", "Look up positions", "text", "true"),
            ]);

    static async Task<int> TruckPositionAsync(LastWarClient client, int server, string marchUid, CancellationToken ct)
    {
        if (!long.TryParse(marchUid, out var m)) return 0;
        var p = new SfsObject();
        p.PutInt("serverId", server);
        p.PutInt("worldId", 0);
        p.PutLong("marchUuid", m);
        p.PutInt("marchType", 2);   // NewMarchType.TRAIN
        var reply = await client.TryCallAsync("get.march.pos", p, TimeSpan.FromSeconds(8), ct);
        return reply is not null && reply.Has("positionId") ? (int)Num(Json(reply), "positionId") : 0;
    }

    /// <summary>A truck's cargo, "cur" (what is left after looting): items by name, resources by kind.</summary>
    static string Goods(LastWar.Storage.GameText text, JsonElement t, string key)
    {
        if (!t.TryGetProperty(key, out var g) || !g.TryGetProperty("cur", out var cur)) return "";
        // Same item in several slots = one line with the total.
        var parts = cur.EnumerateArray().Where(e => e.TryGetProperty("value", out _)).Select(e =>
        {
            var type = (int)Num(e, "type");
            var v = e.GetProperty("value");
            return v.ValueKind == JsonValueKind.Object ? (type, id: Str(v, "id"), n: Num(v, "num")) : (type, id: "", n: v.GetInt64());
        }).GroupBy(p => (p.type, p.id)).Select(g2 => (g2.Key.type, g2.Key.id, n: g2.Sum(p => p.n)));
        return string.Join(", ", parts.Select(p => p.id.Length > 0 ? text.Reward(p.type, p.id, p.n)
            : $"{TruckResources.GetValueOrDefault(p.type, $"resource {p.type}")} {p.n:N0}"));
    }
}
