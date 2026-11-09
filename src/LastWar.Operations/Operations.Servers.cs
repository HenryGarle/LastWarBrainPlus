using System.Text.Json.Nodes;
using LastWar.Client;
using LastWar.Protocol.Sfs;
using LastWar.Storage;

namespace LastWar.Operations;

/// <summary>
/// Every server, at the level the Tracking page sets (ServerRegistry):
///   servers.tables    the game's own tables -> server wars, event groups, season configs (no network)
///   servers.registry  guest: cross.server.ls (all 2,500+ servers), open times, every king + badge
///   servers.basic     guest: season-zone readings for servers tracked basic or full
///   servers.own       an account: what only a server's own players can read (season timeline,
///                     season group, war-zone badge servers, transfer group) - for the account's server
/// </summary>
public static partial class Operations
{
    static async Task<JsonNode?> CallJson(LastWarClient g, string cmd, SfsObject p, CancellationToken ct)
    {
        var reply = await g.TryCallAsync(cmd, p, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return reply is null ? null : SfsJson.Write(reply);
    }

    static long? N(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (long)d
        : v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) ? p : null) : null;
    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString().Trim('"') ?? "";
    static string Iso(long? ms) => ms is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms.Value).ToString("O") : "";

    static void AddServerOperations()
    {
        Add("servers.tables", "Servers", "Load the game's server tables",
            "Reads lw_zone_war (server-vs-server wars), yuntie_battle_server_group_new (cross-server event groups) and lw_season (season dates) "
            + "into the database. First fetches the game's live config table from its CDN when a newer one is out (what the app does at launch - no account).",
            async c =>
            {
                string fetched;
                try { fetched = await GameTables.UpdateAsync(c.DataDir, c.Cancellation); }
                catch (Exception ex) when (ex is not OperationCanceledException) { fetched = "live table not fetched: " + ex.Message; }   // the tables we have still load
                var reg = new ServerRegistry(c.Database);
                var (w, g, s) = reg.IngestTables(Path.Combine(c.DataDir, "assets", "tables"));
                reg.SeedTracking();
                var ev = new EventCalendar(c.Database).Seed(reg, DateTime.UtcNow);   // daily: next weeks' Meteorite rounds, boss spawns, wars
                return OperationResult.Text($"{fetched}; {w} server wars, {g} event groups, {s} season configs loaded; {reg.Blocks().Count} server blocks; {ev} calendar events seeded");
            },
            needsSession: false, writes: true);

        Add("servers.registry", "Servers", "Server registry (every server)",
            "One throwaway guest: the full server list, open times for servers that have none yet (openTimes per run), "
            + "and every server's president and badge (the game's API says king; a badge change marks the end of a season or war the server took part in).",
            async c =>
            {
                var reg = new ServerRegistry(c.Database);
                reg.EnsureSchema();
                var gap = TimeSpan.FromMilliseconds(c.Int("gap", 80));
                string summary = "";
                await WithGuestAsync(c, "registry", async g =>
                {
                    var ls = await CallJson(g, "cross.server.ls", new SfsObject(), c.Cancellation);
                    var list = (ls?["list"] as JsonArray ?? []).OfType<JsonObject>()
                        .Select(o => ((int)(N(o["id"]) ?? 0), Str(o["name"]), (int)(N(o["server_type"]) ?? 0))).Where(x => x.Item1 > 0).ToList();
                    if (list.Count == 0) throw new InvalidOperationException("cross.server.ls returned no servers");
                    var added = reg.UpsertList(list);
                    c.Report($"{list.Count} servers listed, {added} new");

                    var opened = 0;
                    var asked = 0;
                    foreach (var s in reg.MissingOpenTime(c.Int("openTimes", 600)))
                    {
                        if (++asked % 100 == 0) c.Report($"open times: {asked} asked, {opened} found");
                        var p = new SfsObject(); p.PutInt("server", s);
                        if (N((await CallJson(g, "get.other.server.info", p, c.Cancellation))?["openTime"]) is > 0 and var ms) { reg.SetOpenTime(s, ms); opened++; }
                        await Task.Delay(LastWar.Client.Jitter.Of(gap), c.Cancellation);
                    }

                    var kings = new List<ServerRegistry.KingReading>();
                    foreach (var batch in reg.AllServers().Chunk(120))
                    {
                        var p = new SfsObject(); p.PutUtfString("serverIds", string.Join(",", batch));
                        var r = await CallJson(g, "get.cross.server.king.info", p, c.Cancellation);
                        foreach (var s in batch)
                        {
                            var k = r?["serverKing"]?[s.ToString()] as JsonObject;
                            var b = r?["serverInfo"]?[s.ToString()] as JsonObject;
                            if (k is null && b is null) continue;
                            kings.Add(new(s, Str(k?["uid"]), Str(k?["name"]), Str(k?["allianceAbbr"]), Str(k?["allianceName"]), N(k?["power"]) ?? 0,
                                Iso(N(k?["beKingTime"])), Str(b?["cfgId"]), Iso(N(b?["createTime"]))));
                        }
                        await Task.Delay(LastWar.Client.Jitter.Of(gap), c.Cancellation);
                    }
                    var changes = reg.UpsertKings(kings);
                    summary = $"{list.Count} servers ({added} new), {opened} open times added, {kings.Count} kings/badges read, {changes} changes logged";
                });
                return OperationResult.Text(summary);
            },
            needsSession: false, writes: true,
            ps: [new OperationParam("openTimes", "Open times to fetch per run", "number", "600"), new OperationParam("gap", "Gap between requests (ms)", "number", "80")]);

        Add("servers.basic", "Servers", "Season zones (tracked servers)",
            "One throwaway guest: for each server, its season-zone standing (force rank, top alliances) and cross-server war side and cities. "
            + "Default @basic = every server tracked basic or full.",
            async c =>
            {
                var reg = new ServerRegistry(c.Database);
                var servers = ParseServers(c.Text("servers", "@basic"));
                var gap = TimeSpan.FromMilliseconds(c.Int("gap", 80));
                var done = 0;
                await WithGuestAsync(c, "zones", async g =>
                {
                    foreach (var s in servers)
                    {
                        var p = new SfsObject(); p.PutInt("server", s);
                        var z = await CallJson(g, "get.zone.intelligence", p, c.Cancellation);
                        var q = new SfsObject(); q.PutInt("targetServerId", s);
                        var w = await CallJson(g, "get.cross.declare.war.server.info", q, c.Cancellation);
                        var top = new JsonArray((z?["selfZoneInfo"] as JsonArray ?? []).OfType<JsonObject>().Select(a => (JsonNode)new JsonObject
                        {
                            ["id"] = Str(a["allianceId"]), ["tag"] = Str(a["abbr"]), ["name"] = Str(a["name"]), ["score"] = N(a["score"]), ["rank"] = N(a["allianceRank"]),
                        }).ToArray());
                        reg.AddZoneSample(s, N(z?["serverForceRank"]), N(z?["totalNum"]), N(w?["campId"]), N(w?["totalCityCount"]), N(w?["destroyedCityCount"]), top.ToJsonString());
                        if (++done % 25 == 0) c.Report($"{done}/{servers.Count} servers");
                        await Task.Delay(LastWar.Client.Jitter.Of(gap), c.Cancellation);
                    }
                });
                return OperationResult.Text($"{done} servers read");
            },
            needsSession: false, writes: true,
            ps: [new OperationParam("servers", "Servers", "text", "@basic", Help: "A range, a list, or @full / @basic / @visible."), new OperationParam("gap", "Gap between requests (ms)", "number", "80")]);

        Add("servers.season", "Servers", "Season group of every server",
            "One throwaway guest asks to start a new character on each server - the in-game 'create a character on server X' "
            + "(new_account). A server in a season refuses and names the season config it is on (\"target server not allow new user 1322 1\"); "
            + "servers on the same config are one season group (lw_season ids: our S2 was 1319-1326, S3 is 1497-1504). Found 2026-11-07. "
            + "Guests only. A server that accepts would give the guest a character there - it is never logged into.",
            async c =>
            {
                var reg = new ServerRegistry(c.Database);
                var servers = ParseServers(c.Text("servers", "@full"));
                var gap = TimeSpan.FromMilliseconds(c.Int("gap", 3000));
                var rows = new List<object?[]>();
                await WithGuestAsync(c, "season", async g =>
                {
                    foreach (var s in servers)
                    {
                        var p = new SfsObject(); p.PutInt("confirm", 1); p.PutInt("specify", 1); p.PutInt("targetServer", s);
                        var r = await CallJson(g, "new_account", p, c.Cancellation);
                        var msg = Str(r?["errorMsg"]);
                        var m = System.Text.RegularExpressions.Regex.Match(msg, @"not allow new user (\d+)");
                        if (m.Success) { var cfg = long.Parse(m.Groups[1].Value); reg.NoteSeasonCfg(s, cfg); rows.Add([s, cfg, msg]); }
                        else rows.Add([s, null, r?["loginKey"] is not null ? "open to new characters" : msg.Length > 0 ? msg : r?.ToJsonString() ?? "no reply"]);
                        if (rows.Count % 16 == 0) c.Report($"{rows.Count}/{servers.Count} servers");
                        await Task.Delay(LastWar.Client.Jitter.Of(gap), c.Cancellation);
                    }
                });
                var groups = rows.Where(r => r[1] is long).GroupBy(r => (long)r[1]!).OrderBy(x => x.Key)
                    .Select(x => $"{x.Key}: {string.Join(",", x.Select(r => r[0]))}");
                return OperationResult.Table($"{rows.Count(r => r[1] is long)} of {rows.Count} servers named a season config - " + string.Join("; ", groups),
                    ["server", "season config", "reply"], rows);
            },
            needsSession: false, writes: true,
            ps: [new OperationParam("servers", "Servers", "text", "@full", Help: "A range, a list, or @full / @basic / @visible."),
                 new OperationParam("gap", "Gap between requests (ms)", "number", "3000", Help: "Jittered +-30%.")]);

        Add("servers.own", "Servers", "Own server's season facts",
            "What only a server's own players can read, for THIS account's server: its season timeline (get.season.infos), season group "
            + "(lw.season.trend.info), the servers in its war-zone badge (server.badges.get), its transfer group (season.migrate.activity.info) "
            + "and cross-server list. Stores them, and the raw replies under data/raw.",
            async c =>
            {
                var client = c.Require();
                var reg = new ServerRegistry(c.Database);
                reg.EnsureSchema();
                var own = client.Server.Value;
                var raw = new JsonObject { ["server"] = own, ["at"] = DateTimeOffset.UtcNow.ToString("O") };
                async Task<JsonNode?> Get(string cmd) { var r = await CallJson(client, cmd, new SfsObject(), c.Cancellation); raw[cmd] = r?.DeepClone(); await Task.Delay(150, c.Cancellation); return r; }

                var infos = await Get("get.season.infos");
                var trend = await Get("lw.season.trend.info");
                var badges = await Get("server.badges.get");
                var migrate = await Get("season.migrate.activity.info");
                var cross = await Get("get.player.cross.server.list");

                var group = N(trend?["season_group"]);
                var seasons = (infos?["seasonArr"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(s => (N(s["seasonId"]) ?? 0, N(s["cfgId"]) ?? 0, N(s["startTime"]) ?? 0, N(s["endTime"]) ?? 0)).Where(s => s.Item1 > 0).ToList();
                reg.AddSeasons(own, seasons, group);
                if (N(infos?["openTime"]) is > 0 and var open) reg.SetOpenTime(own, open);
                if (group is not null) reg.LogEvent(own, "season_group", group.ToString()!);
                var lines = new List<string> { $"#{own}: {seasons.Count} seasons (latest S{seasons.LastOrDefault().Item1}, cfg {seasons.LastOrDefault().Item2}), season group {group?.ToString() ?? "?"}" };
                foreach (var b in (badges?["ls"] as JsonArray ?? []).OfType<JsonObject>())
                    if (b["source"]?["servers"] is JsonArray srv && srv.Count > 0)
                    {
                        var ids = string.Join(",", srv.Select(x => N(x)));
                        reg.LogEvent(own, "warzone", ids, $"badge {Str(b["cfgId"])} since {Iso(N(b["createTime"]))}");
                        lines.Add($"badge {Str(b["cfgId"])} war zone: {ids}");
                    }
                if (migrate?["migrateServerList"] is JsonArray ms && ms.Count > 0)
                {
                    var ids = string.Join(",", ms.Select(x => x is JsonObject o ? N(o["serverId"] ?? o["id"]) : N(x)));
                    reg.LogEvent(own, "transfer_group", ids);
                    lines.Add($"transfer group: {ids}");
                }
                else lines.Add($"transfer group: {Str(migrate?["errorMsg"]) switch { "" => "none listed", var e => e }}");
                if (cross?["list"] is JsonObject cl) lines.Add($"cross-server list: {cl.ToJsonString()}");
                var file = Path.Combine(c.DataDir, "raw", $"servers_own_{own}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, raw.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return OperationResult.Text(string.Join("; ", lines));
            },
            accounts: AccountTier.Service | AccountTier.Privileged);
    }
}
