namespace LastWar.Storage;

public sealed partial class Insights
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, object Value)> HiveCache = new();

    /// <summary>
    /// Every alliance's hive on every server's newest map (Hive.Rate), computed on request and cached
    /// ten minutes: 32 maps take well under a second, so nothing is stored.
    /// </summary>
    public Dictionary<string, object?> Hives()
    {
        if (HiveCache.TryGetValue(path, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10))
            return (Dictionary<string, object?>)hit.Value;
        using var c = Open();
        var bases = new Dictionary<(long Server, string Alliance), (string Tag, List<Hive.Base> List)>();
        var hero = new Dictionary<(long, string), long>();   // estimated hero power of the alliance's bases, for "top N per server"
        var scans = new Dictionary<long, string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                WITH latest AS (SELECT DISTINCT ON (server_id) server_id, scan_id, taken_at FROM map_scan ORDER BY server_id, taken_at DESC)
                SELECT l.server_id, l.taken_at, t.alliance_id, t.alliance_tag, t.point_id, COALESCE(pl.hero, 0)
                FROM latest l JOIN map_tile t ON t.scan_id = l.scan_id LEFT JOIN player_latest pl ON pl.uid = t.owner_uid
                WHERE t.owner_uid <> '' AND t.alliance_id <> '';
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = (r.GetInt64(0), r.GetString(2));
                scans[key.Item1] = r.GetString(1);
                if (!bases.TryGetValue(key, out var v)) bases[key] = v = (r.GetString(3), []);
                var p = (int)r.GetInt64(4) - 1;
                v.List.Add(new Hive.Base(p % MapSize, p / MapSize));
                hero[key] = hero.GetValueOrDefault(key) + r.GetInt64(5);
            }
        }
        var rows = new List<Dictionary<string, object?>>();
        foreach (var ((server, alliance), (tag, list)) in bases)
        {
            if (list.Count < 5) continue;
            var hive = Hive.HiveOf(list);
            if (Hive.Rate(hive) is not { } s) continue;
            var cx = (int)Math.Round(hive.Average(b => b.X)); var cy = (int)Math.Round(hive.Average(b => b.Y));
            rows.Add(new()
            {
                ["server"] = server, ["allianceId"] = alliance, ["tag"] = tag, ["bases"] = list.Count, ["hive"] = s.N,
                ["hero"] = hero.GetValueOrDefault((server, alliance)),
                ["score"] = s.Value, ["spacing"] = Math.Round(s.Spacing, 3), ["alignment"] = Math.Round(s.Alignment, 3),
                ["filled"] = Math.Round(s.Filled, 3), ["holes"] = s.Holes, ["gap"] = s.Gap, ["pitch"] = s.Pitch, ["x"] = cx, ["y"] = cy,
            });
        }
        var value = new Dictionary<string, object?>
        {
            ["rows"] = rows.OrderByDescending(r => (int)r["score"]!).ToList(),
            ["mapped"] = scans.ToDictionary(k => k.Key.ToString(), k => k.Value),
        };
        HiveCache[path] = (DateTime.UtcNow, value);
        return value;
    }

    /// <summary>Who holds every season city and dig site on every server, from each server's newest intel.sweep.</summary>
    public List<Dictionary<string, object?>> Holders()
    {
        using var c = Open();
        return Q(c, """
        SELECT server, city_id AS id, 'city' AS kind, alliance_id AS "allianceId", tag, alliance_name AS "allianceName", taken_at AS at
        FROM city_sample s WHERE taken_at = (SELECT max(taken_at) FROM city_sample x WHERE x.server = s.server) AND alliance_id <> ''
        UNION ALL
        SELECT server, stronghold_id, 'stronghold', alliance_id, tag, alliance_name, taken_at
        FROM stronghold_sample s WHERE taken_at = (SELECT max(taken_at) FROM stronghold_sample x WHERE x.server = s.server) AND alliance_id <> ''
        ORDER BY 1, 2;
        """);
    }
}
