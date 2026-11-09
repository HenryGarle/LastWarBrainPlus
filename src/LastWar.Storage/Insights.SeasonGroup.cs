namespace LastWar.Storage;

public sealed partial class Insights
{
    /// <summary>
    /// One season group (lw_season config): its servers (game reading or entered by hand), the two seeded servers
    /// decoded from the game table, each server's strength (Overview's measures plus depth counts), every alliance
    /// on those servers ranked by fighting strength (the season-ranking guess), and the strongest players.
    /// </summary>
    public Dictionary<string, object?> SeasonGroup(long cfg)
    {
        new ServerRegistry(path).EnsureSchema();
        using var c = Open();
        var season = Q(c, """
            SELECT id, season, pre_start AS "preStart", start_time AS start, end_time AS "end", seeds FROM game_season WHERE id = @c;
            """, ("@c", cfg)).FirstOrDefault();
        var members = Q(c, """
            SELECT server, MIN(by) AS by FROM (
              SELECT DISTINCT ON (server_id) server_id AS server, cfg_id, 'game' AS by FROM server_season_cfg ORDER BY server_id, last_seen DESC
            ) g WHERE g.cfg_id = @c GROUP BY server
            UNION
            SELECT server_id, 'hand' FROM server_season_hand h WHERE cfg_id = @c
              AND NOT EXISTS (SELECT 1 FROM server_season_cfg s WHERE s.server_id = h.server_id AND s.cfg_id = h.cfg_id)
            ORDER BY server;
            """, ("@c", cfg));
        var ids = members.Select(m => L(m["server"])).Distinct().ToArray();
        var res = new Dictionary<string, object?> { ["cfg"] = cfg, ["season"] = season, ["members"] = members };
        if (ids.Length == 0) { res["servers"] = new List<object>(); res["alliances"] = new List<object>(); res["players"] = new List<object>(); return res; }

        // Recency weight = the Matchup's fighting strength (a day 1, 3 days 0.75, a week 0.35, older 0.1).
        string W = $"""
            CASE WHEN pa.last_active_at >= {Db.AgoIso("1 day")} THEN 1 WHEN pa.last_active_at >= {Db.AgoIso("3 days")} THEN 0.75
                 WHEN pa.last_active_at >= {Db.AgoIso("7 days")} THEN 0.35 ELSE 0.1 END
            """;
        var since7 = Db.AgoIso("7 days");
        var p = $"""
            p AS (SELECT pl.uid, pl.name, pl.server, pl.alliance_id, pl.tag, COALESCE(pl.hero, 0) AS hero, COALESCE(pl.power, 0) AS power,
                         COALESCE(pl.level, 0) AS level, pa.last_active_at, (pa.last_active_at >= {since7}) AS active7, {W} AS w
                  FROM player_latest pl LEFT JOIN player_activity pa ON pa.uid = pl.uid
                  WHERE pl.server = ANY(@ids) AND pl.profiled_at IS NOT NULL)
            """;
        var ps = ("@ids", (object)ids);

        // Depth: how many ACTIVE (7 d) players each server has above hero-power bars, and active HQ 30+.
        var depth = Q(c, $"""
            WITH {p}
            SELECT server,
                   COUNT(*) FILTER (WHERE active7 AND hero >= 100000000) AS "hero100",
                   COUNT(*) FILTER (WHERE active7 AND hero >= 80000000) AS "hero80",
                   COUNT(*) FILTER (WHERE active7 AND hero >= 60000000) AS "hero60",
                   COUNT(*) FILTER (WHERE active7 AND level >= 30) AS "hq30",
                   COALESCE(SUM(t.hero) FILTER (WHERE t.rn <= 100), 0) AS "top100Hero",
                   COALESCE(SUM(t.hero) FILTER (WHERE t.rn <= 20), 0) AS "top20Hero"
            FROM (SELECT p.*, ROW_NUMBER() OVER (PARTITION BY server ORDER BY hero DESC) AS rn FROM p) t
            GROUP BY server;
            """, ps).ToDictionary(r => L(r["server"]));
        var servers = Overview().Where(r => ids.Contains(L(r["server"]))).ToList();
        foreach (var r in servers)
        {
            if (depth.TryGetValue(L(r["server"]), out var d))
                foreach (var k in new[] { "hero100", "hero80", "hero60", "hq30", "top100Hero", "top20Hero" }) r[k] = d[k];
            r.Remove("topAlliances");
        }
        res["servers"] = servers;

        // Every alliance with members from these servers. core30 = fighting strength of its 30 strongest (rallies
        // and garrisons); fight = the whole alliance. The page's season-ranking guess sorts on fight.
        res["alliances"] = Q(c, $"""
            WITH {p},
            r AS (SELECT p.*, ROW_NUMBER() OVER (PARTITION BY alliance_id ORDER BY hero * w DESC) AS rn FROM p WHERE alliance_id <> '')
            SELECT alliance_id AS "allianceId", MAX(tag) AS tag, mode() WITHIN GROUP (ORDER BY server) AS server,
                   COUNT(*) AS members, COUNT(*) FILTER (WHERE active7) AS active7,
                   SUM(power) AS power, SUM(hero) AS hero, MAX(hero) AS "topHero",
                   SUM(hero * w)::bigint AS fight, (SUM(hero * w) FILTER (WHERE rn <= 30))::bigint AS core30,
                   COUNT(*) FILTER (WHERE active7 AND hero >= 80000000) AS "hero80"
            FROM r GROUP BY alliance_id
            HAVING COUNT(*) >= 5
            ORDER BY fight DESC LIMIT 60;
            """, ps);

        res["players"] = Q(c, $"""
            WITH {p}
            SELECT uid, name, server, alliance_id AS "allianceId", tag, hero, power, level, last_active_at AS "lastActive", active7
            FROM p ORDER BY hero DESC LIMIT 50;
            """, ps);
        return res;
    }
}
