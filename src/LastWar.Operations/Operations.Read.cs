using LastWar.Client.Game;
using LastWar.Client.Scanning;
using LastWar.Core.Model;
using LastWar.Lua;
using LastWar.Protocol.Sfs;
using LastWar.Storage;

namespace LastWar.Operations;

public static partial class Operations
{
    /// <summary>The operations that read rather than change: rosters, sweeps, and the script dump.</summary>
    static void AddReadOperations()
    {
        Add("alliance.rank", "Alliance", "Alliance roster",
            "Every member of an alliance with their power - the cheapest leaderboard there is, "
            + "one request for a whole alliance.",
            async c =>
            {
                var id = c.Text("allianceId");
                if (id.Length == 0) throw new ArgumentException("an alliance id is required");
                var p = new SfsObject();
                p.PutUtfString("allianceId", id);
                var reply = await c.Require().CallAsync("al.rank", p, TimeSpan.FromSeconds(10), c.Cancellation);
                if (!reply.TryGetArray("list", out var list))
                    return OperationResult.Text("the alliance returned no roster");

                var rows = list.Objects()
                    .Select(m => new object?[]
                    {
                        m.GetString("name"), m.GetString("uid"), m.GetLong("power"),
                        m.GetInt("hqLevel"), m.GetLong("killArmy"),
                    })
                    .OrderByDescending(r => Convert.ToInt64(r[2]))
                    .ToList();
                return OperationResult.Table(
                    $"{rows.Count} members, {rows.Sum(r => Convert.ToInt64(r[2])):N0} total power",
                    ["name", "uid", "power", "hq", "kills"], rows);
            },
            ps: new OperationParam("allianceId", "Alliance id", "text", Required: true,
                Help: "From a map scan, or the Data page."));

        Add("scan.map", "Scan", "Scan the map",
            "Sweeps a server's world blocks and stores every occupied tile.",
            async c =>
            {
                var db = new LastWarDb(c.Database);
                db.EnsureSchema();
                // One way for every server (blocks of 10, lod 0); MapScanner picks the world width the game uses for it.
                var scanner = new MapScanner(c.Require(), MapOptions(c));
                var rows = new List<object?[]>();
                var ids = ParseServers(c.Text("servers", "1967")).ToList();
                // At most maxServers a run, the longest-unscanned first, so a range is covered over several days at a human
                // pace (lw_2 banned 2026-11-02 after weeks of 64 servers in ~20 min; the game had been closing the
                // connection ~10 minutes in every day before that).
                var cap = Math.Clamp(c.Int("maxServers", 10), 1, 1000);
                if (ids.Count > cap)
                {
                    using var dbc = LastWar.Storage.Db.Open(c.Database);
                    var last = LastWar.Storage.Db.Rows(dbc, "SELECT server_id, max(taken_at) AS at FROM map_scan GROUP BY server_id;")
                        .ToDictionary(r => Convert.ToInt32(r["server_id"]), r => (string)r["at"]!);
                    var picked = ids.OrderBy(i => last.GetValueOrDefault(i, ""), StringComparer.Ordinal).ThenBy(i => i).Take(cap).ToHashSet();
                    c.Report($"{ids.Count} servers asked for, scanning the {cap} longest unscanned: {string.Join(", ", picked.Order())}");
                    ids = [.. ids.Where(picked.Contains)];
                }
                var failedInARow = 0;
                foreach (var id in ids)
                {
                    // Two failures in a row = the session is gone or the game is refusing us: stop rather than send
                    // requests for every remaining server (2026-10-27: the connection dropped after #1972 and 48
                    // servers failed one after another while the run still read "refresh complete").
                    if (failedInARow >= 2)
                    {
                        rows.Add([id, 0, "not tried - stopped (the game closed the connection, or 2 failures in a row)"]);
                        continue;
                    }
                    var server = new ServerId(id);
                    var at = DateTimeOffset.UtcNow;
                    try
                    {
                        var tiles = await scanner.ScanAsync(server, new Progress<MapScanProgress>(p =>
                        {
                            if (p.Request % 50 == 0)
                                c.Report($"{server}: request {p.Request}/{p.TotalRequests}, {p.TilesFound} tiles");
                        }), c.Cancellation);
                        db.InsertMapScan(server, at, [.. tiles], scanner.LastFeatures, scanner.LastGreen);
                        rows.Add([server.Value, tiles.Count, $"stored, {scanner.LastFeatures.Count} alliance points"]);
                        failedInARow = 0;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
                    {
                        rows.Add([server.Value, 0, ex.Message]);
                        failedInARow++;
                        c.Report($"{server}: FAILED - {ex.Message}");
                        // The connection going away mid-scan is the game cutting us off: stop for the day, never push on.
                        if (ex is IOException) failedInARow = 2;
                    }
                }
                // Every server refused (e.g. cross_server_view_tips: HQ under 13) = the scan failed, not "0 players".
                var refused = rows.Where(r => r[2] is string s && !s.StartsWith("stored", StringComparison.Ordinal)).ToList();
                if (rows.Count > 0 && refused.Count == rows.Count)
                    throw new InvalidOperationException($"no server scanned - {refused[0][2]}");
                return OperationResult.Table(
                    $"{rows.Count - refused.Count} of {rows.Count} server(s) scanned, {rows.Sum(r => Convert.ToInt64(r[1])):N0} players found"
                    + (refused.Count > 0 ? $"; {refused.Count} not scanned - first: #{refused[0][0]} {refused[0][2]}" : ""),
                    ["server", "players", "result"], rows);
            },
            ps:
            [
                new OperationParam("servers", "Servers", "text", "1967", Required: true,
                    Help: "A list or a range: 1967, or 1957-1988."),
                new OperationParam("gap", "Gap between requests (ms)", "number", "1000", Help: "Jittered +-30%. Lod 0 is ~79 requests a server. 1 s = a person panning (2026-11-02)."),
                new OperationParam("maxServers", "Servers at most per run", "number", "10", Help: "The longest-unscanned first; the rest wait for the next run."),
                new OperationParam("blockSize", "Block size (tiles)", "number", "10",
                    Help: "10 (lod 0, every base) or 20 (lod 1 - the zoomed-out view, which leaves out about two thirds of the bases on other servers)."),
                new OperationParam("inFlight", "Requests in flight", "number", "1",
                    Help: "Requests outstanding at once. Hides round-trip time; send rate is still set by the gap."),
            ]);

        Add("scan.map.probe", "Scan", "Map reply probe",
            "Scans ONE server and writes everything world.get.block returns beside the bases - lands, "
            + "alliance points, deserts - decoded, to data/raw/map_probe_<server>.json. Stores nothing. "
            + "Run it on a throwaway guest against its own server.",
            async c =>
            {
                var server = new ServerId(c.Int("server", c.Require().Server.Value));
                var fmt = new Google.Protobuf.JsonFormatter(Google.Protobuf.JsonFormatter.Settings.Default.WithFormatDefaultValues(false));
                var counts = new Dictionary<string, int>();
                var samples = new Dictionary<string, List<string>>();
                void Keep(string kind, string json)
                {
                    counts[kind] = counts.GetValueOrDefault(kind) + 1;
                    var list = samples.TryGetValue(kind, out var l) ? l : samples[kind] = [];
                    if (list.Count < 40) list.Add(json);
                }
                string Try<T>(Google.Protobuf.MessageParser<T> parser, byte[] b) where T : Google.Protobuf.IMessage<T>
                {
                    try { return fmt.Format(parser.ParseFrom(b)); } catch (Exception ex) { return "!" + ex.GetType().Name; }
                }
                var scanner = new MapScanner(c.Require(), MapOptions(c))
                {
                    OnReply = reply =>
                    {
                        var blocks = reply.TryGetArray("serverPointArr", out var arr) ? arr.Objects().ToList() : [reply];
                        foreach (var block in blocks)
                            foreach (var key in block.Keys)
                            {
                                if (!block.TryGetArray(key, out var list)) { Keep("scalar:" + key, block.ToString().Length > 0 ? key : key); continue; }
                                foreach (var bytes in list.ByteArrays())
                                {
                                    if (key is "points" or "alInfos")
                                    {
                                        var p = Protobuf.WorldPointInfo.Parser.ParseFrom(bytes);
                                        if (key == "points" && p.BuildInfo is { } bi && bi.OwnerUid.Length > 0 && p.PointType == 6) { counts["points:base"] = counts.GetValueOrDefault("points:base") + 1; continue; }
                                        var extra = p.ExtraInfo.IsEmpty ? "" : $" extra={Convert.ToHexString(p.ExtraInfo.ToByteArray())}"
                                            + $" asBuilding={Try(Protobuf.AllianceBuildingPointInfo.Parser, p.ExtraInfo.ToByteArray())}"
                                            + $" asCity={Try(Protobuf.AllianceCityPointInfo.Parser, p.ExtraInfo.ToByteArray())}";
                                        Keep($"{key}:type{p.PointType}", $"x={(p.Id - 1) % 1000} y={(p.Id - 1) / 1000} {fmt.Format(p)}{extra}");
                                    }
                                    else if (key == "lands") Keep("lands", Try(Protobuf.LandPointInfo.Parser, bytes));
                                    else if (key == "deserts") Keep("deserts", Try(Protobuf.DesertInfo.Parser, bytes));
                                    else Keep("bytes:" + key, Convert.ToHexString(bytes));
                                }
                                foreach (var o in list.Objects()) Keep("obj:" + key, o.ToString());
                            }
                        foreach (var k in reply.Keys) if (k != "serverPointArr") Keep("top:" + k, k);
                    },
                };
                var tiles = await scanner.ScanAsync(server, null, c.Cancellation);
                var file = Path.Combine(c.DataDir, "raw", $"map_probe_{server.Value}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { server = server.Value, bases = tiles.Count, counts, features = scanner.LastFeatures, samples },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return OperationResult.Table($"server {server}: {tiles.Count} bases, dump in {file}",
                    ["kind", "count"], [.. counts.OrderBy(k => k.Key).Select(k => new object?[] { k.Key, k.Value })]);
            },
            ps:
            [
                new OperationParam("server", "Server", "number", Help: "Defaults to the session's own server."),
                new OperationParam("blockSize", "Block size (tiles)", "number", "10"),
            ]);

        Add("scan.map.bench", "Scan", "Map scan benchmark",
            "Scans ONE server several ways and compares time and bases found, storing nothing. For "
            + "tuning block size and pipelining. Run it on a throwaway guest against the guest's own "
            + "server (guests can only view their own map) - never on a real account.",
            async c =>
            {
                var server = new ServerId(c.Int("server", c.Require().Server.Value));
                var rows = new List<object?[]>();
                HashSet<int>? baseline = null;
                foreach (var spec in c.Text("variants", "4x1,10x1,20x1,10x4").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = spec.Trim().Split('x');
                    var opt = new MapScanOptions
                    {
                        BlockSize = int.Parse(parts[0].TrimEnd('L')),
                        LegacyLayout = parts[0].EndsWith('L'),   // "50Lx1" = the old layout, for comparison
                        Lod = parts[0].StartsWith("10") ? 0 : 1,
                        InFlight = parts.Length > 1 ? int.Parse(parts[1]) : 1,
                        Gap = TimeSpan.FromMilliseconds(c.Int("gap", 70)),
                    };
                    var scanner = new MapScanner(c.Require(), opt);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var tiles = await scanner.ScanAsync(server, null, c.Cancellation);
                        sw.Stop();
                        var foreign = tiles.Count(t => t.Server.Value != server.Value);
                        if (foreign > 0) c.Report($"{spec}: {foreign} of the bases belong to other servers - left out");
                        tiles = [.. tiles.Where(t => t.Server.Value == server.Value)];
                        var ids = tiles.Select(t => t.PointId).ToHashSet();
                        baseline ??= ids;
                        rows.Add([spec.Trim(), opt.RequestCount, Math.Round(sw.Elapsed.TotalSeconds, 1), tiles.Count,
                            baseline.Count(i => !ids.Contains(i)), ids.Count(i => !baseline.Contains(i)), scanner.LastMissedBatches,
                            // where the missing ones are: count per 100-tile band of x and of y
                            "x " + string.Join(" ", baseline.Where(i => !ids.Contains(i)).GroupBy(i => (i - 1) % 1000 / 100).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))
                            + " | y " + string.Join(" ", baseline.Where(i => !ids.Contains(i)).GroupBy(i => (i - 1) / 1000 / 100).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))]);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException)
                    {
                        rows.Add([spec.Trim(), opt.RequestCount, Math.Round(sw.Elapsed.TotalSeconds, 1), 0, 0, 0, 0, ex.Message]);
                    }
                    c.Report($"{spec}: {rows[^1][2]}s, {rows[^1][3]} bases");
                    await Task.Delay(TimeSpan.FromSeconds(c.Int("pauseSeconds", 5)), c.Cancellation);
                }
                return OperationResult.Table($"server {server}: {rows.Count} variants (first row is the baseline)",
                    ["blockSize x inFlight", "requests", "seconds", "bases", "missing vs first", "extra vs first", "lost batches", "error"], rows);
            },
            ps:
            [
                new OperationParam("server", "Server", "number", Help: "Defaults to the session's own server."),
                new OperationParam("variants", "Variants", "text", "4x1,10x1,20x1,10x4",
                    Help: "blockSize x inFlight, comma separated. The first is the baseline."),
                new OperationParam("gap", "Gap between sends (ms)", "number", "70"),
                new OperationParam("pauseSeconds", "Pause between variants (s)", "number", "5"),
            ]);

        Add("scan.power", "Scan", "Power snapshot",
            "Scans the map, then collects power for everyone on it, for one server or a range. A "
            + "second snapshot is what makes the activity rule work - a rise against the previous "
            + "one is the only proof of a login. Works cross-server: the session does not need to "
            + "live on the servers it scans. One server failing is recorded and the rest carry on.",
            async c =>
            {
                var db = new LastWarDb(c.Database);
                db.EnsureSchema();
                // "server" is kept for anything already calling it the old way.
                var spec = c.Text("servers", c.Text("server"));
                if (spec.Length == 0) throw new ArgumentException("a server or range is required");
                var fromMap = c.Flag("fromMap");
                var maxAge = c.Int("mapMaxAgeHours", 6);
                var waitMinutes = c.Int("waitForMapMinutes", 0);
                var wave = new WaveStore(c.Database);

                var rows = new List<object?[]>();
                var list = ParseServers(spec);
                var reconnects = 0;
                for (var i = 0; i < list.Count; i++)
                {
                    var id = list[i];
                    var server = new ServerId(id);
                    var at = DateTimeOffset.UtcNow;
                    try
                    {
                        IReadOnlyCollection<LastWar.Core.Model.MapTile> tiles;
                        if (fromMap)
                        {
                            // Guests cannot view another server's map (it is level-gated) but they
                            // CAN ask about its players - so one account sweeps, the rest collect.
                            // Waiting lets collectors run alongside the sweep instead of after it:
                            // each picks up a server as soon as its map lands.
                            var (fromStore, takenAt) = wave.LatestTiles(id, DateTimeOffset.UtcNow.AddHours(-maxAge));
                            var giveUp = DateTimeOffset.UtcNow.AddMinutes(waitMinutes);
                            while (fromStore.Count == 0 && DateTimeOffset.UtcNow < giveUp)
                            {
                                c.Report($"{server} waiting for its map");
                                await Task.Delay(TimeSpan.FromSeconds(30), c.Cancellation);
                                (fromStore, takenAt) = wave.LatestTiles(id, DateTimeOffset.UtcNow.AddHours(-maxAge));
                            }
                            if (fromStore.Count == 0)
                                throw new InvalidOperationException($"no map of {server} in the last {maxAge}h - sweep it first");
                            tiles = fromStore;
                            c.Report($"{server} map from store ({takenAt}): {tiles.Count} players");
                        }
                        else
                        {
                            var scanner = new MapScanner(c.Require());
                            tiles = await scanner.ScanAsync(server, null, c.Cancellation);
                            // The sweep is needed anyway to find who to ask; storing it keeps the map
                            // (and with it everyone's server and alliance) as fresh as the power data.
                            db.InsertMapScan(server, at, [.. tiles], scanner.LastFeatures, scanner.LastGreen);
                            c.Report($"{server} map: {tiles.Count} players");
                        }

                        var samples = await new PowerCollector(c.Require())
                        {
                            Gap = TimeSpan.FromMilliseconds(c.Int("gap", 1000)),
                        }.CollectAsync(
                            server, [.. tiles], at,
                            new Progress<PowerScanProgress>(p =>
                            {
                                if (p.Done % 250 == 0)
                                    c.Report($"{server} {p.Stage}: {p.Done}/{p.Total}, {p.Accounts} accounts");
                            }), c.Cancellation);

                        var stored = db.InsertPowerSamples([.. samples]);
                        rows.Add([server.Value, at.ToString("u"), tiles.Count, stored, "stored"]);
                        c.Report($"{server} done: {stored} accounts");
                    }
                    // 2026-10-02, after Sadmin was banned: a dropped real-account session is NOT logged
                    // back in any more (was up to 20 re-logins - the pattern most likely to flag an account).
                    // The sweep stops; raise this only with Henry's say-so.
                    catch (IOException ex) when (c.Reconnect is not null && reconnects < RealAccountReconnects)
                    {
                        // Long detail sweeps get their connection dropped - seen on every account,
                        // Sadmin included. A real account can log in again, so do that and redo this
                        // server from the start; nothing was stored for it yet.
                        reconnects++;
                        c.Report($"{server} CONNECTION LOST ({ex.Message}) - reconnect {reconnects}");
                        try { if (c.Session is { } old) await old.DisposeAsync(); } catch { /* already dead */ }
                        try
                        {
                            // Back off harder each time: if the drops are rate limiting, hammering
                            // straight back in is exactly the wrong response.
                            await Task.Delay(TimeSpan.FromSeconds(30 * reconnects), c.Cancellation);
                            c.Session = await c.Reconnect(c.Cancellation);
                            i--;
                            continue;
                        }
                        catch (Exception rex) when (rex is not OperationCanceledException)
                        {
                            rows.Add([server.Value, at.ToString("u"), 0, 0, "could not reconnect: " + rex.Message]);
                            foreach (var left in list.Skip(i + 1)) rows.Add([left, "", 0, 0, "not attempted"]);
                            break;
                        }
                    }
                    catch (IOException ex)
                    {
                        // The socket is gone, and a dead socket fails every later server in
                        // milliseconds. Stop and say which were never tried, so a re-run on a fresh
                        // session covers exactly those - a guest cannot log in a second time.
                        rows.Add([server.Value, at.ToString("u"), 0, 0, "connection lost: " + ex.Message]);
                        c.Report($"{server} CONNECTION LOST: {ex.Message}");
                        foreach (var left in ParseServers(spec).SkipWhile(s => s != id).Skip(1))
                            rows.Add([left, "", 0, 0, "not attempted"]);
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        rows.Add([server.Value, at.ToString("u"), 0, 0, ex.Message]);
                        c.Report($"{server} FAILED: {ex.Message}");
                    }
                }
                var ok = rows.Count(r => (string?)r[4] == "stored");
                return OperationResult.Table(
                    $"{ok} of {rows.Count} servers snapshotted, {rows.Sum(r => Convert.ToInt64(r[3])):N0} accounts",
                    ["server", "taken", "players", "accounts", "result"], rows);
            },
            ps:
            [
                new OperationParam("servers", "Servers", "text", "1967", Required: true,
                    Help: "A list or a range: 1967, or 1957-1988."),
                new OperationParam("fromMap", "Use the stored map", "bool", "false",
                    Help: "Skip the sweep and use a recent stored map. How a guest collects power for "
                        + "servers it is not allowed to view."),
                new OperationParam("mapMaxAgeHours", "Stored map no older than (hours)", "number", "6"),
                new OperationParam("gap", "Gap between player requests (ms)", "number", "1000"),
                new OperationParam("waitForMapMinutes", "Wait for a map to arrive (minutes)", "number", "0",
                    Help: "Lets a collector run alongside a sweep that has not reached its server yet."),
            ]);

        Add("lua.find", "Lua", "Search the client script",
            "Searches the decompiled Lua for a string and shows where it is used. This is how a "
            + "command's parameters get confirmed against the client rather than guessed.",
            c =>
            {
                var needle = c.Text("needle");
                if (needle.Length == 0) throw new ArgumentException("something to search for is required");
                var dir = c.Text("dir", @"C:\Temp\LWC\apk\lua");
                if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"no lua at {dir}");

                var rows = new List<object?[]>();
                foreach (var file in Directory.EnumerateFiles(dir, "*.luac", SearchOption.AllDirectories))
                {
                    if (rows.Count >= 400) break;
                    // Not everything in the dump is this build's bytecode format; skip what will not read.
                    string uses;
                    try { uses = LuaDisassembler.FindUses(LuaReader.LoadFile(file), needle, 0); }
                    catch { continue; }
                    // FindUses reports a miss as a sentence rather than an empty string, so a
                    // plain "is it empty" test would list every file in the dump as a hit.
                    if (uses.Contains("(no use of", StringComparison.Ordinal)) continue;
                    foreach (var line in uses.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        if (rows.Count < 400 && line.Trim().Length > 0)
                            rows.Add([Path.GetFileName(file), line.TrimEnd()]);
                }
                return Task.FromResult(OperationResult.Table(
                    $"{rows.Count} use(s) of \"{needle}\"", ["file", "use"], rows));
            },
            needsSession: false,
            ps:
            [
                new OperationParam("needle", "Search for", "text", Required: true),
                new OperationParam("dir", "Script folder", "text", @"C:\Temp\LWC\apk\lua"),
            ]);
    }

    /// <summary>
    /// The daily chores. Each step is caught on its own: a failure in one is reported in its row and
    /// the rest still run, which matters because these are exactly the calls that answer with a
    /// harmless "already done today" error code.
    /// </summary>
    static async Task<OperationResult> RunDailiesAsync(OperationContext c)
    {
        var client = c.Require();
        var steps = new (string Name, Func<Task<GameActionResult>> Run)[]
        {
            ("alliance help", () => new AllianceActions(client).HelpAllAsync(c.Cancellation)),
            ("alliance gifts", () => new AllianceActions(client).ClaimGiftsAsync(c.Cancellation)),
            ("alliance tech", () => new AllianceActions(client).DonateTechAsync(c.Cancellation)),
            ("vip login", () => new VipActions(client).DailyLoginAsync(c.Cancellation)),
            ("vip reward", () => new VipActions(client).DailyRewardAsync(c.Cancellation)),
            ("visitors", () => new VisitorActions(client).GreetAllAsync(c.Cancellation)),
            ("idle rewards", () => new BaseActions(client).IdleRewardAsync(c.Flag("claimIdle", true), c.Cancellation)),
            ("production", () => new BaseActions(client).CollectResourcesAsync(c.Cancellation)),
            ("mail", () => new MailActions(client).ClaimAllAsync(c.Cancellation)),
        };

        var rows = new List<object?[]>();
        foreach (var (name, run) in steps)
        {
            c.Report(name);
            try { rows.Add([name, "ok", (await run()).Summary]); }
            catch (Exception ex) { rows.Add([name, "failed", ex.Message]); }
        }
        var ok = rows.Count(r => (string?)r[1] == "ok");
        return OperationResult.Table($"{ok} of {rows.Count} steps completed",
            ["step", "result", "detail"], rows);
    }
}

public static partial class Operations
{
    const int RealAccountReconnects = 0;

    static MapScanOptions MapOptions(OperationContext c) => new()
    {
        // Lod 0 needs ~79 requests a server (lod 1: 29) - spaced like a player panning, not a burst (jittered +-30%).
        Gap = TimeSpan.FromMilliseconds(c.Int("gap", 250)),
        // Client sizes only: 10 (lod 0) or 20 (lod 1). Lod 1 is the zoomed-out view: the server sends only about a third
        // of the bases there (2026-10-08: #1967 1,784 tiles at lod 1 v 5,568 at lod 0, evenly thinned), so maps use lod 0.
        BlockSize = c.Int("blockSize", 10) is 20 ? 20 : 10,
        Lod = c.Int("blockSize", 10) is 20 ? 1 : 0,
        InFlight = c.Int("inFlight", 1),
    };
}
