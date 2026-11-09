using System.Text.Json;
using System.Text.Json.Nodes;
using LastWar.Client;
using LastWar.Protocol.Sfs;

namespace LastWar.Operations;

/// <summary>
/// Research tool: runs a list of commands on a THROWAWAY GUEST and saves every reply, plus every
/// push that arrived while waiting, to data/raw/probe_&lt;set&gt;_&lt;time&gt;.json. The list lives in
/// data/probes/&lt;set&gt;.json as [{ "command": "..", "params": { .. } }]. Parameter types come from
/// the decompiled command catalogue (data/api/commands.csv: "Int:serverId; Long:uuid"), because
/// the server rejects an Int where a Long is expected. Placeholders: "$server" (the op's server
/// parameter), "$own" (the guest's own server), "$uid", "$alliance".
/// Guest only, by design - a probe sends commands nobody has tried yet.
/// </summary>
public static partial class Operations
{
    static readonly string[] NoisyPushes = ["push.chat", "push.world.", "push.march", "push.heart", "push.server.time"];

    static void AddProbeOperations()
    {
        Add("probe.run", "Research", "Probe commands (guest)",
            "Runs every call in data/probes/<set>.json on a throwaway guest; saves replies and pushes under data/raw.",
            async c =>
            {
                var dataDir = c.DataDir;
                var set = c.Text("set", "");
                var file = Path.Combine(dataDir, "probes", set + ".json");
                if (set.Length == 0 || !File.Exists(file)) return OperationResult.Text($"no probe set {file}");
                var calls = JsonNode.Parse(File.ReadAllText(file))!.AsArray();
                var types = CatalogueTypes(Path.Combine(dataDir, "api", "commands.csv"));
                var rows = new List<IReadOnlyList<string>>();
                var dump = new JsonArray();
                var wait = TimeSpan.FromMilliseconds(c.Int("waitMs", 1500));

                // zone=1967: create the guest ON that server (only works while it is open to new players).
                var zone = c.Text("zone", "") is { Length: > 0 } z ? (z.StartsWith("APS") ? z : "APS" + z) : null;
                var where = "";
                await WithGuestAsync(c, "probe_" + set, async g =>
                {
                    where = $"guest on server {g.Server.Value}";
                    dump.Add(new JsonObject { ["command"] = "(login reply)", ["reply"] = g.LoginReply is null ? null : SfsJson.Write(g.LoginReply) });
                    string Fill(string v) => v.Replace("$server", c.Text("server", "")).Replace("$own", g.Server.Value.ToString())
                        .Replace("$uid", c.Text("uid", "")).Replace("$alliance", c.Text("alliance", ""));
                    foreach (var call in calls)
                    {
                        c.Cancellation.ThrowIfCancellationRequested();
                        var cmd = call!["command"]!.GetValue<string>();
                        var ps = new SfsObject();
                        var shown = new JsonObject();
                        if (call["params"] is JsonObject jp)
                            foreach (var (k, v) in jp)
                            {
                                var raw = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? Fill(s) : v?.ToJsonString() ?? "";
                                Put(ps, k, raw, v is null ? null : JsonNode.Parse(Fill(v.ToJsonString())), types.GetValueOrDefault(cmd)?.GetValueOrDefault(k));
                                shown[k] = raw;
                            }

                        var pushes = new JsonArray();
                        void Seen(FrameRecord f)
                        {
                            if (f.Outbound || f.Command == cmd || NoisyPushes.Any(n => f.Command.StartsWith(n))) return;
                            lock (pushes) pushes.Add(new JsonObject { ["command"] = f.Command, ["params"] = SfsJson.Write(f.Params) });
                        }
                        g.Connection.Frame += Seen;
                        var started = DateTimeOffset.UtcNow;
                        SfsObject? reply;
                        try { reply = await g.TryCallAsync(cmd, ps, TimeSpan.FromSeconds(8), c.Cancellation); }
                        finally { await Task.Delay(wait, c.Cancellation); g.Connection.Frame -= Seen; }
                        var ms = (DateTimeOffset.UtcNow - started - wait).TotalMilliseconds;

                        var json = reply is null ? null : SfsJson.Write(reply);
                        var outcome = reply is null ? "no reply" : reply.Scalar("errorCode") is { Length: > 0 } code && code != "0" ? $"{code} {reply.Scalar("errorMsg")}".Trim() : "ok";
                        dump.Add(new JsonObject
                        {
                            ["command"] = cmd, ["params"] = shown, ["outcome"] = outcome, ["ms"] = Math.Round(ms),
                            ["reply"] = json, ["pushes"] = pushes,
                        });
                        rows.Add([cmd, shown.ToJsonString(), outcome,
                            json is JsonObject o ? string.Join(" ", o.Select(p => p.Key).Take(12)) : "",
                            json?.ToJsonString().Length.ToString() ?? "0",
                            string.Join(" ", pushes.Select(p => p!["command"]!.GetValue<string>()).Distinct())]);
                        await Task.Delay(250, c.Cancellation);
                    }
                }, zone);
                var path = Path.Combine(dataDir, "raw", $"probe_{set}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, dump.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
                return OperationResult.Table($"{where}: {rows.Count} calls, raw replies in {path}",
                    ["command", "params", "outcome", "reply keys", "bytes", "pushes"], rows);
            },
            needsSession: false,
            ps:
            [
                new OperationParam("set", "Probe set (data/probes/<set>.json)", "text", "", Required: true),
                new OperationParam("server", "Server ($server)", "number", "1967"),
                new OperationParam("uid", "Player uid ($uid)", "text", ""),
                new OperationParam("alliance", "Alliance id ($alliance)", "text", ""),
                new OperationParam("waitMs", "Wait for pushes (ms)", "number", "1500"),
                new OperationParam("zone", "Create the guest on this server (blank = wherever the gateway puts it)", "number", ""),
            ]);
    }

    /// <summary>command -> param -> SFS type name, from the catalogue's "Int:serverId; Long:uuid".</summary>
    static Dictionary<string, Dictionary<string, string>> CatalogueTypes(string csv)
    {
        var map = new Dictionary<string, Dictionary<string, string>>();
        if (!File.Exists(csv)) return map;
        foreach (var line in File.ReadLines(csv).Skip(1))
        {
            var cols = line.Split(',');
            if (cols.Length < 4) continue;
            var ps = new Dictionary<string, string>();
            foreach (var p in cols[3].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (p.Split(':') is [var t, var n]) ps[n] = t;
            map[cols[0]] = ps;
        }
        return map;
    }

    static void Put(SfsObject o, string key, string raw, JsonNode? original, string? type)
    {
        switch (type)
        {
            case "Int": o.PutInt(key, int.Parse(raw)); break;
            case "Long": o.PutLong(key, long.Parse(raw)); break;
            case "Bool": o.PutBool(key, raw is "true" or "1"); break;
            case "UtfString": o.PutUtfString(key, raw); break;
            default:
                // Unknown to the catalogue (or an array/object): let the JSON shape decide.
                var one = SfsJson.Parse(new JsonObject { [key] = original is JsonValue v && v.TryGetValue<string>(out _) ? raw : original?.DeepClone() }.ToJsonString());
                foreach (var k in one.Keys) if (one.TryGet(k, out var val)) o.Put(k, val);
                break;
        }
    }
}
