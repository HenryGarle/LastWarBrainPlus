namespace LastWar.Storage;

public sealed partial class Insights
{
    /// <summary>
    /// Every base on one server's map from one scan (the newest by default), as compact rows for
    /// the map view, plus the alliance points (cities, alliance buildings, Marshal's Guard) the same
    /// scan saw. Tile index = x + y * 1000 + 1 on a normal server (AGENTS.md, AOI math).
    /// </summary>
    public Dictionary<string, object?> Map(int server, long? scanId)
    {
        using var c = Open();
        var scans = Q(c, "SELECT scan_id AS scan, taken_at AS at, tile_count AS tiles FROM map_scan WHERE server_id = @s ORDER BY taken_at DESC;", ("@s", server));
        if (scans.Count == 0) return new() { ["server"] = server, ["found"] = false };
        var scan = scanId is { } id && scans.Any(s => Convert.ToInt64(s["scan"]) == id) ? id : Convert.ToInt64(scans[0]["scan"]);

        var tiles = new List<object?[]>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT t.point_id, t.owner_uid, t.name, t.alliance_tag, t.alliance_id, t.hq_level, COALESCE(pl.hero, 0)
                FROM map_tile t LEFT JOIN player_latest pl ON pl.uid = t.owner_uid
                WHERE t.scan_id = @scan AND t.owner_uid <> '';
                """;
            cmd.Parameters.AddWithValue("@scan", scan);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var p = (int)r.GetInt64(0) - 1;
                tiles.Add([p % MapSize, p / MapSize, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), (int)r.GetInt64(5), r.GetInt64(6)]);
            }
        }

        // Scans before 2026-09-29 have none: map_feature started then.
        var features = new List<object?[]>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT point_id, point_type, build_id, level, alliance_id, alliance_tag, alliance_name, label, holder_uid
                FROM map_feature WHERE scan_id = @scan;
                """;
            cmd.Parameters.AddWithValue("@scan", scan);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var p = (int)r.GetInt64(0) - 1;
                features.Add([p % MapSize, p / MapSize, (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3),
                    r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8)]);
            }
        }

        // Who holds each season city and dig site, from the newest intel.sweep (city / stronghold ids =
        // the season table ids the map layout uses). Covers maps scanned before map_feature existed.
        var holders = Q(c, """
            SELECT city_id AS id, alliance_id AS "allianceId", tag, alliance_name AS "allianceName", taken_at AS at FROM city_sample
            WHERE server = @s AND taken_at = (SELECT max(taken_at) FROM city_sample WHERE server = @s) AND alliance_id <> ''
            UNION ALL
            SELECT stronghold_id, alliance_id, tag, alliance_name, taken_at FROM stronghold_sample
            WHERE server = @s AND taken_at = (SELECT max(taken_at) FROM stronghold_sample WHERE server = @s) AND alliance_id <> '';
            """, ("@s", (long)server));

        // Season 3 green (not-mud) tiles, as the stored gzip bitset in base64; none outside season 3.
        var green = Q(c, "SELECT tiles, bits FROM map_green WHERE scan_id = @scan;", ("@scan", scan)).FirstOrDefault();

        return new()
        {
            ["found"] = true, ["server"] = server, ["size"] = MapSize, ["scan"] = scan,
            ["green"] = green is null ? null : Convert.ToBase64String((byte[])green["bits"]!),
            ["greenTiles"] = green?["tiles"],
            ["holders"] = holders,
            ["at"] = scans.First(s => Convert.ToInt64(s["scan"]) == scan)["at"],
            ["scans"] = scans,
            ["columns"] = new[] { "x", "y", "uid", "name", "tag", "allianceId", "hq", "hero" },
            ["tiles"] = tiles,
            ["featureColumns"] = new[] { "x", "y", "type", "buildId", "level", "allianceId", "tag", "allianceName", "label", "holder" },
            ["features"] = features,
        };
    }

    public const int MapSize = 1000;
}