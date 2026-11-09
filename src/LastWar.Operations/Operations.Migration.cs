using LastWar.Storage;
using LastWar.Protocol.Sfs;
using LastWar.Storage.Import;

namespace LastWar.Operations;

public static partial class Operations
{
    static void AddMigrationOperations()
    {
        Add("migration.applicants", "Migration", "Transfer applicants",
            "Everyone who has applied to transfer TO this session's server, with where they are "
            + "coming from. Needs an account that lives on the server - guests cannot be created on "
            + "a server in a migration wave, and the list is only answered from inside it. The "
            + "R4/R5 check the game shows is client-side only; any resident account works.",
            async c =>
            {
                var client = c.Require();
                var p = new SfsObject();
                p.PutInt("startIndex", 0);
                p.PutInt("number", 200);   // observed to be ignored - the whole list comes back
                var reply = await client.CallAsync(
                    "season.migrate.apply.list.info", p, TimeSpan.FromSeconds(12), c.Cancellation);

                var code = reply.Scalar("errorCode");
                if (code.Length > 0 && code != "0")
                    return OperationResult.Text($"the server refused: errorCode {code} {reply.GetString("errorMsg")}");

                // The array's key has not been pinned down, so take the first array of objects.
                var list = reply.Keys
                    .Select(k => reply.TryGetArray(k, out var a) ? a : null)
                    .FirstOrDefault(a => a is not null && a.Objects().Any());
                var to = client.Server.Value;

                var rows = (list?.Objects() ?? [])
                    .Select(a => new object?[]
                    {
                        a.GetString("name"), a.GetString("uid"), a.GetLong("power"),
                        a.GetInt("serverId"), to, a.GetString("alName"), a.GetString("abbr"),
                        a.GetLong("applyScore"), a.GetInt("applyState"), a.GetInt("identity"),
                        a.GetLong("time"), a.GetString("message"), a.Scalar("flag"),
                    })
                    .OrderByDescending(r => Convert.ToInt64(r[2]))
                    .ToList();

                // Hero power lives on each SOURCE server's THP board, not on ours - rank.get
                // answers cross-server when given a serverId. The board is the top 200, so anyone
                // below that simply has no value, which is expected rather than a failure.
                var thp = new Dictionary<string, (long Power, int Rank)>();
                foreach (var source in rows.Select(r => Convert.ToInt32(r[3])).Distinct())
                {
                    foreach (var (uid, entry) in await BoardAsync(client, 13, source, "heroPower", c.Cancellation))
                        thp[uid] = entry;
                    c.Report($"hero power board for {source}");
                }
                rows = [.. rows.Select(r =>
                {
                    var hit = thp.TryGetValue((string)r[1]!, out var e);
                    return r.Concat([hit ? e.Power : null, hit ? e.Rank : null]).ToArray();
                })];

                string[] columns =
                [
                    "name", "uid", "power", "fromServer", "toServer", "alName", "abbr",
                    "applyScore", "applyState", "identity", "time", "message", "flag",
                    "heroPower", "thpRank",
                ];

                // A new dated file each run: earlier captures are kept, never overwritten, so
                // who applied and who withdrew can be read by comparing them.
                var dir = Path.Combine(c.DataDir, "migration");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"applicants_{to}_{DateTime.UtcNow:yyyy-MM-dd_HHmm}.csv");
                Csv.Write(file, columns, rows.Select(r => r.Select(v => v?.ToString()).ToArray()));
                // And into the database, where the player and server pages can join it by uid.
                var insights = new Insights(c.Database); insights.EnsureSchema(); insights.ImportApplicants(file, DateTimeOffset.UtcNow);

                var sources = rows.GroupBy(r => r[3]).OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} x{g.Count()}");
                var ranked = rows.Count(r => r[13] is not null);
                return OperationResult.Table(
                    $"{rows.Count} applicants to {to} ({string.Join(", ", sources.Take(6))}), "
                    + $"{ranked} on their server's hero power board - saved {Path.GetFileName(file)}",
                    columns, rows);
            });

        Add("board.get", "Scan", "Leaderboard",
            "Any server leaderboard, for any server - rank.get answers cross-server. 4 is total "
            + "power, 13 total hero power, 14 PvE stage, 15 strongest single hero, 5 building "
            + "power, 3 kills, 2 alliance power.",
            async c =>
            {
                var type = c.Int("type", 13);
                var rows = new List<object?[]>();
                foreach (var server in ParseServers(c.Text("servers", "1967")))
                {
                    var p = new SfsObject();
                    p.PutInt("type", type);
                    p.PutInt("serverId", server);
                    var reply = await c.Require().CallAsync("rank.get", p, TimeSpan.FromSeconds(12), c.Cancellation);
                    var arr = reply.TryGetArray("serverRanking", out var a) ? a
                            : reply.TryGetArray("allianceRanking", out var al) ? al : null;
                    var rank = 0;
                    foreach (var e in arr?.Objects() ?? [])
                        rows.Add([
                            server, ++rank, e.GetString("name"), e.GetString("abbr"),
                            e.GetLong("power"), e.GetLong("heroPower"), e.GetLong("armyKill"),
                            e.GetString("uid"),
                        ]);
                    c.Report($"board {type} for {server}: {rank} rows");
                }
                return OperationResult.Table($"board {type}: {rows.Count} rows",
                    ["server", "rank", "name", "alliance", "power", "heroPower", "kills", "uid"], rows);
            },
            ps:
            [
                new OperationParam("type", "Board type", "number", "13", Required: true),
                new OperationParam("servers", "Servers", "text", "1967", Required: true,
                    Help: "A list or a range: 1967, or 1957-1988."),
            ]);
    }

    /// <summary>One player board for one server, as uid -> (value, rank).</summary>
    static async Task<Dictionary<string, (long Power, int Rank)>> BoardAsync(
        LastWar.Client.LastWarClient client, int type, int server, string field, CancellationToken ct)
    {
        var p = new SfsObject();
        p.PutInt("type", type);
        p.PutInt("serverId", server);
        var reply = await client.TryCallAsync("rank.get", p, TimeSpan.FromSeconds(12), ct);
        var outp = new Dictionary<string, (long, int)>();
        if (reply is null || !reply.TryGetArray("serverRanking", out var arr)) return outp;
        var rank = 0;
        foreach (var e in arr.Objects()) outp[e.GetString("uid")] = (e.GetLong(field), ++rank);
        return outp;
    }
}
