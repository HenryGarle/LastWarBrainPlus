namespace LastWar.Storage;

public sealed partial class Insights
{
    /// <summary>
    /// One season group (lw_season config): its servers (game reading or entered by hand), the two seeded servers
    /// decoded from the game table, each server's strength (Overview's measures plus depth counts), every alliance
    /// on those servers ranked by fighting strength (the season-ranking guess), and the strongest players.
    /// </summary>
    /// <summary>Season battles (Henry 2026-11-09): Wednesday and Saturday, 12:00 server time (UTC).</summary>
    static readonly DayOfWeek[] EventDays = [DayOfWeek.Wednesday, DayOfWeek.Saturday];
    const int EventHour = 12;
    // ponytail: 4 weeks = 8 events; longer once the season has run a while.
    const int EventLookbackDays = 28;

    public Dictionary<string, object?> SeasonGroup(long cfg, int topAlliances = 0)
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

        // Event presence: the share of past season-event days (Wednesday and Saturday, 12:00 server time = UTC) in
        // the last EventLookbackDays on which the player showed a sign of play in the event hour (a real login,
        // a donation or a chat message); a sign only in the hour either side counts half. Held in a temp table
        // for this connection so the three queries below share it.
        var now = DateTime.UtcNow;
        var evDays = Enumerable.Range(0, EventLookbackDays).Select(i => now.Date.AddDays(-i))
            .Count(d => EventDays.Contains(d.DayOfWeek) && d.AddHours(EventHour + 1) <= now);
        // Readings per day in the window: the hourly pulse gives ~24; a copy without it (local) cannot see time of day.
        var readings = Q(c, "SELECT COUNT(*) AS n FROM profile_reading WHERE taken_at >= @s;",
            ("@s", new DateTimeOffset(now.Date.AddDays(1 - EventLookbackDays), TimeSpan.Zero).ToString("O")))[0]["n"];
        res["event"] = new Dictionary<string, object?> { ["days"] = "Wednesday, Saturday", ["hour"] = EventHour, ["lookbackDays"] = EventLookbackDays,
            ["eventsSeen"] = evDays, ["readingsPerDay"] = Math.Round(L(readings) / (double)EventLookbackDays, 1) };
        using (var cmd = Db.Command(c, $"""
            DROP TABLE IF EXISTS sg_ev;
            CREATE TEMP TABLE sg_ev AS
            WITH m AS (
                SELECT s.uid, (s.seen_at::timestamptz AT TIME ZONE 'UTC') AS at FROM player_seen s
                  JOIN player_latest pl ON pl.uid = s.uid AND pl.server = ANY(@ids)
                  WHERE s.seen_at >= @since AND NOT {PlayerActivity.NotALogin("s.seen_at")}
                UNION ALL
                SELECT a.uid, (a.donated_at::timestamptz AT TIME ZONE 'UTC') FROM alliance_member_sample a
                  JOIN player_latest pl ON pl.uid = a.uid AND pl.server = ANY(@ids) WHERE a.donated_at >= @since
                UNION ALL
                SELECT ch.sender_uid, (ch.sent_at::timestamptz AT TIME ZONE 'UTC') FROM chat_message ch
                  JOIN player_latest pl ON pl.uid = ch.sender_uid AND pl.server = ANY(@ids) WHERE ch.sent_at >= @since),
            d AS (SELECT uid, at::date AS day, MAX(CASE WHEN EXTRACT(hour FROM at) = @h THEN 1.0 ELSE 0.5 END) AS score FROM m
                  WHERE EXTRACT(dow FROM at) IN (3, 6) AND EXTRACT(hour FROM at) BETWEEN @h - 1 AND @h + 1
                    AND at::date + (@h + 1) * interval '1 hour' <= (now() AT TIME ZONE 'UTC')
                  GROUP BY uid, at::date)
            SELECT uid, LEAST(1.0, SUM(score) / GREATEST(@n, 1)) AS p FROM d GROUP BY uid;
            """, ("@ids", ids), ("@since", new DateTimeOffset(now.Date.AddDays(1 - EventLookbackDays), TimeSpan.Zero).ToString("O")),
            ("@h", (double)EventHour), ("@n", (double)evDays))) cmd.ExecuteNonQuery();
        var p = $"""
            p AS (SELECT pl.uid, pl.name, pl.server, pl.alliance_id, pl.tag, COALESCE(pl.hero, 0) AS hero, COALESCE(pl.power, 0) AS power,
                         COALESCE(pl.level, 0) AS level, pa.last_active_at, (pa.last_active_at >= {since7}) AS active7, {W} AS w,
                         COALESCE(ev.p, 0) AS ev
                  FROM player_latest pl LEFT JOIN player_activity pa ON pa.uid = pl.uid LEFT JOIN sg_ev ev ON ev.uid = pl.uid
                  WHERE pl.server = ANY(@ids) AND pl.profiled_at IS NOT NULL)
            """;
        var ps = ("@ids", (object)ids);

        // Server measures. topAlliances > 0 counts only players in each server's N strongest alliances (by fighting
        // strength of their members from that server); 0 = everyone from the server. heroBoard = the newest in-game
        // top-200 hero board value of the counted players (exact, where they are on it).
        res["topAlliances"] = topAlliances;
        res["servers"] = Q(c, $"""
            WITH {p},
            bm AS (SELECT server_id, MAX(taken_at) AS t FROM board_sample WHERE board_type = 13 AND server_id = ANY(@ids) GROUP BY server_id),
            board AS (SELECT DISTINCT ON (b.uid) b.uid, b.value FROM board_sample b JOIN bm ON bm.server_id = b.server_id AND bm.t = b.taken_at
                      WHERE b.board_type = 13 ORDER BY b.uid, b.value DESC),
            al AS (SELECT server, alliance_id, MAX(tag) AS tag, SUM(hero * w) AS f,
                          ROW_NUMBER() OVER (PARTITION BY server ORDER BY SUM(hero * w) DESC) AS rk
                   FROM p WHERE alliance_id <> '' GROUP BY server, alliance_id),
            f AS (SELECT p.* FROM p WHERE @n = 0 OR EXISTS (SELECT 1 FROM al WHERE al.server = p.server AND al.alliance_id = p.alliance_id AND al.rk <= @n)),
            t AS (SELECT f.*, ROW_NUMBER() OVER (PARTITION BY server ORDER BY hero DESC) AS rn FROM f)
            SELECT t.server, COUNT(*) AS players, SUM(power) AS power, MAX(hero) AS "topHero",
                   SUM(hero * w)::bigint AS fight,
                   SUM(hero * ev)::bigint AS "eventFight",
                   COUNT(*) FILTER (WHERE ev >= 0.5 AND hero >= 80000000) AS "eventRegulars80",
                   COALESCE(SUM(hero) FILTER (WHERE active7), 0) AS "activeHero7",
                   COALESCE(SUM(bd.value), 0) AS "heroBoard",
                   COALESCE(SUM(hero) FILTER (WHERE rn <= 100), 0) AS "top100Hero",
                   COALESCE(SUM(hero) FILTER (WHERE rn <= 20), 0) AS "top20Hero",
                   COUNT(*) FILTER (WHERE active7 AND hero >= 100000000) AS "hero100",
                   COUNT(*) FILTER (WHERE active7 AND hero >= 80000000) AS "hero80",
                   COUNT(*) FILTER (WHERE active7 AND hero >= 60000000) AS "hero60",
                   COUNT(*) FILTER (WHERE active7 AND level >= 30) AS "hq30",
                   COUNT(*) FILTER (WHERE active7) AS active,
                   (SELECT string_agg(al.tag, ', ' ORDER BY al.rk) FROM al WHERE al.server = t.server AND al.rk <= @n) AS counted
            FROM t LEFT JOIN board bd ON bd.uid = t.uid
            GROUP BY t.server ORDER BY t.server;
            """, ps, ("@n", (object)(long)Math.Max(0, topAlliances)));

        // Every alliance with members from these servers. core30 = fighting strength of its 30 strongest (rallies
        // and garrisons); fight = the whole alliance. The page's season-ranking guess sorts on fight.
        res["alliances"] = Q(c, $"""
            WITH {p},
            r AS (SELECT p.*, ROW_NUMBER() OVER (PARTITION BY alliance_id ORDER BY hero * w DESC) AS rn,
                         ROW_NUMBER() OVER (PARTITION BY alliance_id ORDER BY hero * ev DESC) AS ern FROM p WHERE alliance_id <> '')
            SELECT alliance_id AS "allianceId", MAX(tag) AS tag, mode() WITHIN GROUP (ORDER BY server) AS server,
                   COUNT(*) AS members, COUNT(*) FILTER (WHERE active7) AS active7,
                   SUM(power) AS power, SUM(hero) AS hero, MAX(hero) AS "topHero",
                   SUM(hero * w)::bigint AS fight, (SUM(hero * w) FILTER (WHERE rn <= 30))::bigint AS core30,
                   COUNT(*) FILTER (WHERE active7 AND hero >= 80000000) AS "hero80",
                   SUM(hero * ev)::bigint AS "eventFight", COALESCE((SUM(hero * ev) FILTER (WHERE ern <= 30))::bigint, 0) AS "eventCore30",
                   COUNT(*) FILTER (WHERE ev >= 0.5 AND hero >= 80000000) AS "eventRegulars80"
            FROM r GROUP BY alliance_id
            HAVING COUNT(*) >= 5
            ORDER BY fight DESC LIMIT 60;
            """, ps);

        res["players"] = Q(c, $"""
            WITH {p}
            SELECT uid, name, server, alliance_id AS "allianceId", tag, hero, power, level, last_active_at AS "lastActive", active7, ev
            FROM p ORDER BY hero DESC LIMIT 50;
            """, ps);
        return res;
    }
}
