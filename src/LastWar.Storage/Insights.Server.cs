namespace LastWar.Storage;

public sealed partial class Insights
{
    /// <summary>
    /// A server as of its newest scan: its alliances ranked by power with how many of their members
    /// are provably active; its strongest players; its hero power board; where its players were
    /// created; who arrived and who left since the scan before; and its scan / snapshot history.
    /// </summary>
    public Dictionary<string, object?> Server(int server)
    {
        using var c = Open();
        var scans = Q(c, """
            SELECT scan_id AS scan, taken_at AS at, tile_count AS players
            FROM map_scan WHERE server_id = @s ORDER BY taken_at;
            """, ("@s", server));
        var snaps = FullSnapshots(c, server);
        if (scans.Count == 0 && snaps.Count == 0) return new() { ["server"] = server, ["found"] = false };

        var scan = scans.Count > 0 ? scans[^1]["scan"] : -1L;
        var prevScan = scans.Count > 1 ? scans[^2]["scan"] : -1L;
        var snap = snaps.Count > 0 ? snaps[^1]["at"] : "";
        var prevSnap = snaps.Count > 1 ? snaps[^2]["at"] : "";
        (string, object)[] ps =
        [
            ("@s", server), ("@scan", scan!), ("@prevScan", prevScan!),
            ("@snap", snap!), ("@prevSnap", prevSnap!),
        ];

        // Active = a login, donation or chat in the 7 days to NOW (PlayerActivity) - the same rule and
        // window as the overview, so the two pages agree.
        var win = PlayerActivity.WindowStart(Db.NowIso);
        var alliances = Q(c, $"""
            SELECT t.alliance_id AS "allianceId", MAX(t.alliance_tag) AS tag, COUNT(*) AS members,
                   SUM(p.power) AS power, COUNT(p.power) AS measured, SUM(pl.hero) AS hero, COUNT(pl.hero) AS "heroMeasured",
                   COUNT(*) FILTER (WHERE pa.last_active_at >= {win}) AS active,
                   COUNT(pa.uid) AS compared
            FROM map_tile t
            LEFT JOIN power_sample p ON p.server_id = @s AND p.taken_at = @snap AND p.uid = t.owner_uid
            LEFT JOIN player_latest pl ON pl.uid = t.owner_uid
            LEFT JOIN player_activity pa ON pa.uid = t.owner_uid
            WHERE t.scan_id = @scan AND t.alliance_id <> ''
            GROUP BY t.alliance_id ORDER BY hero DESC NULLS LAST, members DESC;
            """, ps);
        var solo = Q(c, $"""
            SELECT COUNT(*) AS members, SUM(p.power) AS power,
                   COUNT(*) FILTER (WHERE pa.last_active_at >= {win}) AS active,
                   COUNT(pa.uid) AS compared
            FROM map_tile t
            LEFT JOIN power_sample p ON p.server_id = @s AND p.taken_at = @snap AND p.uid = t.owner_uid
            LEFT JOIN player_activity pa ON pa.uid = t.owner_uid
            WHERE t.scan_id = @scan AND t.alliance_id = '';
            """, ps).FirstOrDefault();

        // Strongest by estimated hero power (the site's default measure), everyone on the newest map.
        var top = Q(c, $"""
            SELECT t.owner_uid AS uid, t.name, t.alliance_tag AS tag, t.alliance_id AS "allianceId", p.power,
                   q.power AS prev, pl.hero AS "heroEst", {Active7("t.owner_uid", "@snap")} AS active7, {LastRise("t.owner_uid")} AS "lastRise",
                   {F(LatestHero, "t.owner_uid")} AS "heroPower", {Origin("t.owner_uid")} AS origin
            FROM map_tile t
            LEFT JOIN player_latest pl ON pl.uid = t.owner_uid
            LEFT JOIN power_sample p ON p.server_id = @s AND p.taken_at = @snap AND p.uid = t.owner_uid
            LEFT JOIN power_sample q ON q.server_id = @s AND q.taken_at = @prevSnap AND q.uid = t.owner_uid
            WHERE t.scan_id = @scan
            ORDER BY pl.hero DESC NULLS LAST LIMIT 30;
            """, ps);
        var heroBoard = Q(c, $"""
            SELECT b.rank, b.uid, b.value AS "heroPower",
                   (SELECT t.name FROM map_tile t WHERE t.scan_id = @scan AND t.owner_uid = b.uid LIMIT 1) AS name,
                   (SELECT t.alliance_tag FROM map_tile t WHERE t.scan_id = @scan AND t.owner_uid = b.uid LIMIT 1) AS tag,
                   (SELECT t.alliance_id FROM map_tile t WHERE t.scan_id = @scan AND t.owner_uid = b.uid LIMIT 1) AS "allianceId"
            FROM board_sample b
            WHERE b.server_id = @s AND b.board_type = 13
              AND b.taken_at = (SELECT MAX(taken_at) FROM board_sample WHERE server_id = @s AND board_type = 13)
            ORDER BY b.rank LIMIT 30;
            """, ps);
        var origins = Q(c, $"""
            SELECT {Origin("owner_uid")} AS origin, COUNT(*) AS players
            FROM map_tile WHERE scan_id = @scan GROUP BY origin ORDER BY players DESC;
            """, ps);

        // Movement between this server's last two scans. For each arrival, the newest sighting
        // before this scan says where it came from; for each departure, the newest sighting of
        // all says where it went.
        var arrivals = prevScan is -1L ? [] : Q(c, """
            SELECT t.owner_uid AS uid, t.name, t.alliance_tag AS tag,
                   (SELECT o.server_id FROM map_tile o JOIN map_scan os ON os.scan_id = o.scan_id
                    WHERE o.owner_uid = t.owner_uid AND o.scan_id <> @scan
                    ORDER BY os.taken_at DESC LIMIT 1) AS "fromServer"
            FROM map_tile t WHERE t.scan_id = @scan
              AND t.owner_uid NOT IN (SELECT owner_uid FROM map_tile WHERE scan_id = @prevScan);
            """, ps);
        var departures = prevScan is -1L ? [] : Q(c, """
            SELECT o.owner_uid AS uid, o.name, o.alliance_tag AS tag,
                   (SELECT n.server_id FROM map_tile n JOIN map_scan ns ON ns.scan_id = n.scan_id
                    WHERE n.owner_uid = o.owner_uid ORDER BY ns.taken_at DESC LIMIT 1) AS "nowServer"
            FROM map_tile o WHERE o.scan_id = @prevScan
              AND o.owner_uid NOT IN (SELECT owner_uid FROM map_tile WHERE scan_id = @scan);
            """, ps);
        var applicants = Q(c, """
            SELECT uid, name, from_server AS "fromServer", abbr AS tag, power, hero_power AS "heroPower",
                   thp_rank AS "thpRank", captured_at AS at
            FROM migration_applicant
            WHERE to_server = @s AND captured_at = (SELECT MAX(captured_at) FROM migration_applicant WHERE to_server = @s)
            ORDER BY power DESC;
            """, ps);

        // The headline counts everyone FROM this server (home server on their newest profile), like the overview.
        var home = Q(c, $"""
            SELECT COUNT(*) AS population, SUM(pl.hero) AS hero, SUM(pl.power) AS power,
                   COUNT(*) FILTER (WHERE pa.last_active_at >= {Db.AgoIso("1 day")}) AS active1d,
                   COUNT(*) FILTER (WHERE pa.last_active_at >= {Db.AgoIso("7 days")}) AS active
            FROM player_latest pl LEFT JOIN player_activity pa ON pa.uid = pl.uid WHERE pl.server = @s;
            """, ("@s", server))[0];
        var compared = Convert.ToInt64(home["population"]);
        var active = Convert.ToInt64(home["active"]);
        return new()
        {
            ["found"] = true,
            ["server"] = server,
            ["scannedAt"] = scans.Count > 0 ? scans[^1]["at"] : null,
            ["players"] = scans.Count > 0 ? scans[^1]["players"] : null,
            ["snapshotAt"] = snap,
            ["previousSnapshotAt"] = prevSnap,
            ["windowDays"] = PlayerActivity.WindowDays,
            ["totalPower"] = home["power"] ?? (snaps.Count > 0 ? snaps[^1]["power"] : null),
            ["heroTotal"] = home["hero"],
            ["compared"] = compared,
            ["active"] = active,
            ["active1d"] = home["active1d"],
            ["alliances"] = alliances,
            ["unaffiliated"] = solo,
            ["top"] = top,
            ["heroBoard"] = heroBoard,
            ["origins"] = origins,
            ["arrivals"] = arrivals,
            ["departures"] = departures,
            ["applicants"] = applicants,
            ["scans"] = scans,
            ["snapshots"] = snaps,
        };
    }

    /// <summary>
    /// A server's power snapshots that cover the WHOLE server: at least 80% of the accounts of its
    /// biggest snapshot. Alliance rosters (members only) and old partial reads are power readings,
    /// but reading one as "the server" made a server look like its ~1,500 alliance members.
    /// </summary>
    List<Dictionary<string, object?>> FullSnapshots(Npgsql.NpgsqlConnection c, int server) => Q(c, """
        WITH s AS (SELECT taken_at AS at, COUNT(*) AS accounts, SUM(power) AS power FROM power_sample WHERE server_id = @s GROUP BY taken_at)
        SELECT * FROM s WHERE accounts >= 0.8 * (SELECT MAX(accounts) FROM s) ORDER BY at;
        """, ("@s", server));

    /// <summary>One row per server: size, power, and how much of it is provably being played.</summary>
    public List<Dictionary<string, object?>> Overview()
    {
        using var c = Open();
        // Every player's NEWEST profile (full sweeps plus the hourly pulse's changes), by the server they
        // are from and their alliance - one pass, summed per server below. Measures:
        //   power; heroEst = k x (power - troop power) for everyone (HeroModel, k fitted per server);
        //   heroBoard = hero power on each server's newest top-200 hero board (so the tail is missing);
        //   active7 / active1d = logged in, donated or chatted (PlayerActivity); activeHero7 = heroEst of the active7.
        // player_latest holds every player's newest profile with its estimated hero power (PlayerLatest).
        var since7 = Db.AgoIso("7 days");
        var groups = Q(c, $"""
            WITH bm AS (SELECT server_id, MAX(taken_at) AS t FROM board_sample WHERE board_type = 13 GROUP BY server_id),
            board AS (
                SELECT DISTINCT ON (b.uid) b.uid, b.value FROM board_sample b
                JOIN bm ON bm.server_id = b.server_id AND bm.t = b.taken_at
                WHERE b.board_type = 13 ORDER BY b.uid, b.value DESC
            )
            SELECT p.server, p.alliance_id AS "allianceId", MAX(p.tag) AS tag, COUNT(*) AS n,
                   SUM(p.power) AS power, MAX(p.power) AS top, MAX(p.profiled_at) AS at,
                   COALESCE(SUM(p.hero), 0) AS "heroEst", MAX(p.hero) AS "topHero",
                   COALESCE(SUM(bd.value), 0) AS "heroBoard", COUNT(bd.uid) AS "onBoard",
                   COUNT(*) FILTER (WHERE pa.last_active_at >= {since7}) AS active7,
                   COUNT(*) FILTER (WHERE pa.last_active_at >= {Db.AgoIso("1 day")}) AS active1d,
                   COALESCE(SUM(p.hero) FILTER (WHERE pa.last_active_at >= {since7}), 0) AS "activeHero7",
                   -- The Matchup's fighting strength: hero x recency (a day 1, 3 days 0.75, a week 0.35, older 0.1).
                   SUM(p.hero * CASE WHEN pa.last_active_at >= {Db.AgoIso("1 day")} THEN 1 WHEN pa.last_active_at >= {Db.AgoIso("3 days")} THEN 0.75
                                     WHEN pa.last_active_at >= {since7} THEN 0.35 ELSE 0.1 END)::bigint AS fight
            FROM player_latest p
            LEFT JOIN board bd ON bd.uid = p.uid
            LEFT JOIN player_activity pa ON pa.uid = p.uid
            WHERE p.profiled_at IS NOT NULL
            GROUP BY p.server, p.alliance_id;
            """).GroupBy(g => L(g["server"])).ToDictionary(g => g.Key, g => g.ToList());
        string[] measures = ["n", "power", "heroEst", "heroBoard", "onBoard", "active7", "active1d", "activeHero7", "fight"];
        var rows = Q(c, $"""
            -- Everyone FROM the server (newest profile's home server) and how many logged in, donated or
            -- chatted in the last day / 7 days (PlayerActivity). "compared" is the population.
            WITH activity AS (
                SELECT pl.server AS server_id, COUNT(*) AS compared,
                       COUNT(*) FILTER (WHERE pa.last_active_at >= {Db.AgoIso("1 day")}) AS active1d,
                       COUNT(*) FILTER (WHERE pa.last_active_at >= {Db.AgoIso("7 days")}) AS active
                FROM player_latest pl LEFT JOIN player_activity pa ON pa.uid = pl.uid
                GROUP BY pl.server
            ),
            maps AS (
                SELECT m.server_id, m.tile_count AS players, m.taken_at AS scanned, m.scan_id,
                       (SELECT COUNT(DISTINCT alliance_id) FROM map_tile t
                        WHERE t.scan_id = m.scan_id AND t.alliance_id <> '') AS alliances
                FROM map_scan m
                WHERE m.taken_at = (SELECT MAX(taken_at) FROM map_scan x WHERE x.server_id = m.server_id)
            ),
            snapcount AS (SELECT server_id, COUNT(*) AS snapshots FROM (SELECT DISTINCT server_id, taken_at FROM power_sample) GROUP BY server_id)
            SELECT m.server_id AS server, m.players, m.alliances, m.scanned,
                   COALESCE(sc.snapshots, 0) AS snapshots,
                   a.compared, a.active, a.active1d
            FROM maps m
            LEFT JOIN activity a ON a.server_id = m.server_id
            LEFT JOIN snapcount sc ON sc.server_id = m.server_id
            ORDER BY m.server_id;
            """);
        foreach (var r in rows)
        {
            if (!groups.TryGetValue(L(r["server"]), out var gs)) continue;
            r["accounts"] = gs.Sum(g => L(g["n"]));
            r["power"] = gs.Sum(g => L(g["power"]));
            r["top"] = gs.Max(g => L(g["top"]));
            r["topHero"] = gs.Max(g => L(g["topHero"]));
            r["snapshotAt"] = gs.Max(g => g["at"] as string);
            r["totals"] = measures.ToDictionary(m => m, m => (object?)gs.Sum(g => L(g[m])));
            // The strongest alliances by the power of their members FROM this server, every measure each;
            // the page picks the top N by whichever measure it shows and calls the rest "other".
            // ponytail: top 20 by power - an alliance outside that but top-15 by another measure is missed.
            r["topAlliances"] = gs.Where(g => g["allianceId"] is string a && a.Length > 0)
                .OrderByDescending(g => L(g["power"])).Take(20)
                .Select(g =>
                {
                    var d = measures.ToDictionary(m => m, m => (object?)L(g[m]));
                    d["allianceId"] = g["allianceId"]; d["tag"] = g["tag"];
                    return d;
                })
                .ToList();
        }
        return rows;
    }
}
