using LastWar.Client;
using LastWar.Client.Auth;
using LastWar.Client.Game;
using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Operations;

public static partial class Operations
{
    /// <summary>
    /// The profile sweep: every player ever tied to a server range, asked "which server are you
    /// from, and what have you got" in batches of a thousand. This is the authoritative record of
    /// home server - a map only says where a base happened to sit, and at weekends and in seasons
    /// players teleport to other servers to fight without transferring.
    ///
    /// One sweep is one snapshot: every row carries the sweep's start time. Rows are appended,
    /// never updated, so the sweep before a transfer window and the one after it both survive and
    /// migration.compare can read who went where. Power comes with it (the same figure the detail
    /// scan reads), so the sweep also writes a power snapshot filed under each player's HOME
    /// server - which keeps server strength honest while visitors are on the map.
    ///
    /// Runs on fresh guests only; a guest can log in once, so a dropped connection continues on a
    /// new one with whoever has not been asked yet.
    /// </summary>
    static void AddProfileOperations()
    {
        Add("scan.profiles", "Scan", "Profile sweep (home servers)",
            "Every known player on a server range: home server, where their base is right now, "
            + "alliance and power, from a fresh guest. Takes minutes, not hours. Run it before and after "
            + "a transfer window - the pair is what migration.compare reads.",
            async c =>
            {
                var (lo, hi) = Range(c.Text("servers", "1957-1988"));
                var profiles = new ProfileStore(c.Database);
                profiles.EnsureSchema();
                var db = new LastWarDb(c.Database);
                db.EnsureSchema();

                var uids = profiles.KnownUids(lo, hi);
                var batchSize = Math.Clamp(c.Int("batch", 1000), 50, 2000);
                var gap = TimeSpan.FromMilliseconds(c.Int("gap", 1000));
                var writePower = c.Flag("power", true);
                // Changes only (the hourly activity pulse): keep LOGIN MOMENTS only - a player_seen row when their last
                // seen moved forward, with power, troops, kills and HQ at that moment. No full profile rows and no power
                // snapshot: those come from the once-a-day full sweep (Henry 2026-10-09: the hourly rows were a third
                // of a 1.5 GB/day growth).
                var changesOnly = c.Flag("changes", false);
                // A named snapshot (e.g. "capitol" during a Capitol War) is kept whole: data.thin only thins "sweep".
                var label = changesOnly ? "pulse" : c.Text("source", "sweep");
                // A full sweep in the last 45 minutes already asked everyone: the pulse would only repeat it.
                if (changesOnly)
                {
                    using var dbc = LastWar.Storage.Db.Open(c.Database);
                    if (LastWar.Storage.Db.Rows(dbc, "SELECT MAX(taken_at) AS t FROM player_profile WHERE source = 'sweep';").FirstOrDefault()?["t"] is string last
                        && DateTimeOffset.TryParse(last, out var lastAt) && DateTimeOffset.UtcNow - lastAt < TimeSpan.FromMinutes(45))
                        return OperationResult.Text($"skipped: a full profile sweep ran at {lastAt:HH:mm} UTC");
                }
                var known = changesOnly ? profiles.LatestSeen() : null;
                var stored = 0;
                var at = DateTimeOffset.UtcNow;
                var root = Path.Combine(c.DataDir, "sessions", "auto");
                c.Report($"{uids.Count:N0} players tied to {lo}-{hi}; sweep stamped {at:u}");

                var asked = new HashSet<string>();
                var got = new Dictionary<string, PlayerProfile>();
                var guests = 0;
                for (var attempt = 1; attempt <= c.Int("attempts", 6) && asked.Count < uids.Count; attempt++)
                {
                    guests = attempt;
                    var home = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_profiles_{attempt}");
                    try
                    {
                        var config = await GuestBootstrap.CreateAsync(home, null, c.Cancellation);
                        await using var client = await LastWarClient.ConnectAsync(config, GameIdentity.LoadOrCreate(home), c.Cancellation);
                        c.Report($"attempt {attempt}: guest on {client.Server.Value}, {uids.Count - asked.Count:N0} left");
                        // A request sent straight after login, while the init burst is still arriving,
                        // is never answered (seen on every attempt on 2026-09-25). Let login settle first.
                        if (LoginPushRecorder.For(client.Connection) is { } rec)
                            await rec.WaitAsync(TimeSpan.FromSeconds(20), c.Cancellation);
                        await Task.Delay(TimeSpan.FromSeconds(3), c.Cancellation);
                        foreach (var batch in uids.Where(u => !asked.Contains(u)).Chunk(batchSize))
                        {
                            var rows = await ProfileLookup.FetchAsync(client, batch, at, label, TimeSpan.FromSeconds(45), c.Cancellation);   // the first call on a fresh guest can be slow
                            // Stored as they arrive: a sweep cut short still keeps what it learned.
                            if (known is not null)
                            {
                                var fresh = rows.Where(r => r.LastSeen is { } s
                                    && (!known.TryGetValue(r.Uid, out var k) || string.CompareOrdinal(s.ToString("O"), k) > 0)).ToList();
                                stored += profiles.InsertSeen(fresh);
                                foreach (var r in fresh) known[r.Uid] = r.LastSeen!.Value.ToString("O");
                            }
                            else
                            {
                                profiles.Insert(rows);
                                profiles.InsertSeen(rows);
                                stored += rows.Count;
                            }
                            // Only servers in the range: they are swept whole. A lone player homed elsewhere would
                            // otherwise become that server's "latest snapshot" of one account. Changes-only mode
                            // writes no power rows at all: a power snapshot means EVERY player of a server at one
                            // moment, and a partial one made the pages read ~300 accounts as the whole server.
                            if (writePower && !changesOnly) db.InsertPowerSamples([.. rows.Where(r => r.HomeServer >= lo && r.HomeServer <= hi).Select(ToPower)]);
                            foreach (var r in rows) got[r.Uid] = r;
                            asked.UnionWith(batch);
                            if (asked.Count % (batchSize * 10) == 0 || asked.Count == uids.Count)
                                c.Report($"{asked.Count:N0}/{uids.Count:N0} asked, {got.Count:N0} answered{(changesOnly ? $", {stored:N0} changed" : "")}");
                            await Task.Delay(LastWar.Client.Jitter.Of(gap), c.Cancellation);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException or LoginFailedException
                                                    or HttpRequestException or InvalidOperationException)
                    {
                        c.Report($"attempt {attempt} lost ({ex.Message}); {asked.Count:N0} asked so far");
                        await Task.Delay(TimeSpan.FromSeconds(20 * attempt), c.Cancellation);
                    }
                    finally
                    {
                        if (GuestKeeper.Retire(c.DataDir, c.Database, home) is { } kept) c.Report($"guest landed on a tracked server - kept in {kept}");
                    }
                }

                if (got.Count > 0) profiles.AddReading(at, label, got.Count);
                var byHome = got.Values.GroupBy(p => p.HomeServer).OrderBy(g => g.Key)
                    .Select(g => new object?[]
                    {
                        g.Key, g.Count(), g.Sum(p => p.Power),
                        g.Count(p => p.Visiting),
                        g.Count(p => p.HomeServer != (int.TryParse(p.Uid[^6..], out var o) ? o : 0)),
                        g.Key >= lo && g.Key <= hi ? "" : "outside range",
                    }).ToList();
                var visiting = got.Values.Count(p => p.Visiting);
                var summary = $"{got.Count:N0} of {uids.Count:N0} answered ({uids.Count - asked.Count:N0} never asked) over {guests} guest(s); "
                    + $"sweep {at:u}; {visiting:N0} away from home right now"
                    + (changesOnly ? $"; {stored:N0} new login moments stored" : writePower ? "; power snapshot written under home servers" : "");
                return OperationResult.Table(summary,
                    ["home server", "players", "power", "based elsewhere now", "created elsewhere", "note"], byHome);
            },
            needsSession: false,
            ps:
            [
                new OperationParam("servers", "Servers", "text", "1957-1988", Required: true,
                    Help: "A range. Everyone ever seen on, applying to, or from these servers is asked - including people who have since left."),
                new OperationParam("batch", "Players per request", "number", "1000", Help: "2,000 answers in under 300 ms."),
                new OperationParam("gap", "Gap between requests (ms)", "number", "1000"),
                new OperationParam("power", "Also write a power snapshot", "bool", "true"),
                new OperationParam("attempts", "Guests at most", "number", "6"),
                new OperationParam("source", "Label the snapshot", "text", "sweep",
                    Help: "Anything but \"sweep\" (e.g. \"capitol\") is never thinned - for moments worth keeping whole, like a Capitol War."),
                new OperationParam("changes", "Store changes only", "bool", "false",
                    Help: "For the hourly activity pulse: keep only login moments (last seen moved forward), no full profiles or power snapshot."),
            ]);

        Add("migration.leavers", "Migration", "Who has left (live)",
            "Sweeps one server's players right now (fresh guest, seconds) and lists everyone whose home "
            + "was this server at the baseline and is somewhere else now - with where they went - plus "
            + "known players who have arrived. Every run is stored as a sweep, so run it as often as you "
            + "like through a transfer window; the baseline stays fixed.",
            async c =>
            {
                var server = c.Int("server", 1967);
                if (!DateTimeOffset.TryParse(c.Text("baseline", "2026-09-25 00:00"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var baseline))
                    throw new ArgumentException("baseline must be a UTC time, e.g. 2026-09-25 00:00");

                // The sweep itself - same code path as scan.profiles, so it is stored the same way.
                var started = DateTimeOffset.UtcNow;
                var sweep = await RunAsync("scan.profiles", new OperationContext
                {
                    Args = new Dictionary<string, string> { ["servers"] = $"{server}-{server}", ["batch"] = "1000", ["gap"] = "500" },
                    Database = c.Database, DataDir = c.DataDir, Cancellation = c.Cancellation, Progress = c.Progress,
                });
                c.Report(sweep.Summary);

                var profiles = new ProfileStore(c.Database);
                var uids = profiles.KnownUids(server, server);
                var before = profiles.Latest(uids, null, baseline);
                var now = profiles.Latest(uids, started.AddSeconds(-1), null);
                if (now.Count == 0) throw new InvalidOperationException("the sweep stored nothing - see its progress lines");

                var left = before.Values.Where(b => b.HomeServer == server && now.TryGetValue(b.Uid, out var a) && a.HomeServer != server)
                    .Select(b => (B: b, A: now[b.Uid])).OrderByDescending(x => x.B.Power).ToList();
                var arrived = now.Values.Where(a => a.HomeServer == server && (!before.TryGetValue(a.Uid, out var b) || b.HomeServer != server))
                    .Select(a => (B: before.GetValueOrDefault(a.Uid), A: a)).OrderByDescending(x => x.A.Power).ToList();
                var stayed = before.Values.Count(b => b.HomeServer == server && now.TryGetValue(b.Uid, out var a) && a.HomeServer == server);
                var visiting = now.Values.Count(a => a.HomeServer == server && a.Visiting);

                var where = string.Join(", ", left.GroupBy(x => x.A.HomeServer).OrderByDescending(g => g.Count())
                    .Select(g => $"#{g.Key} x{g.Count()}"));
                var summary = $"#{server} since {baseline:u}: {left.Count} left ({left.Sum(x => x.B.Power):N0} power), "
                    + $"{arrived.Count} known players arrived, {stayed:N0} still here, {visiting} based elsewhere right now"
                    + (left.Count > 0 ? $". Gone to: {where}" : "")
                    + ". Arrivals from servers we never scanned only show after a map scan of this server.";

                // Hero power is only published on the hero board and on transfer applications.
                var hero = new WaveStore(c.Database).LatestHeroPower(left.Select(x => x.B.Uid).Concat(arrived.Select(x => x.A.Uid)));
                long? Hero(string uid) => hero.TryGetValue(uid, out var h) ? h : null;

                string[] cols = ["name", "alliance before", "power before", "from", "to", "alliance now", "power", "hero power", "uid"];
                string[] totals = ["power before", "power", "hero power"];
                var links = new Dictionary<string, string> { ["name"] = "player:uid", ["from"] = "server:", ["to"] = "server:" };
                var leavers = OperationTable.Of($"Left #{server}", cols, left.Select(x => new object?[]
                    { x.B.Name, x.B.AllianceTag, x.B.Power, x.B.HomeServer, x.A.HomeServer, x.A.AllianceTag, x.A.Power, Hero(x.B.Uid), x.B.Uid }))
                    with { Brief = ["name", "alliance before", "to", "alliance now", "power", "hero power"], Links = links, Totals = totals };
                var joiners = OperationTable.Of($"Joined #{server}", cols, arrived.Select(x => new object?[]
                    { x.A.Name, x.B?.AllianceTag, x.B?.Power, x.B?.HomeServer, x.A.HomeServer, x.A.AllianceTag, x.A.Power, Hero(x.A.Uid), x.A.Uid }))
                    with { Brief = ["name", "alliance before", "from", "alliance now", "power", "hero power"], Links = links, Totals = totals };
                return new OperationResult(summary, [], []) { Tables = [leavers, joiners] };            },
            needsSession: false,
            ps:
            [
                new OperationParam("server", "Server", "number", "1967", Required: true),
                new OperationParam("baseline", "Baseline (UTC)", "text", "2026-09-25 00:00",
                    Help: "Home server is taken from the newest profile before this moment - pick a time before the window opened."),
            ]);
        Add("names.changes", "Scan", "Name changes",
            "Re-detects every player name, alliance tag and alliance name change from all stored "
            + "observations (maps, power, boards, profiles, chat, applications) and lists the newest. "
            + "Runs by itself after every scan; this is for looking, or after a big backfill.",
            c =>
            {
                var names = new NameHistory(c.Database);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var n = names.Rebuild();
                var kind = c.Text("kind", "");
                var rows = names.Recent(c.Int("limit", 100), kind.Length > 0 ? kind : null).Select(r => new object?[]
                    { r["detected"], r["kind"], r["field"], r["old"], r["new"], r["lastSeenOld"], r["source"], r["id"] });
                return Task.FromResult(new OperationResult($"{n:N0} changes on record (rebuilt in {sw.Elapsed.TotalSeconds:0.0}s)", [], [])
                {
                    Tables =
                    [
                        OperationTable.Of("Newest changes", ["detected", "kind", "field", "was", "now", "last seen as was", "seen in", "id"], rows)
                            with { Brief = ["detected", "kind", "was", "now"], Links = new Dictionary<string, string> { ["now"] = "player:id" } },
                    ],
                });
            },
            needsSession: false,
            ps:
            [
                new OperationParam("kind", "Only", "text", "", Help: "player or alliance; empty for both."),
                new OperationParam("limit", "How many", "number", "100"),
            ]);
        Add("history.servers", "Scan", "Server history (transfers & visits)",
            "Rebuilds every player's server story from all stored evidence - creation server, any "
            + "early move (assumed November 2025), transfers between HOME servers with the moment they "
            + "happened narrowed as far as the data allows, and visits to other servers to fight - and "
            + "lists the newest transfers and visits. Runs by itself after every scan.",
            c =>
            {
                var h = new ServerHistory(c.Database);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var n = h.Rebuild();
                using var conn = LastWar.Storage.Db.Open(c.Database);
                List<object?[]> Q(string sql)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("@n", c.Int("limit", 100));
                    using var r = cmd.ExecuteReader();
                    var rows = new List<object?[]>();
                    while (r.Read()) { var row = new object[r.FieldCount]; r.GetValues(row); rows.Add(row); }
                    return rows;
                }
                const string Name = "(SELECT p.name FROM player_profile p WHERE p.uid = h.uid ORDER BY p.taken_at DESC LIMIT 1)";
                var transfers = Q($"SELECT {Name}, h.from_server, h.to_server, h.from_at, h.to_at, h.evidence, h.uid FROM server_history h WHERE kind = 'transfer' ORDER BY to_at DESC LIMIT @n;");
                var visits = Q($"SELECT {Name}, h.from_server, h.to_server, h.from_at, h.to_at, h.evidence, h.uid FROM server_history h WHERE kind = 'visit' ORDER BY to_at DESC LIMIT @n;");
                var counts = string.Join(", ", h.Counts().Select(x => $"{x.Players:N0} {x.Kind}"));
                var links = new Dictionary<string, string> { ["name"] = "player:uid", ["from"] = "server:", ["to"] = "server:" };
                return Task.FromResult(new OperationResult($"{n:N0} events rebuilt in {sw.Elapsed.TotalSeconds:0.0}s - players with: {counts}", [], [])
                {
                    Tables =
                    [
                        OperationTable.Of("Newest transfers", ["name", "from", "to", "last seen at old home", "first seen at new home", "evidence", "uid"], transfers)
                            with { Brief = ["name", "from", "to", "first seen at new home"], Links = links },
                        OperationTable.Of("Newest visits", ["name", "from", "to", "first seen there", "last seen there", "evidence", "uid"], visits)
                            with { Brief = ["name", "from", "to", "last seen there"], Links = links },
                    ],
                });
            },
            needsSession: false,
            ps: [new OperationParam("limit", "How many of each", "number", "100")]);
        Add("profiles.sweeps", "Scan", "Profile sweeps held",
            "Every stored profile sweep: when, how many players, and how many were based away from home.",
            c =>
            {
                var profiles = new ProfileStore(c.Database);
                profiles.EnsureSchema();
                var rows = profiles.Sweeps().Select(s => new object?[] { s.TakenAt, s.Players, s.Visiting }).ToList();
                return Task.FromResult(OperationResult.Table($"{rows.Count} sweep(s)", ["taken", "players", "away from home"], rows));
            },
            needsSession: false);
    }

    static PowerSample ToPower(PlayerProfile p) => new()
    {
        TakenAt = p.TakenAt,
        Server = new ServerId(p.HomeServer),
        Uid = new PlayerUid(p.Uid),
        Power = p.Power,
        Name = p.Name,
        AllianceId = p.AllianceId,
        AllianceTag = p.AllianceTag,
        AllianceName = p.AllianceName,
        KillArmy = p.ArmyKill,
        HqLevel = p.Level,
        Source = PowerSource.Profile,
    };
}
