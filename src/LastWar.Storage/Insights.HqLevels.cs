using System.Globalization;

namespace LastWar.Storage;

public sealed partial class Insights
{
    /// <summary>
    /// HQ levels across the tracked servers (server_track 'full'): how many players each server has at each level (newest
    /// full profile sweep, by home server), and everyone at <paramref name="minLevel"/> or above with when they reached each
    /// level from minLevel up.
    ///
    /// When: the HQ board (board_type 5, "first to max") carries updateTime = the moment the upgrade finished - checked
    /// 2026-10-20 on Ghost: 02:00:26 on 12 Oct, between his last hourly reading at HQ 30 (01:25) and first at 31 (02:23).
    /// Every board row ever stored counts, so a board read at HQ 31 still dates the 31 after they reach 32. Off the board (top
    /// 100 per server), the date is bracketed by our readings - login moments (player_seen, ~hourly) and profile sweeps:
    /// after the last reading below the level, by the first at or above it.
    /// </summary>
    public Dictionary<string, object?> HqLevels(int minLevel = 31)
    {
        minLevel = Math.Clamp(minLevel, 31, 40);   // HQ 30 is most of every server: the list would be tens of thousands
        using var c = Open();
        if (Q(c, "SELECT max(taken_at) AS t FROM player_profile WHERE source = 'sweep';")[0]["t"] is not string sweep)
            return new() { ["sweep"] = null, ["servers"] = new List<object>(), ["players"] = new List<object>() };
        const string Tracked = "(SELECT server_id FROM server_track WHERE level = 'full')";
        var counts = Q(c, $"""
            SELECT home_server AS server, level, count(*) AS n FROM player_profile
            WHERE taken_at = @t AND level > 0 AND home_server IN {Tracked}
            GROUP BY 1, 2 ORDER BY 1, 2;
            """, ("@t", sweep));
        // HQ 30+ players seen in the game in the last 3 days (player_activity, the same "active" as the SvS strength read).
        var active = Q(c, $"""
            SELECT p.home_server AS server, count(*) AS n FROM player_profile p JOIN player_activity pa ON pa.uid = p.uid
            WHERE p.taken_at = @t AND p.level >= 30 AND p.home_server IN {Tracked}
              AND pa.last_active_at <> '' AND pa.last_active_at >= {Db.AgoIso("3 days")}
            GROUP BY 1;
            """, ("@t", sweep));
        var players = Q(c, $"""
            SELECT uid, name, alliance_tag AS tag, alliance_id AS "allianceId", home_server AS server, level, power
            FROM player_profile
            WHERE taken_at = @t AND level >= @m AND home_server IN {Tracked}
            ORDER BY level DESC, power DESC;
            """, ("@t", sweep), ("@m", (long)minLevel));
        var uids = players.Select(p => (string)p["uid"]!).ToArray();

        // Readings, one pass: each player's first and last reading at every level they were seen at. Reaching L = the first
        // reading at L or above; the window opens at the last reading below L.
        var seenAt = new Dictionary<string, List<(long Level, string First, string Last)>>();
        if (uids.Length > 0)
            foreach (var r in Q(c, """
                WITH obs AS (
                    SELECT uid, seen_at AS at, level FROM player_seen WHERE uid = ANY(@u) AND level > 0
                    UNION ALL
                    SELECT uid, taken_at, level FROM player_profile WHERE uid = ANY(@u) AND level > 0)
                SELECT uid, level, MIN(at) AS first, MAX(at) AS last FROM obs GROUP BY 1, 2;
                """, ("@u", uids)))
            {
                var u = (string)r["uid"]!;
                if (!seenAt.TryGetValue(u, out var list)) seenAt[u] = list = [];
                list.Add((Convert.ToInt64(r["level"]), (string)r["first"]!, (string)r["last"]!));
            }
        (string? After, string? By) Window(string uid, long level)
        {
            if (!seenAt.TryGetValue(uid, out var list)) return (null, null);
            var by = list.Where(x => x.Level >= level).Select(x => x.First).Min(StringComparer.Ordinal);
            var after = list.Where(x => x.Level < level).Select(x => x.Last).Max(StringComparer.Ordinal);
            return (after, by);
        }
        // Exact times from every HQ board row we hold for these players.
        var exact = new Dictionary<(string, long), string>();
        if (uids.Length > 0)
            foreach (var r in Q(c, "SELECT DISTINCT uid, extra FROM board_sample WHERE board_type = 5 AND uid = ANY(@u);", ("@u", uids)))
            {
                long level = 0, ms = 0;
                foreach (var kv in ((string?)r["extra"] ?? "").Split(';'))
                {
                    if (kv.StartsWith("level=")) long.TryParse(kv[6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out level);
                    if (kv.StartsWith("updateTime=")) long.TryParse(kv[11..], NumberStyles.Integer, CultureInfo.InvariantCulture, out ms);
                }
                if (level >= minLevel && ms > 0)
                    exact[((string)r["uid"]!, level)] = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O");
            }

        foreach (var p in players)
        {
            var uid = (string)p["uid"]!;
            var top = Convert.ToInt64(p["level"]);
            var reached = new Dictionary<string, object?>();
            for (var l = (long)minLevel; l <= top; l++)
            {
                var b = Window(uid, l);
                // A reading bracket can only narrow what the board does not give; an "after" later than "by" is a reading quirk.
                var after = b.After is { } a && b.By is { } y && string.CompareOrdinal(a, y) > 0 ? null : b.After;
                reached[l.ToString(CultureInfo.InvariantCulture)] = exact.TryGetValue((uid, l), out var at)
                    ? new Dictionary<string, object?> { ["at"] = at }
                    : new Dictionary<string, object?> { ["after"] = after, ["by"] = b.By };
            }
            p["reached"] = reached;
        }
        return new() { ["sweep"] = sweep, ["minLevel"] = minLevel, ["counts"] = counts, ["active"] = active, ["players"] = players };
    }
}
