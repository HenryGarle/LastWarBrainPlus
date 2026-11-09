namespace LastWar.Storage;

public sealed partial class Insights
{
    /// <summary>
    /// One measure a ranking can be built on. Source "profile" reads a full profile sweep (every
    /// player, home server from the profile); "board" reads a leaderboard snapshot (top 50-200 per
    /// server, only what the game publishes). Expr is the per-row value (players) or the aggregate
    /// (alliances, servers).
    /// </summary>
    public sealed record RankMetric(string Entity, string Key, string Label, string Source, int Board, string Expr,
        string Format = "big", string Note = "", bool Seen = false);

    static string Pve(string v) => $"({v} / 10 - 199896)";   // PvE board raw value -> in-game stage

    /// <summary>
    /// Fighting strength of one profile row, as the Matchup counts it: estimated hero power x how
    /// recently they were in the game AT THAT SNAPSHOT (last seen: a day 1, 3 days 0.75, a week 0.35,
    /// older - or only a game-side mass touch - 0.1). Last seen only, so it can be read for old sweeps.
    /// </summary>
    static string Fight() => $"""
        CAST({HeroModel.EstimateSql("power", "army_power", "0.78")} * (CASE WHEN {PlayerActivity.NotALogin("last_seen_at")} THEN 0.1
            WHEN {SeenHours} <= 24 THEN 1 WHEN {SeenHours} <= 72 THEN 0.75 WHEN {SeenHours} <= 168 THEN 0.35 ELSE 0.1 END) AS BIGINT)
        """;
    const string SeenHours = "EXTRACT(EPOCH FROM (CAST(@at AS timestamptz) - CAST(NULLIF(last_seen_at, '') AS timestamptz))) / 3600";
    /// <summary>Seen in the game within 7 days of the snapshot (last seen only, game-side touches left out).</summary>
    static string Seen7() => $"(NOT {PlayerActivity.NotALogin("last_seen_at")} AND {SeenHours} <= 168)";
    static string HeroEstRow => HeroModel.EstimateSql("power", "army_power", "0.78");
    const string ActiveNote = "seen in the game within 7 days of that snapshot (last seen only - donations and chat are not kept per snapshot)";
    /// <summary>A server total that is a sum over its players, so it can be split by alliance.</summary>
    static bool Additive(RankMetric m) => m.Entity == "servers" && (m.Expr.StartsWith("SUM(") || m.Expr.StartsWith("COUNT("));
    const string FightNote = "estimated hero power x how recently they were in the game at that snapshot (a day 1, 3 days 0.75, a week 0.35, older 0.1)";

    public static readonly IReadOnlyList<RankMetric> RankMetrics =
    [
        new("players", "power", "Total power", "profile", 0, "power"),
        new("players", "kills", "Kills", "profile", 0, "army_kill"),
        new("players", "hq", "HQ level", "profile", 0, "level", "int"),
        new("players", "hero", "Hero power", "board", 13, "value", Note: "hero power board - top 200 per server"),
        new("players", "heroest", "Hero power (estimated, everyone)", "profile", 0, HeroEstRow,
            Note: "0.78 x (power - troop power) from the profile - within about 1% of the hero board"),
        new("players", "fight", "Fighting strength", "profile", 0, Fight(), Note: FightNote, Seen: true),
        new("players", "stronghero", "Strongest hero", "board", 15, "value", Note: "top 100 per server"),
        new("players", "pve", "PvE stage", "board", 14, Pve("value"), "int", "top 100 per server"),

        new("alliances", "power", "Total power", "profile", 0, "SUM(power)", Note: "every current member"),
        new("alliances", "members", "Members", "profile", 0, "COUNT(*)", "int"),
        new("alliances", "avgpower", "Average power", "profile", 0, "CAST(AVG(power) AS BIGINT)"),
        new("alliances", "kills", "Kills", "profile", 0, "SUM(army_kill)"),
        new("alliances", "heroest", "Hero power (estimated, every member)", "profile", 0, $"SUM({HeroEstRow})",
            Note: "0.78 x (power - troop power) summed over every current member"),
        new("alliances", "fight", "Fighting strength", "profile", 0, $"SUM({Fight()})", Note: "every current member's " + FightNote, Seen: true),
        new("alliances", "active7", "Active members, 7 days", "profile", 0, $"COUNT(*) FILTER (WHERE {Seen7()})", "int", ActiveNote, Seen: true),
        new("alliances", "activehero7", "Active estimated hero power, 7 days", "profile", 0, $"SUM({HeroEstRow}) FILTER (WHERE {Seen7()})", Note: "estimated hero power of members " + ActiveNote, Seen: true),
        new("alliances", "hero", "Hero power (members on hero boards)", "board", 13, "SUM(value)",
            Note: "sum over members on their server's hero board (top 200) - smaller alliances are undercounted"),
        new("alliances", "board2", "Alliance power board", "board", 2, "value", Note: "the in-game alliance board, top 50 per server"),
        new("alliances", "board1", "Alliance kills board", "board", 1, "value", Note: "the in-game alliance board, top 50 per server"),

        new("servers", "power", "Total power", "profile", 0, "SUM(power)", Note: "every player homed there"),
        new("servers", "accounts", "Players", "profile", 0, "COUNT(*)", "int"),
        new("servers", "kills", "Kills", "profile", 0, "SUM(army_kill)"),
        new("servers", "heroest", "Estimated hero power", "profile", 0, $"SUM({HeroEstRow})", Note: "0.78 x (power - troop power) for every player homed there"),
        new("servers", "fight", "Fighting strength", "profile", 0, $"SUM({Fight()})", Note: "every player homed there: " + FightNote, Seen: true),
        new("servers", "active7", "Active players, 7 days", "profile", 0, $"COUNT(*) FILTER (WHERE {Seen7()})", "int", "players homed there " + ActiveNote, Seen: true),
        new("servers", "activehero7", "Active estimated hero power, 7 days", "profile", 0, $"SUM({HeroEstRow}) FILTER (WHERE {Seen7()})", Note: "estimated hero power of players " + ActiveNote, Seen: true),
        new("servers", "top200", "Top 200 power", "board", 4, "SUM(value)"),
        new("servers", "hero200", "Top 200 hero power", "board", 13, "SUM(value)"),
        new("servers", "kills100", "Top 100 kills", "board", 3, "SUM(value)"),
        new("servers", "stronghero", "Strongest hero", "board", 15, "MAX(value)"),
        new("servers", "pve", "Best PvE stage", "board", 14, Pve("MAX(value)"), "int"),
    ];

    /// <summary>
    /// A cross-server ranking: every alliance, player or server in the range on one measure, as of
    /// one snapshot, with its rank and value in an earlier snapshot, and the chosen entities'
    /// values over every snapshot for the chart.
    /// </summary>
    public Dictionary<string, object?> Rankings(string entity, string metric, string? at, string? vs,
        int lo, int hi, int? server, int limit, IReadOnlyList<string>? chart, int top = 0, bool others = true)
    {
        // Unknown measure: estimated hero power, the site's default.
        var m = RankMetrics.FirstOrDefault(x => x.Entity == entity && x.Key == metric)
             ?? RankMetrics.FirstOrDefault(x => x.Entity == entity && x.Key == (entity == "servers" ? "activehero7" : "heroest"))
             ?? RankMetrics.First(x => x.Entity == entity || x.Entity == "alliances");
        entity = m.Entity;
        using var c = Open();

        // Snapshots: full profile sweeps, or the board's snapshots that cover most of the range.
        var snaps = (m.Source == "profile"
            // A sweep counts only if it covers the range: 90% of the most players any sweep found homed in it.
            // Sweeps stopped part-way (a restart mid-run) and sweeps of other servers are left out - 2026-09-30,
            // hosted: three interrupted 64-server sweeps showed as sudden drops in every alliance total.
            ? Q(c, $"""
                WITH s AS (SELECT taken_at, COUNT(*) FILTER (WHERE home_server BETWEEN @lo AND @hi) AS n, COUNT(NULLIF(last_seen_at, '')) AS seen
                           FROM player_profile WHERE source = 'sweep' GROUP BY taken_at HAVING COUNT(*) > 50000)
                SELECT taken_at AS at FROM s WHERE n >= 0.9 * (SELECT MAX(n) FROM s){(m.Seen ? " AND seen > 50000" : "")} ORDER BY taken_at;
                """, ("@lo", lo), ("@hi", hi))
            : Q(c, """
                SELECT taken_at AS at FROM board_sample WHERE board_type = @b AND server_id BETWEEN @lo AND @hi
                GROUP BY taken_at HAVING COUNT(DISTINCT server_id) * 2 >= @hi - @lo + 1 ORDER BY taken_at;
                """, ("@b", m.Board), ("@lo", lo), ("@hi", hi)))
            .Select(r => (string)r["at"]!).ToList();
        var meta = new Dictionary<string, object?>
        {
            ["metrics"] = RankMetrics, ["entity"] = entity, ["metric"] = m.Key, ["label"] = m.Label,
            ["format"] = m.Format, ["note"] = m.Note, ["source"] = m.Source, ["snapshots"] = snaps,
            ["lo"] = lo, ["hi"] = hi, ["server"] = server,
            ["splittable"] = Additive(m), ["top"] = Additive(m) ? Math.Clamp(top, 0, 20) : 0, ["others"] = others || top <= 0 || !Additive(m),
        };
        if (snaps.Count == 0) return new(meta) { ["rows"] = new List<object>(), ["series"] = new List<object>() };

        at = at is { Length: > 0 } && snaps.Contains(at) ? at : snaps[^1];
        // Comparison: an exact snapshot, or a period back from "as of" (1d / 7d / 30d: the newest
        // snapshot at least that far back, else the oldest), or by default the OLDEST snapshot.
        var atTime = DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture);
        var oldest = snaps[0] != at ? snaps[0] : null;
        string? Back(int days) =>
            snaps.LastOrDefault(s => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture) <= atTime.AddDays(-days)) ?? oldest;
        var since = vs is "1d" or "7d" or "30d" or "none" ? vs : vs is { Length: > 0 } && snaps.Contains(vs) ? "exact" : "first";
        vs = vs switch
        {
            "none" => null,
            "1d" => Back(1), "7d" => Back(7), "30d" => Back(30),
            { Length: > 0 } when snaps.Contains(vs) => vs,
            _ => oldest,
        };

        top = Additive(m) ? Math.Clamp(top, 0, 20) : 0;
        others = others || top == 0;
        // "Everyone else" hidden: a server's value IS its top N alliances, so rank, movement and chart all use that.
        var src = others ? Source(m, server)
            : $"""SELECT key, '#' || key AS name, key AS server, '' AS tag, '' AS "allianceId", SUM(value) AS value FROM ({Split(m, server)}) s WHERE n <= {top} GROUP BY key""";
        (string, object)[] P(string when) => [("@at", when), ("@lo", lo), ("@hi", hi), ("@srv", server ?? 0), ("@b", m.Board)];

        var rows = Q(c, $"""
            SELECT *, ROW_NUMBER() OVER (ORDER BY value DESC, CAST(key AS TEXT)) AS rank FROM ({src}) WHERE value IS NOT NULL
            ORDER BY value DESC, CAST(key AS TEXT) LIMIT {Math.Clamp(limit, 10, 1000)};
            """, P(at));
        var keys = rows.Select(r => Convert.ToString(r["key"])!).ToList();

        if (vs is not null && keys.Count > 0)
        {
            var inList = string.Join(",", keys.Select((_, i) => $"@k{i}"));
            var prev = Q(c, $"""
                SELECT key, value, rank FROM (SELECT key, value, ROW_NUMBER() OVER (ORDER BY value DESC, CAST(key AS TEXT)) AS rank
                                              FROM ({src}) WHERE value IS NOT NULL)
                WHERE CAST(key AS TEXT) IN ({inList});
                """, [.. P(vs), .. keys.Select((k, i) => ($"@k{i}", (object)k))])
                .ToDictionary(r => Convert.ToString(r["key"])!);
            foreach (var r in rows)
                if (prev.TryGetValue(Convert.ToString(r["key"])!, out var p)) { r["prevRank"] = p["rank"]; r["prevValue"] = p["value"]; }
        }

        // Each server's top N alliances on this measure, for the stacked bars.
        if (top > 0 && keys.Count > 0)
        {
            var inList = string.Join(",", keys.Select((_, i) => $"@k{i}"));
            var parts = Q(c, $"SELECT * FROM ({Split(m, server)}) s WHERE n <= {top} AND CAST(key AS TEXT) IN ({inList}) ORDER BY key, n;",
                    [.. P(at), .. keys.Select((k, i) => ($"@k{i}", (object)k))])
                .GroupBy(r => Convert.ToString(r["key"])!)
                .ToDictionary(g => g.Key, g => g.Select(r => new Dictionary<string, object?> { ["allianceId"] = r["allianceId"], ["tag"] = r["tag"], ["value"] = r["value"] }).ToList());
            foreach (var r in rows) r["parts"] = parts.GetValueOrDefault(Convert.ToString(r["key"])!) ?? [];
        }

        // An alliance's server = where most of its members are homed (profile metrics).
        if (entity == "alliances" && m.Source == "profile" && keys.Count > 0)
        {
            var inList = string.Join(",", keys.Select((_, i) => $"@k{i}"));
            var homes = Q(c, $"""
                SELECT alliance_id AS a, home_server AS h, COUNT(*) AS n FROM player_profile
                WHERE taken_at = @at AND alliance_id IN ({inList}) GROUP BY 1, 2;
                """, [("@at", at), .. keys.Select((k, i) => ($"@k{i}", (object)k))])
                .GroupBy(r => (string)r["a"]!).ToDictionary(g => g.Key, g => g.MaxBy(r => Convert.ToInt64(r["n"]))!["h"]);
            foreach (var r in rows) r["server"] = homes.GetValueOrDefault((string)r["key"]!);
        }

        // The chart: the picked entities (default the top four, up to twelve) at every snapshot.
        var pick = (chart is { Count: > 0 } ? chart : keys.Take(4)).Take(12).ToList();
        var series = new List<Dictionary<string, object?>>();
        if (pick.Count > 0)
        {
            var inList = string.Join(",", pick.Select((_, i) => $"@k{i}"));
            var byKey = pick.ToDictionary(k => k, _ => new List<Dictionary<string, object?>>());
            var names = new Dictionary<string, object?>();
            foreach (var s in snaps)
                foreach (var r in Q(c, $"SELECT key, name, tag, value FROM ({src}) WHERE CAST(key AS TEXT) IN ({inList});",
                             [.. P(s), .. pick.Select((k, i) => ($"@k{i}", (object)k))]))
                {
                    var k = Convert.ToString(r["key"])!;
                    byKey[k].Add(new() { ["at"] = s, ["value"] = r["value"] });
                    names[k] = entity == "alliances" ? r["tag"] ?? r["name"] : r["name"];
                }
            series.AddRange(pick.Select(k => new Dictionary<string, object?>
            { ["key"] = k, ["label"] = names.GetValueOrDefault(k) ?? k, ["points"] = byKey[k] }));
        }

        return new(meta) { ["at"] = at, ["vs"] = vs, ["since"] = since, ["rows"] = rows, ["series"] = series };
    }

    /// <summary>
    /// SQL for every entity in scope at snapshot @at: key, name, server, tag, allianceId, value.
    /// Profiles scope on HOME server, boards on the server whose board it is.
    /// </summary>
    static string Scope(RankMetric m, int? server)
    {
        var one = server is null ? "" : m.Source == "profile" ? " AND home_server = @srv" : " AND server_id = @srv";
        return m.Source == "profile" ? $"home_server BETWEEN @lo AND @hi{one}" : $"server_id BETWEEN @lo AND @hi{one}";
    }

    /// <summary>A server metric per (server, alliance) at @at, ranked within the server: key, allianceId, tag, value, n.</summary>
    static string Split(RankMetric m, int? server)
    {
        var (col, from) = m.Source == "profile"
            ? ("home_server", $"player_profile WHERE taken_at = @at AND {Scope(m, server)}")
            : ("server_id", $"board_sample WHERE board_type = @b AND taken_at = @at AND {Scope(m, server)}");
        return $"""
            SELECT {col} AS key, alliance_id AS "allianceId", MAX(alliance_tag) AS tag, {m.Expr} AS value,
                   ROW_NUMBER() OVER (PARTITION BY {col} ORDER BY {m.Expr} DESC, alliance_id) AS n
            FROM {from} AND alliance_id <> '' GROUP BY {col}, alliance_id
            """;
    }

    static string Source(RankMetric m, int? server)
    {
        var scope = Scope(m, server);
        return (m.Entity, m.Source) switch
        {
            ("players", "profile") => $"""
                SELECT uid AS key, name, home_server AS server, alliance_tag AS tag, alliance_id AS "allianceId", {m.Expr} AS value
                FROM player_profile WHERE taken_at = @at AND {scope}
                """,
            ("players", _) => $"""
                SELECT uid AS key, name, server_id AS server, alliance_tag AS tag, alliance_id AS "allianceId", {m.Expr} AS value
                FROM board_sample WHERE board_type = @b AND taken_at = @at AND {scope}
                """,
            ("alliances", "profile") => $"""
                SELECT alliance_id AS key, MAX(alliance_name) AS name, NULL AS server, MAX(alliance_tag) AS tag,
                       alliance_id AS "allianceId", {m.Expr} AS value
                FROM player_profile WHERE taken_at = @at AND alliance_id <> '' AND {scope} GROUP BY alliance_id
                """,
            // Alliance boards list the alliance itself (uid = alliance id); the hero board lists players.
            ("alliances", _) when m.Board is 1 or 2 => $"""
                SELECT uid AS key, name, server_id AS server, alliance_tag AS tag, uid AS "allianceId", {m.Expr} AS value
                FROM board_sample WHERE board_type = @b AND taken_at = @at AND {scope}
                """,
            ("alliances", _) => $"""
                SELECT alliance_id AS key, MAX(alliance_tag) AS name, MIN(server_id) AS server, MAX(alliance_tag) AS tag,
                       alliance_id AS "allianceId", {m.Expr} AS value
                FROM board_sample WHERE board_type = @b AND taken_at = @at AND alliance_id <> '' AND {scope} GROUP BY alliance_id
                """,
            (_, "profile") => $"""
                SELECT home_server AS key, '#' || home_server AS name, home_server AS server, '' AS tag, '' AS "allianceId", {m.Expr} AS value
                FROM player_profile WHERE taken_at = @at AND {scope} GROUP BY home_server
                """,
            _ => $"""
                SELECT server_id AS key, '#' || server_id AS name, server_id AS server, '' AS tag, '' AS "allianceId", {m.Expr} AS value
                FROM board_sample WHERE board_type = @b AND taken_at = @at AND {scope} GROUP BY server_id
                """,
        };
    }
}
