using System.Collections.Concurrent;

namespace LastWar.Storage;

public sealed partial class Insights
{
    static readonly ConcurrentDictionary<(string, int, int), (DateTime At, object Value)> NewAccountCache = new();

    /// <summary>
    /// Accounts made on a server: a base first seen by a map scan AFTER our first scan of that server,
    /// whose uid says it was made there (the last six digits are the server it was created on - a
    /// migrant's say somewhere else), and small when first seen (HQ &lt;= FreshHq). A new account
    /// places its base at once, so the map is where it shows up first. Caveat: a small long-idle
    /// account whose base had left the map and came back looks the same.
    /// Same rows for the server list and the report, so both agree.
    /// </summary>
    public const int FreshHq = 15;

    static string NewAccountsSql(string where) => $"""
        WITH cov AS (SELECT server_id, min(taken_at) AS since FROM map_scan GROUP BY server_id),
        first AS (
            SELECT DISTINCT ON (t.owner_uid) t.owner_uid AS uid, s.taken_at AS first_at, t.server_id, t.hq_level AS first_hq,
                   t.name, t.alliance_tag AS tag, t.alliance_id
            FROM map_tile t JOIN map_scan s ON s.scan_id = t.scan_id
            WHERE t.owner_uid <> '' ORDER BY t.owner_uid, s.taken_at)
        SELECT f.* FROM first f JOIN cov ON cov.server_id = f.server_id
        WHERE f.first_at > cov.since AND f.first_hq <= {FreshHq} AND {Origin("f.uid")} = f.server_id {where}
        """;

    /// <summary>One server's new accounts, newest first, with how they have grown since.</summary>
    public Dictionary<string, object?> NewAccounts(int server, int days)
    {
        days = Math.Clamp(days, 1, 3650);
        if (NewAccountCache.TryGetValue((path, server, days), out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10))
            return (Dictionary<string, object?>)hit.Value;
        using var c = Open();
        var cut = DateTimeOffset.UtcNow.AddDays(-days).ToString("O");
        var rows = Q(c, $"""
            WITH n AS ({NewAccountsSql("AND f.server_id = @s AND f.first_at >= @cut")})
            SELECT n.uid, n.first_at AS "firstAt", n.first_hq AS "firstHq", n.name AS "firstName",
                   COALESCE(pl.name, n.name) AS name, COALESCE(pl.tag, n.tag) AS tag, COALESCE(pl.alliance_id, n.alliance_id) AS "allianceId",
                   pl.server, pl.power, pl.hero,
                   (SELECT t.hq_level FROM map_tile t WHERE t.owner_uid = n.uid ORDER BY t.scan_id DESC LIMIT 1) AS hq
            FROM n LEFT JOIN player_latest pl ON pl.uid = n.uid
            ORDER BY n.first_at DESC, n.uid LIMIT 1000;
            """, ("@s", (long)server), ("@cut", cut));
        var since = Q(c, "SELECT min(taken_at) AS since, count(*) AS scans FROM map_scan WHERE server_id = @s;", ("@s", (long)server)).FirstOrDefault();
        var value = new Dictionary<string, object?>
        {
            ["server"] = server, ["days"] = days, ["freshHq"] = FreshHq,
            ["watchedSince"] = since?["since"], ["scans"] = since?["scans"], ["accounts"] = rows,
        };
        NewAccountCache[(path, server, days)] = (DateTime.UtcNow, value);
        return value;
    }

    /// <summary>New accounts per server per day, with how many map scans each day had (no scan = unknown, not 0).</summary>
    public Dictionary<string, object?> NewAccountReport(int days)
    {
        days = Math.Clamp(days, 1, 3650);
        if (NewAccountCache.TryGetValue((path, -1, days), out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10))
            return (Dictionary<string, object?>)hit.Value;
        using var c = Open();
        var cut = DateTimeOffset.UtcNow.AddDays(-days).ToString("O");
        var counts = Q(c, $"""
            WITH n AS ({NewAccountsSql("AND f.first_at >= @cut")})
            SELECT server_id AS server, left(first_at, 10) AS day, count(*) AS n FROM n GROUP BY 1, 2 ORDER BY 1, 2;
            """, ("@cut", cut));
        var scans = Q(c, """
            SELECT server_id AS server, left(taken_at, 10) AS day, count(*) AS n, min(taken_at) = (SELECT min(taken_at) FROM map_scan m WHERE m.server_id = s.server_id) AS first
            FROM map_scan s WHERE taken_at >= @cut GROUP BY 1, 2 ORDER BY 1, 2;
            """, ("@cut", cut));
        var value = new Dictionary<string, object?> { ["days"] = days, ["freshHq"] = FreshHq, ["counts"] = counts, ["scans"] = scans };
        NewAccountCache[(path, -1, days)] = (DateTime.UtcNow, value);
        return value;
    }
}