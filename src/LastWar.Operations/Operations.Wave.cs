using System.Globalization;
using LastWar.Protocol.Sfs;
using LastWar.Storage;

namespace LastWar.Operations;

public static partial class Operations
{
    static void AddWaveOperations()
    {
        Add("board.snapshot", "Migration", "Save leaderboards",
            "Stores every server leaderboard for a server range - power, hero power, kills, PvE, "
            + "strongest hero, HQ race, the three battlefields and both alliance boards - so servers "
            + "and alliances can be compared over time. Hero power is only published on its board. "
            + "Append-only: each run is a new dated snapshot. Cross-server, so a guest will do.",
            async c =>
            {
                var wave = new WaveStore(c.Database);
                wave.EnsureSchema();
                var types = c.Text("types", Boards.DefaultTypes).Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => int.Parse(t.Trim(), CultureInfo.InvariantCulture)).ToArray();
                var gap = TimeSpan.FromMilliseconds(c.Int("gap", 250));
                var at = DateTimeOffset.UtcNow;
                var rows = new List<object?[]>();
                foreach (var server in ParseServers(c.Text("servers", "1957-1988")))
                    foreach (var type in types)
                    {
                        var board = Boards.Find(type) ?? new Boards.Board(type, $"type {type}", "power", [], false, 0);
                        var p = new SfsObject();
                        p.PutInt("type", type);
                        p.PutInt("serverId", server);
                        var reply = await c.Require().TryCallAsync("rank.get", p, TimeSpan.FromSeconds(12), c.Cancellation);
                        var parsed = reply is null ? [] : ParseBoard(reply, board);
                        var n = wave.InsertBoard(at, server, type, parsed);
                        rows.Add([server, type, board.Name, n, reply is null ? "no answer" : ""]);
                        c.Report($"{server} {board.Name}: {n} rows");
                        await Task.Delay(LastWar.Client.Jitter.Of(gap), c.Cancellation);
                    }
                var missing = rows.Count(r => (string?)r[4] == "no answer");
                return OperationResult.Table(
                    $"{rows.Count} boards saved at {at:u}, {rows.Sum(r => Convert.ToInt64(r[3])):N0} rows"
                    + (missing > 0 ? $"; {missing} got no answer - rerun those" : ""),
                    ["server", "board", "name", "rows", "note"], rows);
            },
            ps:
            [
                new OperationParam("servers", "Servers", "text", "1957-1988", Required: true),
                new OperationParam("types", "Boards", "text", Boards.DefaultTypes,
                    Help: "rank.get types. 4 power, 13 hero power, 3 kills, 14 PvE, 15 strongest hero, 5 HQ race, 18-20 battlefields, 1-2 alliance boards."),
                new OperationParam("gap", "Gap between boards (ms)", "number", "250"),
            ]);
        Add("migration.compare", "Migration", "Compare before and after",
            "Follows every player tied to a server range from before a moment to after it: who moved "
            + "and where, who gained, who lost, who left and who arrived - and what that did to each "
            + "server and alliance. Home server comes from profile sweeps where there are any (a "
            + "player over on another server for a war is NOT a move), from maps otherwise. Uses only "
            + "data taken after the cutoff for the after side, and says which servers have none.",
            c =>
            {
                var (lo, hi) = Range(c.Text("servers", "1957-1988"));
                if (!DateTimeOffset.TryParse(c.Text("cutoff"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var cutoff))
                    throw new ArgumentException("a cutoff time is required, e.g. 2026-09-25 12:00 (UTC)");
                bool In(int s) => s >= lo && s <= hi;

                var wave = new WaveStore(c.Database);
                wave.EnsureSchema();
                var (before, bCov) = wave.StateIn(lo, hi, null, cutoff);
                // "until" closes the after side, so a report on one transfer window stays the same
                // when later windows are scanned. Empty = everything since the cutoff.
                var until = DateTimeOffset.TryParse(c.Text("until"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u) ? u : DateTimeOffset.UtcNow.AddYears(1);
                var (after, aCov) = wave.StateIn(lo, hi, cutoff, until);
                var unscanned = bCov.Where(In).Except(aCov).OrderBy(s => s).ToList();

                var all = new List<(string Status, PlayerState? B, PlayerState? A)>();
                foreach (var uid in before.Keys.Union(after.Keys))
                {
                    before.TryGetValue(uid, out var b);
                    after.TryGetValue(uid, out var a);
                    // Only players who belonged to the range on at least one side.
                    if (!(b is not null && In(b.Server)) && !(a is not null && In(a.Server))) continue;
                    var status = (b, a) switch
                    {
                        ({ } x, { } y) when x.Server != y.Server => "moved",
                        ({ }, { }) => "stayed",
                        ({ } x, null) when !aCov.Contains(x.Server) => "not rescanned",
                        ({ }, null) => "gone",
                        _ => "arrived",
                    };
                    all.Add((status, b, a));
                }

                long? Delta((string, PlayerState? B, PlayerState? A) r) =>
                    r.B?.Power is { } p0 && r.A?.Power is { } p1 ? p1 - p0 : null;

                var counts = string.Join(", ", all.GroupBy(r => r.Status).OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Count():N0} {g.Key}"));
                var visitors = all.Count(r => r.A?.Visiting == true);
                var sources = $"before from {Src(before.Values)}, after from {Src(after.Values)}";
                var summary = $"before <= {cutoff:u} < after: {counts}; {visitors:N0} based away from home after; {sources}"
                    + (unscanned.Count > 0 ? $". NO after-data yet for {string.Join(",", unscanned)}" : "");

                var show = c.Text("show", "moved").ToLowerInvariant();
                if (show == "flows")
                {
                    var flows = all.Where(r => r.Status == "moved")
                        .GroupBy(r => (From: r.B!.Server, To: r.A!.Server))
                        .OrderByDescending(g => g.Count())
                        .Select(g => new object?[]
                        {
                            g.Key.From, g.Key.To, g.Count(),
                            g.Sum(r => r.B!.Power ?? 0), g.Sum(r => r.A!.Power ?? r.B!.Power ?? 0),
                            TopTags(g.Select(r => r.B!.AllianceTag)),
                        });
                    return Task.FromResult(OperationResult.Table(summary,
                        ["from", "to", "players", "power before", "power after", "alliances they left"], flows));
                }

                if (show == "servers")
                {
                    var wasOn = all.Where(r => r.B is not null).ToLookup(r => r.B!.Server);
                    var nowOn = all.Where(r => r.A is not null).ToLookup(r => r.A!.Server);
                    var servers = Enumerable.Range(lo, hi - lo + 1).Select(s =>
                    {
                        var was = wasOn[s].ToList();
                        var now = nowOn[s].ToList();
                        var left = was.Where(r => r.A is not null && r.A.Server != s).ToList();
                        var came = now.Where(r => r.B is null || r.B.Server != s).ToList();
                        long P(IEnumerable<PlayerState?> xs) => xs.Sum(x => x?.Power ?? 0);
                        return new object?[]
                        {
                            s, was.Count, P(was.Select(r => r.B)), now.Count, P(now.Select(r => r.A)),
                            P(now.Select(r => r.A)) - P(was.Select(r => r.B)),
                            left.Count, P(left.Select(r => r.B)), came.Count, P(came.Select(r => r.A)),
                            now.Count(r => r.A!.Visiting), aCov.Contains(s) ? "" : "NO after-data",
                        };
                    });
                    return Task.FromResult(OperationResult.Table(summary,
                        ["server", "players before", "power before", "players after", "power after", "change",
                         "left", "power left", "arrived", "power arrived", "based elsewhere now", "note"], servers));
                }

                if (show == "alliances")
                {
                    var keys = all.SelectMany(r => new[] { r.B, r.A }).Where(x => x is not null && x.AllianceId.Length > 0)
                        .Select(x => x!.AllianceId).Distinct();
                    var wasBy = all.Where(r => r.B is not null).ToLookup(r => r.B!.AllianceId);
                    var nowBy = all.Where(r => r.A is not null).ToLookup(r => r.A!.AllianceId);
                    var alliances = keys.Select(id =>
                    {
                        var was = wasBy[id].ToList();
                        var now = nowBy[id].ToList();
                        var label = now.Select(r => r.A!).Concat(was.Select(r => r.B!)).First();
                        var home = now.Select(r => r.A!.Server).Concat(was.Select(r => r.B!.Server))
                            .GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
                        long wasP = was.Sum(r => r.B!.Power ?? 0), nowP = now.Sum(r => r.A!.Power ?? 0);
                        return new object?[]
                        {
                            label.AllianceTag, home, was.Count, wasP, now.Count, nowP, nowP - wasP,
                            was.Count(r => r.A?.AllianceId != id), now.Count(r => r.B?.AllianceId != id),
                            was.Count(r => r.Status == "moved"), id,
                        };
                    }).Where(r => In((int)r[1]!)).OrderByDescending(r => Math.Abs((long)r[6]!)).Take(c.Int("limit", 200));
                    return Task.FromResult(OperationResult.Table(summary,
                        ["alliance", "server", "members before", "power before", "members after", "power after",
                         "change", "left it", "joined it", "moved server", "alliance id"], alliances));
                }

                var picked = show switch
                {
                    "gained" => all.Where(r => Delta(r) > 0).OrderByDescending(r => Delta(r)),
                    "lost" => all.Where(r => Delta(r) < 0).OrderBy(r => Delta(r)),
                    "visiting" => all.Where(r => r.A?.Visiting == true).OrderByDescending(r => r.A!.Power ?? 0),
                    "all" => all.OrderByDescending(r => r.A?.Power ?? r.B?.Power ?? 0),
                    _ => all.Where(r => r.Status == show).OrderByDescending(r => r.A?.Power ?? r.B?.Power ?? 0),
                };
                var rows = picked.Select(r => new object?[]
                {
                    r.A?.Name ?? r.B?.Name, r.Status, r.B?.Server, r.A?.Server,
                    r.A?.Visiting == true ? r.A.Based : null,
                    r.B?.AllianceTag, r.A?.AllianceTag, r.B?.Power, r.A?.Power, Delta(r),
                    r.B?.HeroPower, r.A?.HeroPower, r.A?.Uid ?? r.B?.Uid,
                });
                return Task.FromResult(OperationResult.Table(summary,
                    ["name", "status", "from", "to", "based on now", "alliance before", "alliance after",
                     "power before", "power after", "change", "thp before", "thp after", "uid"], rows));
            },
            needsSession: false,
            ps:
            [
                new OperationParam("cutoff", "Cutoff (UTC)", "text", Required: true,
                    Help: "Any moment between the last before-scan and the first after-scan, e.g. when the transfer ran."),
                new OperationParam("until", "Until (UTC)", "text",
                    Help: "Optional end of the after side - the newest scan up to here is used. Pin it to keep an old window's report fixed."),
                new OperationParam("servers", "Servers", "text", "1957-1988", Required: true,
                    Help: "A contiguous range."),
                new OperationParam("show", "Show", "text", "moved",
                    Help: "moved, flows, servers, alliances, gained, lost, gone, arrived, stayed, visiting, all."),
                new OperationParam("limit", "Alliances shown", "number", "200"),
            ]);

        static string Src(IEnumerable<PlayerState> xs) =>
            string.Join(" + ", xs.GroupBy(x => x.Source).OrderByDescending(g => g.Count()).Select(g => $"{g.Count():N0} {g.Key}"));

        static string TopTags(IEnumerable<string> tags) =>
            string.Join(" ", tags.Where(t => t.Length > 0).GroupBy(t => t).OrderByDescending(t => t.Count()).Take(4)
                .Select(t => $"{t.Key}x{t.Count()}"));
    }

    /// <summary>
    /// A rank.get reply as rows. Numbers are read through Scalar because the server is not
    /// consistent about widths - armyKill arrives as a STRING on the alliance boards.
    /// </summary>
    static List<BoardRow> ParseBoard(SfsObject reply, Boards.Board board)
    {
        var arr = reply.TryGetArray("serverRanking", out var a) ? a
                : reply.TryGetArray("allianceRanking", out var al) ? al : null;
        var outp = new List<BoardRow>();
        var rank = 0;
        foreach (var e in arr?.Objects() ?? [])
        {
            rank++;
            long Num(string k) => long.TryParse(e.Scalar(k), out var v) ? v
                                : double.TryParse(e.Scalar(k), CultureInfo.InvariantCulture, out var d) ? (long)d : 0;
            var cross = (int)Num("crossFightSrcServerId");
            outp.Add(new BoardRow(rank, e.GetString("uid"),
                board.Alliance ? e.GetString("alliancename") : e.GetString("name"),
                e.GetString("abbr"), board.ValueField.Length > 0 ? Num(board.ValueField) : 0)
            {
                AllianceId = board.Alliance ? e.GetString("uid") : e.GetString("allianceId"),
                SrcServer = cross > 0 ? cross : (int)Num("srcServer"),
                Extra = string.Join(";", board.Extra.Where(k => e.Has(k)).Select(k => $"{k}={e.Scalar(k)}")),
            });
        }
        return outp;
    }
    static (int Lo, int Hi) Range(string spec)
    {
        var ids = ParseServers(spec);
        return (ids.Min(), ids.Max());
    }
}
