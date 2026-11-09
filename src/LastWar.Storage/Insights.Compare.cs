namespace LastWar.Storage;

/// <summary>
/// Side-by-side comparison of up to four servers, alliances or players.
///
/// Every kind answers in the same shape so one page draws them all:
///   entities: [{ key, label, ...identity }]
///   metrics:  [{ key, label, format, better, series: { entityKey: [{ at, v }] } }]
/// Each metric is a time series per entity; the page shows the latest value of every metric in a
/// table and charts whichever one is picked. "better" is "high" or "low" (ranks).
///
/// Everything is read from the append-only snapshot tables, so a comparison made after a
/// transfer still shows each side as it was at every earlier snapshot.
/// </summary>
public sealed partial class Insights
{
    sealed class Metric(string key, string label, string format, string better = "high")
    {
        public readonly Dictionary<string, List<(string At, double V)>> Series = new();
        public void Add(string entity, string at, double v)
        {
            if (!Series.TryGetValue(entity, out var s)) Series[entity] = s = [];
            s.Add((at, v));
        }
        public Dictionary<string, object?> Shape() => new()
        {
            ["key"] = key, ["label"] = label, ["format"] = format, ["better"] = better,
            ["series"] = Series.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(p => p.At, StringComparer.Ordinal)
                .Select(p => new Dictionary<string, object?> { ["at"] = p.At, ["v"] = p.V }).ToList()),
        };
    }

    static List<Dictionary<string, object?>> Shape(IEnumerable<Metric> ms) =>
        ms.Where(m => m.Series.Count > 0).Select(m => m.Shape()).ToList();

    static string In(string col, int n, string prefix) =>
        $"{col} IN ({string.Join(",", Enumerable.Range(0, n).Select(i => $"@{prefix}{i}"))})";

    static (string, object)[] Ps(IEnumerable<object> values, string prefix) =>
        values.Select((v, i) => ($"@{prefix}{i}", v)).ToArray();

    static double Stage(double raw) => raw / 10 - 199896;   // PvE board: in-game stage (AGENTS.md)

    static long? Extra(string? extra, string key) =>
        (extra ?? "").Split(';').Select(kv => kv.Split('=', 2)).Where(p => p.Length == 2 && p[0] == key)
            .Select(p => long.TryParse(p[1], out var v) ? v : (long?)null).FirstOrDefault();

    // ---- servers

    public Dictionary<string, object?> CompareServers(IReadOnlyList<int> ids)
    {
        using var c = Open();
        var ps = Ps(ids.Cast<object>(), "s");
        var accounts = new Metric("accounts", "Accounts measured", "int");
        var total = new Metric("power", "Total power", "big");
        foreach (var r in Q(c, $"""
            SELECT server_id AS s, taken_at AS at, COUNT(*) AS n, SUM(power) AS p
            FROM power_sample WHERE {In("server_id", ids.Count, "s")} GROUP BY server_id, taken_at;
            """, ps))
        {
            accounts.Add(r["s"]!.ToString()!, (string)r["at"]!, Convert.ToDouble(r["n"]));
            total.Add(r["s"]!.ToString()!, (string)r["at"]!, Convert.ToDouble(r["p"]));
        }

        var rows = Q(c, $"""
            SELECT server_id AS s, board_type AS b, taken_at AS at, rank, uid, value, name, alliance_tag AS tag, extra
            FROM board_sample WHERE {In("server_id", ids.Count, "s")} ORDER BY server_id, board_type, taken_at, rank;
            """, ps);
        var ms = new List<Metric> { accounts, total };
        void Board(int type, string what, int depth)
        {
            var sum = new Metric($"b{type}_sum", $"Top {depth} {what} - total", "big");
            var first = new Metric($"b{type}_1", $"#1 {what}", "big");
            var last = new Metric($"b{type}_last", $"#{depth} {what} (entry bar)", "big");
            var rose = new Metric($"b{type}_rose", $"Top {depth} {what} - risen since last board", "int");
            foreach (var g in rows.Where(r => Convert.ToInt32(r["b"]) == type).GroupBy(r => r["s"]!.ToString()!))
            {
                Dictionary<string, double>? prev = null;
                foreach (var snap in g.GroupBy(r => (string)r["at"]!).OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    var vals = snap.GroupBy(r => (string)r["uid"]!).ToDictionary(x => x.Key, x => Convert.ToDouble(x.First()["value"]));
                    var byRank = snap.OrderBy(r => Convert.ToInt32(r["rank"])).ToList();
                    sum.Add(g.Key, snap.Key, vals.Values.Sum());
                    first.Add(g.Key, snap.Key, Convert.ToDouble(byRank[0]["value"]));
                    last.Add(g.Key, snap.Key, Convert.ToDouble(byRank[^1]["value"]));
                    // Only a rise proves activity; a player dropping off the board proves nothing.
                    if (prev is not null)
                        rose.Add(g.Key, snap.Key, vals.Count(kv => prev.TryGetValue(kv.Key, out var p) && kv.Value > p));
                    prev = vals;
                }
            }
            ms.AddRange([sum, first, last, rose]);
        }
        Board(4, "power", 200);
        Board(13, "hero power", 200);
        Board(3, "kills", 100);
        Board(15, "strongest hero", 100);

        var pve1 = new Metric("b14_1", "PvE stage - #1", "int");
        var pve100 = new Metric("b14_last", "PvE stage - #100", "int");
        var al10 = new Metric("b2_top10", "Top 10 alliances - power", "big");
        var al1 = new Metric("b2_1", "#1 alliance power", "big");
        var alk = new Metric("b1_top10", "Top 10 alliances - kills", "big");
        foreach (var g in rows.GroupBy(r => (S: r["s"]!.ToString()!, B: Convert.ToInt32(r["b"]), At: (string)r["at"]!)))
        {
            var byRank = g.OrderBy(r => Convert.ToInt32(r["rank"])).ToList();
            switch (g.Key.B)
            {
                case 14:
                    pve1.Add(g.Key.S, g.Key.At, Stage(Convert.ToDouble(byRank[0]["value"])));
                    pve100.Add(g.Key.S, g.Key.At, Stage(Convert.ToDouble(byRank[^1]["value"])));
                    break;
                case 2:
                    al10.Add(g.Key.S, g.Key.At, byRank.Take(10).Sum(r => Convert.ToDouble(r["value"])));
                    al1.Add(g.Key.S, g.Key.At, Convert.ToDouble(byRank[0]["value"]));
                    break;
                case 1:
                    alk.Add(g.Key.S, g.Key.At, byRank.Take(10).Sum(r => Convert.ToDouble(r["value"])));
                    break;
            }
        }
        ms.AddRange([pve1, pve100, al10, al1, alk]);

        // The strongest alliances on each server's newest alliance-power board.
        var entities = ids.Select(id =>
        {
            var latest = rows.Where(r => Convert.ToInt32(r["b"]) == 2 && Convert.ToInt32(r["s"]) == id)
                .GroupBy(r => (string)r["at"]!).OrderBy(g => g.Key, StringComparer.Ordinal).LastOrDefault();
            return new Dictionary<string, object?>
            {
                ["key"] = id.ToString(), ["label"] = $"#{id}", ["server"] = id,
                ["alliances"] = latest?.OrderBy(r => Convert.ToInt32(r["rank"])).Take(8).Select(r => new Dictionary<string, object?>
                {
                    ["allianceId"] = r["uid"], ["tag"] = r["tag"], ["name"] = r["name"], ["power"] = r["value"],
                    ["members"] = Extra((string?)r["extra"], "nums"),
                }).ToList(),
            };
        }).ToList();
        return new() { ["kind"] = "servers", ["entities"] = entities, ["metrics"] = Shape(ms) };
    }

    // ---- alliances

    public Dictionary<string, object?> CompareAlliances(IReadOnlyList<string> ids)
    {
        using var c = Open();
        var ps = Ps(ids, "a");
        var members = new Metric("members", "Members measured", "int");
        var power = new Metric("power", "Member power (sum)", "big");
        var avg = new Metric("avg", "Average member power", "big");
        var kills = new Metric("kills", "Member kills (sum)", "big");
        foreach (var r in Q(c, $"""
            SELECT alliance_id AS a, taken_at AS at, COUNT(*) AS n, SUM(power) AS p, SUM(kill_army) AS k
            FROM power_sample WHERE {In("alliance_id", ids.Count, "a")} GROUP BY alliance_id, taken_at;
            """, ps))
        {
            var (a, at, n) = ((string)r["a"]!, (string)r["at"]!, Convert.ToDouble(r["n"]));
            members.Add(a, at, n);
            power.Add(a, at, Convert.ToDouble(r["p"]));
            avg.Add(a, at, n > 0 ? Convert.ToDouble(r["p"]) / n : 0);
            if (Convert.ToDouble(r["k"]) > 0) kills.Add(a, at, Convert.ToDouble(r["k"]));
        }

        var boardPower = new Metric("board_power", "Alliance power (board)", "big");
        var boardRank = new Metric("board_rank", "Alliance power rank on its server", "rank", "low");
        var boardKills = new Metric("board_kills", "Alliance kills (board)", "big");
        foreach (var r in Q(c, $"""
            SELECT uid AS a, board_type AS b, taken_at AS at, rank, value FROM board_sample
            WHERE board_type IN (1, 2) AND {In("uid", ids.Count, "a")};
            """, ps))
        {
            var (a, at) = ((string)r["a"]!, (string)r["at"]!);
            if (Convert.ToInt32(r["b"]) == 2) { boardPower.Add(a, at, Convert.ToDouble(r["value"])); boardRank.Add(a, at, Convert.ToDouble(r["rank"])); }
            else boardKills.Add(a, at, Convert.ToDouble(r["value"]));
        }

        var top200 = new Metric("in_top200", "Members in server top 200 power", "int");
        var topHero = new Metric("in_top200_hero", "Members in server top 200 hero power", "int");
        var heroSum = new Metric("hero_sum", "Hero power of members on the board", "big");
        foreach (var r in Q(c, $"""
            SELECT alliance_id AS a, board_type AS b, taken_at AS at, COUNT(*) AS n, SUM(value) AS v FROM board_sample
            WHERE board_type IN (4, 13) AND {In("alliance_id", ids.Count, "a")} GROUP BY alliance_id, board_type, taken_at;
            """, ps))
        {
            var (a, at) = ((string)r["a"]!, (string)r["at"]!);
            if (Convert.ToInt32(r["b"]) == 4) top200.Add(a, at, Convert.ToDouble(r["n"]));
            else { topHero.Add(a, at, Convert.ToDouble(r["n"])); heroSum.Add(a, at, Convert.ToDouble(r["v"])); }
        }

        var entities = ids.Select(id =>
        {
            // Identity and roster from the newest power snapshot that has this alliance in it.
            var roster = Q(c, """
                SELECT uid, name, power, alliance_tag AS tag, alliance_name AS "allianceName", server_id AS server, taken_at AS at
                FROM power_sample WHERE alliance_id = @a
                  AND taken_at = (SELECT MAX(taken_at) FROM power_sample WHERE alliance_id = @a)
                ORDER BY power DESC;
                """, ("@a", id));
            var top = roster.FirstOrDefault();
            var tag = top?["tag"] as string;
            if (string.IsNullOrEmpty(tag))
                tag = Q(c, "SELECT alliance_tag AS t FROM map_tile WHERE alliance_id = @a LIMIT 1;", ("@a", id)).FirstOrDefault()?["t"] as string;
            return new Dictionary<string, object?>
            {
                ["key"] = id, ["label"] = string.IsNullOrEmpty(tag) ? id[..Math.Min(8, id.Length)] : tag,
                ["allianceId"] = id, ["name"] = top?["allianceName"], ["server"] = top?["server"], ["at"] = top?["at"],
                ["members"] = roster.Take(10).ToList(),
            };
        }).ToList();
        return new()
        {
            ["kind"] = "alliances", ["entities"] = entities,
            ["metrics"] = Shape([members, power, avg, kills, boardPower, boardRank, boardKills, top200, topHero, heroSum]),
        };
    }

    // ---- players

    public Dictionary<string, object?> ComparePlayers(IReadOnlyList<string> uids)
    {
        using var c = Open();
        var ps = Ps(uids, "u");
        var power = new Metric("power", "Power", "big");
        var kills = new Metric("kills", "Kills", "big");
        foreach (var r in Q(c, $"""
            SELECT uid AS u, taken_at AS at, power, kill_army AS k FROM power_sample WHERE {In("uid", uids.Count, "u")};
            """, ps))
        {
            power.Add((string)r["u"]!, (string)r["at"]!, Convert.ToDouble(r["power"]));
            if (Convert.ToDouble(r["k"]) > 0) kills.Add((string)r["u"]!, (string)r["at"]!, Convert.ToDouble(r["k"]));
        }

        var ms = new List<Metric> { power, kills };
        var byType = new Dictionary<int, (Metric Value, Metric Rank)>();
        foreach (var b in Boards.All.Where(b => !b.Alliance))
        {
            var value = new Metric($"b{b.Type}", b.Name, b.Type == 14 ? "int" : "big");
            var rank = new Metric($"b{b.Type}_rank", $"Rank: {b.Name}", "rank", "low");
            byType[b.Type] = (value, rank);
            // 5 (HQ race) has no meaningful value; 3's value is kills, already charted from power scans.
            if (b.ValueField.Length > 0 && b.Type is not 5 and not 3) ms.Add(value);
            ms.Add(rank);
        }
        foreach (var r in Q(c, $"""
            SELECT uid AS u, board_type AS b, taken_at AS at, rank, value FROM board_sample
            WHERE {In("uid", uids.Count, "u")};
            """, ps))
        {
            if (!byType.TryGetValue(Convert.ToInt32(r["b"]), out var m)) continue;
            var (u, at, v) = ((string)r["u"]!, (string)r["at"]!, Convert.ToDouble(r["value"]));
            m.Value.Add(u, at, Convert.ToInt32(r["b"]) == 14 ? Stage(v) : v);
            m.Rank.Add(u, at, Convert.ToDouble(r["rank"]));
        }

        var profiles = new ProfileStore(path).Latest(uids);
        var entities = uids.Select(u =>
        {
            profiles.TryGetValue(u, out var p);
            var seen = Q(c, """
                SELECT name, server_id AS server, alliance_tag AS tag, alliance_id AS "allianceId" FROM power_sample
                WHERE uid = @u ORDER BY taken_at DESC LIMIT 1;
                """, ("@u", u)).FirstOrDefault();
            return new Dictionary<string, object?>
            {
                ["key"] = u, ["uid"] = u,
                ["label"] = p?.Name is { Length: > 0 } n ? n : seen?["name"] as string ?? u,
                ["home"] = p?.HomeServer ?? seen?["server"], ["based"] = p?.CurrentServer,
                ["tag"] = p?.AllianceTag ?? seen?["tag"], ["allianceId"] = p?.AllianceId ?? seen?["allianceId"],
                ["origin"] = u.Length >= 6 && int.TryParse(u[^6..], out var o) ? o : null,
                ["level"] = p?.Level,
            };
        }).ToList();
        return new() { ["kind"] = "players", ["entities"] = entities, ["metrics"] = Shape(ms) };
    }
}
