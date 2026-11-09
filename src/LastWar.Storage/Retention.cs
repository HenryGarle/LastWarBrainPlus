namespace LastWar.Storage;

/// <summary>
/// Keeps the database to the data points that matter (Henry, 2026-10-09). Before this it grew ~1.5 GB a day:
/// hourly pulse profiles, two full profile sweeps a day and roster reads every 6 hours, each a full copy.
///
///   - Login moments live in player_seen (ProfileStore): one small row per (uid, last seen). The hourly pulse
///     writes only those; the once-a-day sweep writes full profiles.
///   - Full snapshots - profile sweeps, their power rows, roster power rows and alliance rosters - are kept one a
///     day for the last <see cref="DailyDays"/> days and one a week before that. Each day or week keeps its
///     biggest snapshot (a one-server sweep never stands in for a full one), the latest on a tie. Today is
///     never touched.
///
/// Backfill() is the one-off conversion of the old data: login moments out of every stored profile, readings
/// noted, then the pulse's full profile rows removed. It runs once (schema_fix 'login_moments').
/// </summary>
public sealed class Retention(string path)
{
    public const int DailyDays = 14;

    /// <summary>One-off: login moments and readings out of the old profiles, then the pulse profiles go. Returns rows removed.</summary>
    public long Backfill()
    {
        new ProfileStore(path).EnsureSchema();
        using var c = Db.Open(path);
        Db.Command(c, "CREATE TABLE IF NOT EXISTS schema_fix (name TEXT PRIMARY KEY, applied_at TEXT NOT NULL, rows BIGINT NOT NULL);").ExecuteNonQuery();
        if (Db.Rows(c, "SELECT 1 AS x FROM schema_fix WHERE name = 'login_moments';").Count > 0) return 0;
        using var tx = c.BeginTransaction();
        void Run(string sql) { using var cmd = Db.Command(c, sql); cmd.Transaction = tx; cmd.CommandTimeout = 0; cmd.ExecuteNonQuery(); }
        Run("""
            INSERT INTO player_seen (uid, seen_at, power, army_power, army_kill, level)
            SELECT DISTINCT ON (uid, last_seen_at) uid, last_seen_at, power, army_power, army_kill, level
            FROM player_profile WHERE last_seen_at <> '' ORDER BY uid, last_seen_at, taken_at
            ON CONFLICT DO NOTHING;
            INSERT INTO profile_reading (taken_at, source, players)
            SELECT taken_at, source, COUNT(*) FROM player_profile WHERE source IN ('sweep', 'pulse') GROUP BY 1, 2
            ON CONFLICT DO NOTHING;
            """);
        using var del = Db.Command(c, "DELETE FROM player_profile WHERE source = 'pulse';");
        del.Transaction = tx;
        del.CommandTimeout = 0;
        long removed = del.ExecuteNonQuery();
        Run($"INSERT INTO schema_fix (name, applied_at, rows) VALUES ('login_moments', {Db.NowIso} || 'Z', {removed});");
        tx.Commit();
        return removed;
    }

    /// <summary>
    /// Thins full snapshots older than today to one a day (last <paramref name="dailyDays"/> days) or one a week.
    /// Returns rows removed per table.
    /// </summary>
    public Dictionary<string, long> Thin(DateTimeOffset now, int dailyDays = DailyDays)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).ToString("O");
        var daily = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-dailyDays), TimeSpan.Zero).ToString("O");
        // The day for recent snapshots, the Monday of its week for older ones.
        const string Bucket = """
            CASE WHEN taken_at >= @daily THEN substr(taken_at, 1, 10)
                 ELSE to_char(date_trunc('week', taken_at::timestamptz AT TIME ZONE 'UTC'), 'YYYY-MM-DD') END
            """;
        var outp = new Dictionary<string, long>();
        using var c = Db.Open(path);
        long Run(string table, string sql)
        {
            using var cmd = Db.Command(c, sql, ("@today", today), ("@daily", daily));
            cmd.CommandTimeout = 0;
            return outp[table] = outp.GetValueOrDefault(table) + cmd.ExecuteNonQuery();
        }
        Run("player_profile", $"""
            WITH snaps AS (SELECT taken_at, COUNT(*) AS n FROM player_profile
                           WHERE source = 'sweep' AND taken_at < @today GROUP BY taken_at),
                 keep AS (SELECT DISTINCT ON (b) taken_at FROM (SELECT taken_at, n, {Bucket} AS b FROM snaps) s
                          ORDER BY b, n DESC, taken_at DESC)
            DELETE FROM player_profile p USING snaps s
            WHERE p.source = 'sweep' AND p.taken_at = s.taken_at AND s.taken_at NOT IN (SELECT taken_at FROM keep);
            """);
        Run("power_sample", $"""
            WITH snaps AS (SELECT source, server_id, taken_at, COUNT(*) AS n FROM power_sample
                           WHERE source IN ('Profile', 'AllianceRoster') AND taken_at < @today GROUP BY 1, 2, 3),
                 keep AS (SELECT DISTINCT ON (source, server_id, b) source, server_id, taken_at
                          FROM (SELECT *, {Bucket} AS b FROM snaps) s ORDER BY source, server_id, b, n DESC, taken_at DESC),
                 gone AS (SELECT source, server_id, taken_at FROM snaps EXCEPT SELECT source, server_id, taken_at FROM keep)
            DELETE FROM power_sample p USING gone g
            WHERE p.source = g.source AND p.server_id = g.server_id AND p.taken_at = g.taken_at;
            """);
        Run("alliance_member_sample", $"""
            WITH snaps AS (SELECT alliance_id, taken_at, COUNT(*) AS n FROM alliance_member_sample
                           WHERE taken_at < @today GROUP BY 1, 2),
                 keep AS (SELECT DISTINCT ON (alliance_id, b) alliance_id, taken_at
                          FROM (SELECT *, {Bucket} AS b FROM snaps) s ORDER BY alliance_id, b, n DESC, taken_at DESC),
                 gone AS (SELECT alliance_id, taken_at FROM snaps EXCEPT SELECT alliance_id, taken_at FROM keep)
            DELETE FROM alliance_member_sample m USING gone g
            WHERE m.alliance_id = g.alliance_id AND m.taken_at = g.taken_at;
            """);
        return outp;
    }

    /// <summary>Gives the freed space back to the disk (VACUUM FULL: locks each table while it rewrites it).</summary>
    public void Compact()
    {
        using var c = Db.Open(path);
        foreach (var t in new[] { "player_profile", "player_seen", "power_sample", "alliance_member_sample" })
        {
            using var cmd = Db.Command(c, $"VACUUM (FULL, ANALYZE) {t};");
            cmd.CommandTimeout = 0;
            cmd.ExecuteNonQuery();
        }
    }
}
