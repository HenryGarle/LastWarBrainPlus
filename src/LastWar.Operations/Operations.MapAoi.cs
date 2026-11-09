using System.Text.Json;
using LastWar.Client;
using LastWar.Protocol.Sfs;

namespace LastWar.Operations;

public static partial class Operations
{
    /// <summary>
    /// Research (guest only): asks for a small patch of map around a known truck exactly as the game client does
    /// at closest zoom (WorldPointManager.SendAoiRequest: lod 0 -> blockSize 10 from LWAoiBlockSizeArray {10, 20,
    /// 1000}, x/y = the camera's tile, leftBottom/rightTop = the view corners, at most 160 block indices) and
    /// reports whether the truck's march comes back in push.world.march.world.get.new. Two index layouts are tried:
    /// blocks per row = server size / blockSize (what our scanner does) and 3000 / blockSize (what the client code
    /// computes, RecalculateBlockArg(3000, ...)). Nothing stored; one small request per layout.
    /// </summary>
    /// <summary>Research (guest): the client's far-zoom request (lod 2, blockSize 1000, the 9 blocks of the
    /// 3000-tile world) - where on the big map this server's points are.</summary>
    static void AddMapOverviewProbe() =>
        Add("probe.map.overview", "Research", "Map overview (far zoom)",
            "One client-shaped far-zoom request over the whole 3000-tile world: which 1000-tile regions hold whose points. Guest only.",
            async c =>
            {
                var g = c.Require();
                var server = c.Int("server", g.Server.Value);
                var lines = new List<string>();
                foreach (var (size, lod) in new[] { (1000, 2), (20, 1) })
                {
                    var per = 3000 / size;
                    // lod 2: all 9 blocks; lod 1: one 160-block view in each 1000-tile region's centre (8 x 20 blocks).
                    var views = lod == 2 ? [[.. Enumerable.Range(0, 9)]]
                        : (from ry in Enumerable.Range(0, 3) from rx in Enumerable.Range(0, 3)
                           select (int[])[.. from yy in Enumerable.Range(ry * 50 + 21, 8) from xx in Enumerable.Range(rx * 50 + 17, 16) select yy * per + xx]).ToList();
                    var counts = new Dictionary<string, int>();
                    foreach (var idx in views)
                    {
                        int minX = idx.Min(i => i % per) * size, minY = idx.Min(i => i / per) * size;
                        int maxX = Math.Min((idx.Max(i => i % per) + 1) * size - 1, 2999), maxY = Math.Min((idx.Max(i => i / per) + 1) * size - 1, 2999);
                        var o = new SfsObject();
                        o.PutInt("_id", 8000 + idx[0]);
                        o.PutInt("bigMap", 1);
                        o.PutInt("x", (minX + maxX) / 2);
                        o.PutInt("y", (minY + maxY) / 2);
                        o.PutInt("serverId", server);
                        o.PutInt("worldId", 0);
                        o.PutInt("type", 0);
                        o.PutInt("viewLvl", lod);
                        o.PutInt("timeStamp", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        o.PutIntArray("index", idx);
                        o.PutInt("blockSize", size);
                        o.PutInt("leftBottom", minX + minY * 3000 + 1);
                        o.PutInt("rightTop", maxX + maxY * 3000 + 1);
                        await g.Connection.SendCommandAsync("world.get.block", o);
                        try
                        {
                            var reply = (await g.Connection.WaitForCommandAsync(TimeSpan.FromSeconds(8), c.Cancellation, "world.get.block")).Params;
                            foreach (var b in reply.TryGetArray("serverPointArr", out var arr) ? arr.Objects().ToList() : [reply])
                                if (b.TryGetArray("points", out var pts))
                                    foreach (var bytes in pts.ByteArrays())
                                    {
                                        var p = LastWar.Client.Proto.WorldPointInfo.Parser.ParseFrom(bytes);
                                        var k = $"region {minX / 1000},{minY / 1000} srv {p.ServerId} pid<{(p.Id > 1_000_000 ? "big" : "1M")}";
                                        counts[k] = counts.GetValueOrDefault(k) + 1;
                                    }
                        }
                        catch (TimeoutException) { counts[$"region {minX / 1000},{minY / 1000} no reply"] = 1; }
                        await Task.Delay(Jitter.Of(TimeSpan.FromSeconds(1)), c.Cancellation);
                    }
                    lines.Add($"lod {lod}: " + string.Join("; ", counts.OrderBy(k => k.Key).Select(kv => $"{kv.Key}: {kv.Value}")));
                }
                return OperationResult.Text(string.Join(" || ", lines));
            },
            ps: [new OperationParam("server", "Server", "number")]);

    static void AddMapAoiProbe() =>
        Add("probe.map.aoi", "Research", "Map patch around a truck (client-shaped request)",
            "Finds a truck on this guest's server, then requests the map around it the way the game client does at "
            + "closest zoom, and reports whether the truck appears among the marches. Guest only.",
            async c =>
            {
                var g = c.Require();
                var own = g.Server.Value;
                // A truck on our own server, and where it is.
                var trucks = new List<JsonElement>();
                for (var i = 0; i < 4 && trucks.Count == 0; i++)
                {
                    var p = new SfsObject();
                    p.PutBool("isRefresh", i > 0);
                    var list = Json((await g.TryCallAsync("train.list", p, TimeSpan.FromSeconds(10), c.Cancellation))!);
                    trucks = [.. list.GetProperty("ls").EnumerateArray().Where(t => Num(t, "serverId") == own)];
                    if (trucks.Count == 0) await Task.Delay(Jitter.Of(TimeSpan.FromSeconds(2)), c.Cancellation);
                }
                if (trucks.Count == 0) return OperationResult.Text($"no truck on #{own} in this list");
                var truck = trucks[0];
                var march = Str(truck, "marchUid");
                var pos = await TruckPositionAsync(g, own, march, c.Cancellation);
                if (pos <= 0) return OperationResult.Text("no position for the truck");
                int tx = (pos - 1) % 1000, ty = (pos - 1) / 1000;
                await Task.Delay(Jitter.Of(TimeSpan.FromSeconds(2)), c.Cancellation);

                var lines = new List<string> { $"truck {march} of {Str(truck, "name")} at #{own} {tx}:{ty}" };
                var radius = c.Int("radius", 30);
                foreach (var (size, lod) in new[] { (10, 0), (20, 1) })
                {
                    const int tiles = 3000;
                    var perRow = tiles / size;
                    var idx = new List<int>();
                    for (var by = Math.Max(0, (ty - radius) / size); by <= (ty + radius) / size; by++)
                        for (var bx = Math.Max(0, (tx - radius) / size); bx <= (tx + radius) / size; bx++)
                            idx.Add(by * perRow + bx);
                    var marches = new Dictionary<long, string>();
                    var truckJson = "";
                    var points = 0;
                    void Seen(FrameRecord f)
                    {
                        if (f.Outbound || f.Command != "push.world.march.world.get.new") return;
                        var j = Json(f.Params);
                        foreach (var s in j.GetProperty("serverMarchArr").EnumerateArray())
                        {
                            if (s.TryGetProperty("uuidSet", out var us)) foreach (var u in us.EnumerateArray()) marches.TryAdd(u.GetInt64(), "?");
                            if (s.TryGetProperty("marchInfos", out var mi))
                                foreach (var m in mi.EnumerateArray()) { marches[Num(m, "uuid")] = $"type {Num(m, "type")}"; if (Str(m, "uuid") == march || (Num(m, "type") is not 2 and not 3 && truckJson.Length == 0)) truckJson = m.ToString(); }
                        }
                    }
                    g.Connection.Frame += Seen;
                    try
                    {
                        var o = new SfsObject();
                        o.PutInt("_id", 7000 + tiles);
                        o.PutInt("bigMap", 1);
                        o.PutInt("x", tx);
                        o.PutInt("y", ty);
                        o.PutInt("serverId", own);
                        o.PutInt("worldId", 0);
                        o.PutInt("type", 0);
                        o.PutInt("viewLvl", lod);
                        o.PutInt("timeStamp", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        o.PutIntArray("index", [.. idx.Take(160)]);
                        o.PutInt("blockSize", size);
                        o.PutInt("clearUuidSet", 1);
                        o.PutBool("force", true);
                        int lx = Math.Max(0, tx - radius), ly = Math.Max(0, ty - radius), rx = Math.Min(tiles - 1, tx + radius), ry = Math.Min(tiles - 1, ty + radius);
                        o.PutInt("leftBottom", lx + ly * tiles + 1);
                        o.PutInt("rightTop", rx + ry * tiles + 1);
                        await g.Connection.SendCommandAsync("world.get.block", o);
                        try
                        {
                            var reply = (await g.Connection.WaitForCommandAsync(TimeSpan.FromSeconds(8), c.Cancellation, "world.get.block")).Params;
                            foreach (var b in reply.TryGetArray("serverPointArr", out var arr) ? arr.Objects().ToList() : [reply])
                                if (b.TryGetArray("points", out var pts)) points += pts.ByteArrays().Count();
                        }
                        catch (TimeoutException) { lines.Add($"lod {lod}: no reply"); continue; }
                        await Task.Delay(TimeSpan.FromSeconds(3), c.Cancellation);
                    }
                    finally { g.Connection.Frame -= Seen; }
                    var hit = long.TryParse(march, out var mu) && marches.ContainsKey(mu);
                    if (truckJson.Length > 0) lines.Add("truck march: " + truckJson);
                    lines.Add($"lod {lod}/size {size} ({idx.Count} blocks): {points} points, {marches.Count} marches "
                        + $"[{string.Join(", ", marches.Values.GroupBy(v => v).Select(x => $"{x.Key} x{x.Count()}"))}], truck {(hit ? "FOUND" : "not seen")}");
                    await Task.Delay(Jitter.Of(TimeSpan.FromSeconds(3)), c.Cancellation);
                }
                File.WriteAllText(Path.Combine(c.DataDir, "raw", $"map_aoi_{own}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt"), string.Join("\n", lines));
                return OperationResult.Text(string.Join(" | ", lines));
            },
            ps: [new OperationParam("radius", "Tiles around the truck", "number", "30")]);
}
