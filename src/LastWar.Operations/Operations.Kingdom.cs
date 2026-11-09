using System.Text.Json;
using LastWar.Client;
using LastWar.Protocol.Sfs;
using LastWar.Storage;

namespace LastWar.Operations;

public static partial class Operations
{
    // Titles come from the game's government table + strings (assets.extract): 10001 President,
    // 10002 Vice President, 10003-10007 Secretaries of Strategy, Defense, Development, Science, Interior.
    static GameText? _text;
    internal static GameText Text(OperationContext c) =>
        _text ??= new GameText(Path.Combine(c.DataDir, "assets"));
    static string PositionName(OperationContext c, string id) => Text(c).PositionName(id);

    static JsonElement Json(SfsObject o) => JsonSerializer.Deserialize<JsonElement>(SfsJson.Write(o).ToJsonString());
    static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ToString() : "";
    static long Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : 0;
    static string Ms(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("u") : "";

    static void AddKingdomOperations()
    {
        Add("mail.probe", "Mail", "Probe mail (raw)",
            "Research: lists a throwaway guest's own mail and saves every raw entry under data/raw.",
            async c =>
            {
                var n = 0;
                string path = "";
                await WithGuestAsync(c, "mail", async g =>
                {
                    var (mail, _) = await new LastWar.Client.Game.MailActions(g).FetchAllAsync(5, c.Cancellation);
                    n = mail.Count;
                    path = Path.Combine(c.DataDir, "raw", $"mail_guest_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                    File.WriteAllText(path, new System.Text.Json.Nodes.JsonArray([.. mail.Select(m => (System.Text.Json.Nodes.JsonNode)SfsJson.Write(m.Raw))])
                        .ToJsonString());
                });
                return OperationResult.Text($"{n} mail saved to {path}");
            },
            needsSession: false);

        Add("kingdom.positions", "Government", "Ministry positions (who holds them)",
            "The President and every minister on each server in the range, with alliance, power and "
            + "when they were appointed. Cross-server, so it runs on a throwaway guest. Every run is "
            + "stored, so the holders can be followed over time.",
            async c =>
            {
                var at = DateTimeOffset.UtcNow;
                var found = new List<KingdomStore.PositionRow>();
                var missed = new List<int>();
                await WithGuestAsync(c, "kingdom", async g =>
                {
                    foreach (var server in ParseServers(c.Text("servers", "1967")))
                    {
                        var p = new SfsObject();
                        p.PutInt("serverId", server);
                        var reply = await g.TryCallAsync("get.kingdom.positions", p, TimeSpan.FromSeconds(10), c.Cancellation);
                        if (reply is null || !reply.Has("positions")) { missed.Add(server); continue; }
                        foreach (var e in Json(reply).GetProperty("positions").EnumerateArray())
                            found.Add(new(at, server, Str(e, "positionId"), Str(e, "uid"), Str(e, "name"), Str(e, "allianceId"),
                                Str(e, "abbr"), Num(e, "power"), Num(e, "appointTime")));
                        c.Report($"#{server}: {found.Count(f => f.Server == server)} positions held");
                        await Task.Delay(c.Int("gap", 300), c.Cancellation);
                    }
                });
                var store = new KingdomStore(c.Database);
                store.EnsureSchema();
                store.Insert(found);
                var table = OperationTable.Of("Ministry positions", ["server", "position", "name", "alliance", "power", "appointed", "position id", "uid"],
                    found.OrderBy(f => f.Server).ThenBy(f => f.PositionId).Select(f => new object?[]
                    { f.Server, PositionName(c, f.PositionId), f.Name, f.Tag, f.Power, Ms(f.AppointedMs), f.PositionId, f.Uid })) with
                {
                    Brief = ["server", "position", "name", "alliance", "power", "appointed"],
                    Links = new Dictionary<string, string> { ["name"] = "player:uid", ["server"] = "server:" },
                };
                return new OperationResult($"{found.Count} positions held across {found.Select(f => f.Server).Distinct().Count()} server(s)"
                    + (missed.Count > 0 ? $"; no answer for {string.Join(",", missed)}" : ""), [], []) { Tables = [table] };
            },
            needsSession: false,
            ps:
            [
                new OperationParam("servers", "Servers", "text", "1967", Required: true, Help: "One server, a list or a range."),
                new OperationParam("gap", "Gap between servers (ms)", "number", "300"),
            ]);

        Add("kingdom.queue", "Government", "Ministry queue (appointments & applications)",
            "Who is queued for each minister position, who has applied, and who held it recently. The "
            + "game only answers these about the account's OWN server, so it needs an account that lives "
            + "there - a guest lands on a random server and sees that server's queue instead.",
            async c =>
            {
                var client = c.Require();
                var tables = new List<OperationTable>();
                var ids = c.Text("positions", "10002,10003,10004,10005,10006,10007").Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var (cmd, title, arrayKey) in new[]
                {
                    ("kingdom.position.appointment.list", "Queued", "applyArr"),
                    ("kingdom.position.apply.list", "Applied", "applyArr"),
                    ("kingdom.position.history.list", "Recent holders", "arr"),
                })
                {
                    var rows = new List<Dictionary<string, string>>();
                    foreach (var id in ids)
                    {
                        var p = new SfsObject();
                        p.PutUtfString("positionId", id.Trim());
                        var reply = await client.TryCallAsync(cmd, p, TimeSpan.FromSeconds(10), c.Cancellation);
                        if (reply is not null && Json(reply).TryGetProperty(arrayKey, out var arr) && arr.ValueKind == JsonValueKind.Array)
                            foreach (var (e, i) in arr.EnumerateArray().Select((e, i) => (e, i)))
                            {
                                var row = new Dictionary<string, string> { ["position"] = PositionName(c, id.Trim()), ["#"] = (i + 1).ToString() };
                                foreach (var prop in e.EnumerateObject())
                                    row[prop.Name] = prop.Name.EndsWith("Time", StringComparison.Ordinal) && prop.Value.TryGetInt64(out var ms) && ms > 1_000_000_000_000
                                        ? Ms(ms) : prop.Value.ToString();
                                rows.Add(row);
                            }
                        await Task.Delay(300, c.Cancellation);
                    }
                    // Columns are whatever the game sent - the entries are not fully mapped yet.
                    var lead = new[] { "position", "#", "name", "abbr", "uid" };
                    var cols = lead.Concat(rows.SelectMany(r => r.Keys).Distinct().Except(lead)
                        .Where(k => !k.StartsWith("head", StringComparison.Ordinal) && k is not ("pic" or "picver"))).ToList();
                    tables.Add(OperationTable.Of($"{title} - #{client.Server.Value}", cols,
                        rows.Select(r => cols.Select(k => (object?)r.GetValueOrDefault(k)))) with
                    { Links = new Dictionary<string, string> { ["name"] = "player:uid" } });
                }
                return new OperationResult($"ministry queue on #{client.Server.Value}: "
                    + string.Join(", ", tables.Select(t => $"{t.Rows.Count} {t.Title.Split(' ')[0].ToLowerInvariant()}")), [], []) { Tables = tables };
            },
            ps: [new OperationParam("positions", "Positions", "text", "10002,10003,10004,10005,10006,10007",
                Help: "Position ids; 10001 (President) has no queue.")]);

        Add("kingdom.probe", "Government", "Probe government commands",
            "Research: on a throwaway guest, asks the read-only government commands about one server "
            + "(positions, president, and for each position its appointment queue, applicants and "
            + "history) and saves every raw reply under data/raw. Changes nothing.",
            async c =>
            {
                var server = c.Int("server", 1967);
                var dump = new Dictionary<string, object?>();
                var rows = new List<object?[]>();
                await WithGuestAsync(c, "kingdom", async g =>
                {
                    async Task<SfsObject?> Ask(string cmd, SfsObject p)
                    {
                        var reply = await g.TryCallAsync(cmd, p, TimeSpan.FromSeconds(10), c.Cancellation);
                        var key = $"{cmd} {SfsJson.Write(p).ToJsonString()}";
                        dump[key] = reply is null ? null : JsonSerializer.Deserialize<JsonElement>(SfsJson.Write(reply).ToJsonString());
                        rows.Add([cmd, SfsJson.Write(p).ToJsonString(), reply is null ? "no answer" : string.Join(",", reply.Keys.Take(12))]);
                        c.Report($"{cmd}: {(reply is null ? "no answer" : "ok")}");
                        await Task.Delay(400, c.Cancellation);
                        return reply;
                    }
                    SfsObject S(params (string K, object V)[] kv)
                    {
                        var o = new SfsObject();
                        foreach (var (k, v) in kv)
                            if (v is int i) o.PutInt(k, i); else o.PutUtfString(k, v.ToString()!);
                        return o;
                    }

                    var positions = await Ask("get.kingdom.positions", S(("serverId", server)));
                    await Ask("get.king.info", S(("serverId", server), ("toInt", 0)));
                    await Ask("kingdom.position.countdown.get", new SfsObject());
                    // Every positionId that appears anywhere in the positions reply.
                    var ids = new HashSet<string>();
                    void Walk(JsonElement e)
                    {
                        if (e.ValueKind == JsonValueKind.Object)
                            foreach (var p in e.EnumerateObject())
                            {
                                if (p.NameEquals("positionId") || p.NameEquals("id")) ids.Add(p.Value.ToString());
                                Walk(p.Value);
                            }
                        else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
                    }
                    if (positions is not null) Walk(JsonSerializer.Deserialize<JsonElement>(SfsJson.Write(positions).ToJsonString()));
                    foreach (var id in ids.Take(c.Int("maxPositions", 12)))
                    {
                        await Ask("kingdom.position.appointment.list", S(("positionId", id)));
                        await Ask("kingdom.position.appointment.list", S(("positionId", id), ("serverId", server)));
                        await Ask("kingdom.position.apply.list", S(("positionId", id), ("serverId", server)));
                        await Ask("kingdom.position.history.list", S(("positionId", id)));
                    }
                    // The guest's own server, where the queue commands certainly apply.
                    var own = g.Server.Value;
                    await Ask("get.kingdom.positions", S(("serverId", own)));
                    foreach (var id in new[] { "10002", "10003", "10004" })
                        await Ask("kingdom.position.appointment.list", S(("positionId", id)));
                });
                var path = Path.Combine(c.DataDir, "raw", $"kingdom_{server}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
                return OperationResult.Table($"{rows.Count} calls, raw replies in {path}", ["command", "params", "reply keys"], rows);
            },
            needsSession: false,
            ps:
            [
                new OperationParam("server", "Server", "number", "1967", Required: true),
                new OperationParam("maxPositions", "Positions to follow up", "number", "12"),
            ]);
    }
}
