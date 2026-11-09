using LastWar.Protocol.Sfs;
using LastWar.Core.Analysis;
using LastWar.Core.Model;
using LastWar.Storage;
using LastWar.Storage.Import;

namespace LastWar.Cli;

public static class Program
{
    const string DefaultRoot = @"C:\Temp\LWC";
    static string _root = DefaultRoot;
    static string DataDir => Path.Combine(_root, "data");
    /// <summary>Postgres: --pg "<connection string>", else LASTWAR_PG, else the local docker setup (PgConfig).</summary>
    static string _pg = "";
    static string DefaultDb => _pg;

    public static int Main(string[] argv)
    {
        // Chat and player names are full Unicode; without this the console mangles them.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (argv.Length == 0) { Usage(); return 1; }
        var args = new Args(argv.Skip(1));
        _root = args.Get("root") ?? DefaultRoot;
        _pg = LastWar.Storage.Postgres.PgConfig.Resolve(args.Get("pg"), _root);
        var db = new LastWarDb(_pg);

        try
        {
            switch (argv[0].ToLowerInvariant())
            {
                case "init": db.EnsureSchema(); Console.WriteLine("schema ready"); return 0;
                case "import-map": return ImportMap(db, args);
                case "import-power": return ImportPower(db, args);
                case "activity": return Activity(db, args);
                case "call": return CallAsync(args).GetAwaiter().GetResult();
                case "lua-dump": return LuaDump(args);
                case "ops": return Ops(args);
                case "op": return OpAsync(db, args).GetAwaiter().GetResult();
                case "guest": return GuestAsync(args).GetAwaiter().GetResult();
                case "chat": return ChatAsync(args).GetAwaiter().GetResult();
                case "scan-map": return ScanMapAsync(db, args).GetAwaiter().GetResult();
                case "scan-power": return ScanPowerAsync(db, args).GetAwaiter().GetResult();
                case "snapshots": return Snapshots(db, args);
                default: Usage(); return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    static void Usage() => Console.WriteLine("""
        lastwar <command> [--pg <connection string>] [--root <folder holding data\>]

          init                                        create/upgrade the database schema
          import-map    --path <file|dir>             load map_*.csv scans
          import-power  --server <id> [--at <iso>]
                        [--detail <power csv>] [--rosters <al.rank log>]
          snapshots     --server <id>                 list snapshot timestamps
          activity      --server <id> [--out <csv>]   compare the two newest snapshots
          scan-map      --servers <1957-1988|1967,1970>   sweep maps into the database
          scan-power    --server <id>                     power snapshot for one server
          chat          --listen [--seconds <n>] [--raw]      stream live chat into the db
                        --history --room <id> [--pages N] [--resume] [--delay MS]
                        --script <file>                       raw {cmd,params} probes
                                                      connect to chat; stream or query
          lua-dump      --file <x.luac> [--find <string>] [--context <n>] [--all]
                                                      disassemble this build's Lua bytecode
          call          --cmd <command> [--json <params>] [--config <session.json>]
                                                      issue one command and print the reply

        Activity rule: a RISE in power against the previous snapshot proves someone
        logged in and acted. A fall proves nothing and is never read as dormancy.
        """);

    /// <summary>Lists what the tooling can do. Same registry the dashboard renders.</summary>
    static int Ops(Args args)
    {
        var filter = args.Get("group");
        foreach (var group in LastWar.Operations.Operations.All
                     .Where(o => filter is null || o.Group.Equals(filter, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(o => o.Group))
        {
            Console.WriteLine($"\n{group.Key}");
            foreach (var op in group)
            {
                var ps = op.Params.Count == 0
                    ? ""
                    : "  " + string.Join(" ", op.Params.Select(p =>
                        p.Required ? $"--{p.Name} <{p.Type}>" : $"[--{p.Name} <{p.Type}>]"));
                Console.WriteLine($"  {op.Id,-18}{(op.Writes ? "*" : " ")} {op.Title}{ps}");
            }
        }
        Console.WriteLine("\n* changes something on the account rather than reading it");
        return 0;
    }

    /// <summary>Runs one operation. Parameters come from the registry, so there is no per-op parsing.</summary>
    static async Task<int> OpAsync(LastWarDb db, Args args)
    {
        var id = args.Require("op");
        var info = LastWar.Operations.Operations.Find(id) ?? throw new ArgumentException($"no operation '{id}'");

        var values = new Dictionary<string, string>();
        foreach (var p in info.Params)
            if (args.Get(p.Name) is { } v) values[p.Name] = v;
            else if (p.Default is { } d) values[p.Name] = d;

        // The same account policy as the dashboard, decided before anything connects.
        if (info.NeedsSession && info.Refuses(TierOf(args)) is { } why)
        {
            Console.Error.WriteLine($"refused: {why}");
            return 2;
        }
        LastWar.Client.LastWarClient? client = null;
        if (info.NeedsSession) client = await ConnectAsync(args);
        try
        {
            var result = await LastWar.Operations.Operations.RunAsync(id, new LastWar.Operations.OperationContext
            {
                Args = values,
                Database = db.Path,
                DataDir = DataDir,
                Session = client,
                Reconnect = info.NeedsSession ? _ => ConnectAsync(args) : null,
                Progress = new Progress<string>(m => Console.Error.WriteLine($"  {m}")),
            });

            Console.WriteLine(result.Summary);
            if (result.Columns.Count > 0) PrintTable(result.Columns, result.Rows);
            foreach (var t in result.Tables)
            {
                Console.WriteLine();
                Console.WriteLine($"{t.Title} ({t.Rows.Count})");
                if (t.Rows.Count == 0) continue;
                // A total row for the summed columns; blank cells (unknown values) are skipped.
                var rows = t.Totals.Count == 0 ? t.Rows : [.. t.Rows, t.Columns.Select((col, i) =>
                    t.Totals.Contains(col) ? t.Rows.Sum(r => long.TryParse(r[i], out var v) ? v : 0).ToString("N0")
                    : i == 0 ? $"TOTAL ({t.Rows.Count})" : "").ToArray()];
                PrintTable(t.Columns, rows);
            }
            return 0;
        }
        finally { if (client is not null) await client.DisposeAsync(); }
    }

    /// <summary>Column-aligned output, so a result is readable in a terminal as well as a browser.</summary>
    static void PrintTable(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var widths = columns
            .Select((c, i) => Math.Max(c.Length, rows.Count == 0 ? 0 : rows.Max(r => i < r.Count ? r[i].Length : 0)))
            .Select(w => Math.Min(w, 48))
            .ToArray();

        string Format(IReadOnlyList<string> cells) => string.Join("  ", cells.Select((v, i) =>
            (v.Length > widths[i] ? v[..widths[i]] : v).PadRight(widths[i]))).TrimEnd();

        Console.WriteLine();
        Console.WriteLine(Format(columns));
        Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows) Console.WriteLine(Format(row));
    }

    /// <summary>Bootstraps a fresh guest into its own home directory and saves the session.</summary>
    static async Task<int> GuestAsync(Args args)
    {
        var home = args.Get("home") ?? Path.Combine(@"C:\Temp\LWC\data\guests", $"guest_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
        var config = await LastWar.Client.Auth.GuestBootstrap.CreateAsync(home, args.Get("zone"));
        var path = Path.Combine(home, ".lastwar_goclient_session.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
            config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"guest ready: zone={config.Zone} uid={config.GameUid} {config.PrimaryHost}:{config.Port}");
        Console.WriteLine($"session saved to {path}");
        return 0;
    }

    /// <summary>
    /// The tier of the session --config points at: from the runner spec that owns that file (the
    /// dashboard's Sessions page sets it), else a guest for throwaway guest folders, else service.
    /// --tier overrides.
    /// </summary>
    static LastWar.Operations.AccountTier TierOf(Args args)
    {
        if (args.Get("tier") is { } t) return LastWar.Operations.AccountTiers.Parse(t);
        var config = Path.GetFullPath(args.Get("config")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lastwar_goclient_session.json"));
        var runners = @"C:\Temp\LWC\data\runners";
        foreach (var file in Directory.Exists(runners) ? Directory.EnumerateFiles(runners, "*.json") : [])
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                if (!r.TryGetProperty("ConfigPath", out var cp) || !string.Equals(Path.GetFullPath(cp.GetString() ?? ""), config, StringComparison.OrdinalIgnoreCase)) continue;
                var tier = r.TryGetProperty("Tier", out var tt) ? tt.GetString() ?? "" : "";
                var kind = r.TryGetProperty("Kind", out var k) ? k.GetString() ?? "" : "";
                return tier.Length > 0 ? LastWar.Operations.AccountTiers.Parse(tier)
                    : kind == "guest" ? LastWar.Operations.AccountTier.Guest
                    : kind == "account" ? LastWar.Operations.AccountTier.Privileged
                    : LastWar.Operations.AccountTier.Service;
            }
            catch { /* a half-written spec is not a match */ }
        }
        return config.Contains(@"\guests\", StringComparison.OrdinalIgnoreCase) || config.Contains(@"\sessions\auto\", StringComparison.OrdinalIgnoreCase)
            ? LastWar.Operations.AccountTier.Guest : LastWar.Operations.AccountTier.Service;
    }

    static async Task<LastWar.Client.LastWarClient> ConnectAsync(Args args)
    {
        var configPath = args.Get("config")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".lastwar_goclient_session.json");
        var config = LastWar.Client.Auth.SessionConfig.Load(configPath);
        var identity = LastWar.Client.Auth.GameIdentity.LoadOrCreate(Path.GetDirectoryName(configPath)!);
        var client = await LastWar.Client.LastWarClient.ConnectAsync(config, identity);
        Console.Error.WriteLine($"connected to server {client.Server} as {config.GameUid}");
        return client;
    }

    static async Task<int> ScanMapAsync(LastWarDb db, Args args)
    {
        db.EnsureSchema();
        var servers = ParseServers(args.Require("servers"));
        await using var client = await ConnectAsync(args);
        var scanner = new LastWar.Client.Scanning.MapScanner(client, new LastWar.Client.Scanning.MapScanOptions
        {
            Gap = TimeSpan.FromMilliseconds(args.Int("gap", 70)),
        });
        Console.WriteLine($"{scanner.Options.RequestCount} requests per server, {servers.Count} server(s)");

        foreach (var s in servers)
        {
            var server = new ServerId(s);
            var at = DateTimeOffset.UtcNow;
            try
            {
                var tiles = await scanner.ScanAsync(server);
                db.InsertMapScan(server, at, tiles.ToList(), scanner.LastFeatures, scanner.LastGreen);
                Console.WriteLine($"  {server}: {tiles.Count,6} players, {scanner.LastFeatures.Count} alliance points");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                Console.Error.WriteLine($"  {server}: {ex.Message}");
            }
        }
        return 0;
    }

    static async Task<int> ScanPowerAsync(LastWarDb db, Args args)
    {
        db.EnsureSchema();
        var server = new ServerId(args.Int("server", 0));
        if (server.Value == 0) throw new ArgumentException("--server is required");

        await using var client = await ConnectAsync(args);
        var at = DateTimeOffset.UtcNow;

        var tiles = await new LastWar.Client.Scanning.MapScanner(client).ScanAsync(server);
        Console.WriteLine($"map: {tiles.Count} players");

        var collector = new LastWar.Client.Scanning.PowerCollector(client);
        var samples = await collector.CollectAsync(server, tiles.ToList(), at,
            new Progress<LastWar.Client.Scanning.PowerScanProgress>(p =>
            {
                if (p.Done % 250 == 0)
                    Console.Error.WriteLine($"  {p.Stage}: {p.Done}/{p.Total}, {p.Accounts} accounts");
            }));

        var n = db.InsertPowerSamples(samples.ToList());
        Console.WriteLine($"snapshot {at:O} server {server}: {n} accounts stored");
        return 0;
    }

    static List<int> ParseServers(string spec)
    {
        var outp = new List<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-', 2);
            if (range.Length == 2 && int.TryParse(range[0], out var lo) && int.TryParse(range[1], out var hi))
                for (var i = lo; i <= hi; i++) outp.Add(i);
            else if (int.TryParse(part.Trim(), out var one)) outp.Add(one);
        }
        return outp;
    }

    static async Task<int> CallAsync(Args args)
    {
        await using var client = await ConnectAsync(args);
        var cmd = args.Require("cmd");
        var ps = JsonArg(args) is { } j ? SfsJson.Parse(j) : null;
        var reply = await client.CallAsync(cmd, ps, TimeSpan.FromSeconds(args.Int("timeout", 10)));
        Console.WriteLine(reply);
        return 0;
    }

    static async Task<int> ChatAsync(Args args)
    {
        // --stats reads the database only. No login, so it can never evict a
        // running backfill's session (one session per account).
        if (args.Has("stats"))
        {
            var s = new ChatStore(args.Get("db") ?? DefaultDb);
            var room = args.Require("room");
            var (n, lo, hi) = s.RoomStats(room);
            var done = hi == 0 ? 0 : 100.0 * n / hi;
            Console.WriteLine($"{room}: {n} stored, seq {lo}..{hi} ({done:F1}% of {hi}, {Math.Max(lo - 1, 0)} to go)");
            return 0;
        }

        await using var game = await ConnectAsync(args);
        var creds = await game.WaitForChatCredentialsAsync();
        if (creds is null) { Console.Error.WriteLine("no chat credentials in the init push"); return 2; }
        Console.Error.WriteLine($"chat token captured for {creds.Uid} ({creds.ChatToken.Length} chars)");

        var (preferred, legacy) = await new LastWar.Client.Chat.ChatDirectory()
            .FetchAsync(creds.Uid, game.Server.Value);
        Console.Error.WriteLine($"gateways: {preferred.Count} preferred, {legacy.Count} legacy");
        if (preferred.Count == 0) { Console.Error.WriteLine("no preferred gateway offered"); return 2; }

        LastWar.Client.Chat.ChatConnection? chat = null;
        foreach (var ep in preferred)
        {
            try
            {
                chat = await LastWar.Client.Chat.ChatConnection.ConnectAsync(ep.Uri.Host is { } _ ? ep : ep,
                    creds.Uid, creds.ChatToken);
                Console.Error.WriteLine($"connected: {ep.Uri}");
                break;
            }
            catch (TimeoutException ex) { Console.Error.WriteLine($"  {ep.Uri}: {ex.Message}"); }
        }
        if (chat is null) { Console.Error.WriteLine("no gateway accepted the handshake"); return 2; }
        await using var _ = chat;

        var store = new ChatStore(args.Get("db") ?? DefaultDb);
        store.EnsureSchema();

        if (args.Get("script") is { } scriptPath)
        {
            // One connection, many requests: a game login costs ~20s, so probing several
            // shapes in one session is far cheaper than one process per attempt.
            foreach (var line in File.ReadAllLines(scriptPath))
            {
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
                using var d = System.Text.Json.JsonDocument.Parse(line);
                var name = d.RootElement.GetProperty("cmd").GetString()!;
                object? sps = d.RootElement.TryGetProperty("params", out var pe) ? pe.Clone() : null;
                Console.WriteLine($"==> {name}");
                await chat.SendSignedAsync(name, sps);
                await Task.Delay(1500);
            }
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(args.Int("timeout", 12)));
            try
            {
                await foreach (var f in chat.Frames(drain.Token))
                    Console.WriteLine($"[{f.Command}] {(args.Has("raw") ? f.Raw : Trim(f.Raw, 900))}");
            }
            catch (OperationCanceledException) { }
            return 0;
        }

        if (args.Has("history"))
        {
            var room = args.Require("room");
            var pages = args.Int("pages", 1);
            var delay = TimeSpan.FromMilliseconds(args.Int("delay", 0));
            long cursor = 0;                        // "0" means "from the newest"
            if (args.Has("resume") && store.OldestSeqId(room) is { } back)
            {
                cursor = back;
                Console.WriteLine($"resuming below seq {back}");
            }
            var total = 0;
            for (var page = 0; page < pages; page++)
            {
                // Page 1 pulls the newest block with history.roomsv2, whose per-room value
                // is a FLAG ("0" newest / "1" oldest), NOT a cursor. Paging backwards uses
                // history.roomgoto2 with sort=2; plain history.roomgoto returns the same
                // window every time and never advances.
                string cmdName;
                object ps;
                if (cursor == 0)
                {
                    cmdName = "history.roomsv2";
                    ps = new Dictionary<string, object?>
                    {
                        ["rooms"] = new Dictionary<string, string> { [room] = "0" },
                    };
                }
                else
                {
                    cmdName = "history.roomgoto2";
                    ps = new Dictionary<string, object?>
                    {
                        ["roomId"] = room,
                        ["seqId"] = cursor,
                        ["sort"] = 2,
                        ["count"] = args.Int("count", 50),
                    };
                }
                await chat.SendSignedAsync(cmdName, ps);
                var reply = await WaitFor(chat, cmdName, TimeSpan.FromSeconds(15));
                if (reply is null) { Console.Error.WriteLine("no history reply"); break; }

                var rooms = LastWar.Client.Chat.ChatMessageParser.FromHistory(reply);
                var got = rooms.SelectMany(r => r.Messages).ToList();
                if (got.Count == 0) got = LastWar.Client.Chat.ChatMessageParser.FromGoto(reply);
                if (got.Count == 0) { Console.WriteLine("no more history"); break; }

                var added = store.Insert(got);
                total += got.Count;
                var oldest = got.Min(m => m.SeqId);
                Console.WriteLine($"page {page + 1}: {got.Count} messages (seq {oldest}..{got.Max(m => m.SeqId)}), {added} new");
                if (oldest <= 1) { Console.WriteLine("reached the start of the room"); break; }
                // Stop when a page fails to reach further back, rather than looping forever
                // on a server that clamps the window.
                if (cursor != 0 && oldest >= cursor)
                {
                    Console.WriteLine($"history stopped advancing at seq {oldest}");
                    break;
                }
                cursor = oldest;
                if (delay > TimeSpan.Zero) await Task.Delay(delay);
            }
            var (rm, ro, rn) = store.RoomStats(room);
            Console.WriteLine($"fetched {total}; {room} now holds {rm} messages, seq {ro}..{rn}");
            return 0;
        }

        var seconds = args.Int("seconds", 60);
        Console.Error.WriteLine($"listening {seconds}s ...");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var batch = new List<LastWar.Core.Model.ChatMessage>();
        var frames = 0;
        try
        {
            await foreach (var f in chat.Frames(cts.Token))
            {
                frames++;
                if (LastWar.Client.Chat.ChatMessageParser.FromPush(f) is not { } msg)
                {
                    if (args.Has("raw")) Console.WriteLine($"[{f.Command}] {f.Raw}");
                    continue;
                }
                batch.Add(msg);
                Console.WriteLine($"{msg.SentAt:HH:mm:ss} [{msg.RoomId}] {msg.SenderAlliance,-5} {msg.SenderName}: {msg.Text}");
                if (batch.Count >= 25) { store.Insert(batch); batch.Clear(); }
            }
        }
        catch (OperationCanceledException) { }
        var newRows = store.Insert(batch);
        var (mc, rc) = store.Stats();
        Console.Error.WriteLine($"{frames} frame(s); database holds {mc} messages across {rc} room(s)");
        return 0;
    }

    static async Task<LastWar.Client.Chat.ChatFrame?> WaitFor(
        LastWar.Client.Chat.ChatConnection chat, string command, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var f in chat.Frames(cts.Token))
                if (f.Command == command) return f;
        }
        catch (OperationCanceledException) { }
        return null;
    }
    static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + " ...";

    /// <summary>
    /// JSON parameters from --json or --json-file. Prefer the file: every shell between
    /// here and the command line has its own opinion about quotes.
    /// </summary>
    static string? JsonArg(Args args) =>
        args.Get("json-file") is { } f ? File.ReadAllText(f) : args.Get("json");

    static int LuaDump(Args args)
    {
        var proto = LastWar.Lua.LuaReader.LoadFile(args.Require("file"));
        if (args.Get("find") is { } needle)
        {
            Console.WriteLine(LastWar.Lua.LuaDisassembler.FindUses(proto, needle, args.Int("context", 14), args.Has("exact")));
            return 0;
        }
        if (args.Get("proto") is { } which && int.TryParse(which, out var pi))
        {
            // Direct child of the chunk's main function: CLOSURE #n in the listing.
            var target = proto.Protos.ElementAtOrDefault(pi);
            if (target is null) { Console.Error.WriteLine($"no closure #{pi}"); return 1; }
            Console.WriteLine(LastWar.Lua.LuaDisassembler.Describe(target, pi, $"closure #{pi}"));
            foreach (var k in target.Constants) Console.WriteLine($"    K: {k}");
            return 0;
        }
        var i = 0;
        foreach (var p in proto.Walk())
        {
            Console.WriteLine(LastWar.Lua.LuaDisassembler.Describe(p, i, $"#{i}"));
            if (!args.Has("all") && ++i > 3) { Console.WriteLine("... (--all for every function)"); break; }
        }
        return 0;
    }

    static int ImportMap(LastWarDb db, Args args)
    {
        db.EnsureSchema();
        var path = args.Require("path");
        var files = Directory.Exists(path)
            ? Directory.GetFiles(path, "map_*.csv").OrderBy(f => f).ToArray()
            : [path];
        var total = 0;
        var failed = 0;
        foreach (var f in files)
        {
            IReadOnlyList<MapTile> tiles;
            try { tiles = LegacyImport.ReadMapCsv(f); }
            catch (IOException ex)
            {
                failed++;
                Console.Error.WriteLine($"  {Path.GetFileName(f)}: skipped - {ex.Message}");
                continue;
            }
            if (tiles.Count == 0) { Console.WriteLine($"  {Path.GetFileName(f)}: empty, skipped"); continue; }
            var server = tiles[0].Server;
            var takenAt = new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero);
            db.InsertMapScan(server, takenAt, tiles);
            total += tiles.Count;
            Console.WriteLine($"  {Path.GetFileName(f)}: {tiles.Count,6} tiles -> server {server}");
        }
        Console.WriteLine($"imported {total} tiles from {files.Length - failed}/{files.Length} file(s)");
        return 0;
    }

    static int ImportPower(LastWarDb db, Args args)
    {
        db.EnsureSchema();
        var server = new ServerId(args.Int("server", 0));
        if (server.Value == 0) throw new ArgumentException("--server is required");
        var at = DateTimeOffset.TryParse(args.Get("at"), out var t) ? t : DateTimeOffset.UtcNow;

        var samples = new Dictionary<PlayerUid, PowerSample>();
        if (args.Get("rosters") is { } rosterLog && File.Exists(rosterLog))
        {
            foreach (var s in LegacyImport.ReadAllianceRankLog(rosterLog, server, at)) samples[s.Uid] = s;
            Console.WriteLine($"  alliance rosters: {samples.Count} accounts");
        }
        if (args.Get("detail") is { } detailCsv && File.Exists(detailCsv))
        {
            var before = samples.Count;
            foreach (var s in LegacyImport.ReadPowerCsv(detailCsv, at))
                samples[s.Uid] = s with { TakenAt = at };   // one timestamp for the whole snapshot
            Console.WriteLine($"  tile detail:      {samples.Count - before} further accounts");
        }
        if (samples.Count == 0) throw new ArgumentException("nothing to import; pass --rosters and/or --detail");

        var n = db.InsertPowerSamples(samples.Values.ToList());
        Console.WriteLine($"snapshot {at:O} server {server}: {n} accounts stored");
        return 0;
    }

    static int Snapshots(LastWarDb db, Args args)
    {
        var server = new ServerId(args.Int("server", 0));
        foreach (var t in db.SnapshotTimes(server)) Console.WriteLine(t.ToString("O"));
        return 0;
    }

    static int Activity(LastWarDb db, Args args)
    {
        var server = new ServerId(args.Int("server", 0));
        var times = db.SnapshotTimes(server);
        if (times.Count < 2)
        {
            Console.WriteLine($"server {server} has {times.Count} snapshot(s); need 2 to compare.");
            if (times.Count == 1) Console.WriteLine($"baseline stored at {times[0]:O} - take another later.");
            return 0;
        }

        var history = times.Select(t => db.LoadSnapshot(server, t)).ToList();
        // Active = a rise anywhere in the 7 days to the newest snapshot, not only on the last step.
        var results = ActivityAnalyzer.Compare(history, TimeSpan.FromDays(args.Int("days", 7)));
        var gone = ActivityAnalyzer.Disappeared(history[^2], history[^1]);

        Console.WriteLine($"{times[^2]:u} -> {times[^1]:u}   ({times.Count} snapshots, active = a rise in the last {args.Int("days", 7)} days)");
        foreach (var g in results.GroupBy(r => r.Status).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-7} {g.Count(),6}");
        Console.WriteLine($"  gone    {gone.Count,6}");

        Console.WriteLine("\ntop risers:");
        foreach (var r in results.Where(r => r.Delta > 0).Take(15))
            Console.WriteLine($"  {r.Name,-22} +{r.Delta,-12:N0} {r.Power,14:N0}  {r.AllianceTag}");

        if (args.Get("out") is { } outPath)
        {
            Csv.Write(outPath,
                ["uid", "name", "allianceTag", "originServer", "power", "prevPower", "delta", "status", "activeEver"],
                results.Select(r => new string?[]
                {
                    r.Uid.Value, r.Name, r.AllianceTag, r.OriginServer?.ToString(),
                    r.Power.ToString(), r.PreviousPower?.ToString(), r.Delta.ToString(),
                    r.Status.ToString(), r.ActiveEver.ToString(),
                }));
            Console.WriteLine($"\n-> {outPath}");
        }
        return 0;
    }
}
