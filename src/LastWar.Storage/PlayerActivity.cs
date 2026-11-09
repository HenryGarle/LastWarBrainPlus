using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// Is a player being played? DERIVED, rebuilt with the other histories.
///
/// Only things a player has to DO count as proof:
///   last_seen_at    - newest profile lastUpdateTime that is a real login (player_seen; see mass_touch and idle_seen below)
///   last_donated_at - newest alliance donation (al.rank donateTime)
///   last_chat_at    - newest chat message
///   last_active_at  - the latest of those three: what "active" is judged on
///
/// Not proof, kept for display only:
///   last_updated_at - newest lastUpdateTime of any kind: when the game last touched the account at all.
///   last_rise_at    - newest power reading higher than the one before. Buildings, research and
///                     troops queued earlier finish while the player is away, so power rises for
///                     almost everyone (96% of alliance members in a week, 2026-09-28).
///
/// mass_touch: minutes when the GAME touched thousands of accounts at once. lastUpdateTime moves
/// for them without a login - e.g. 2,408 accounts in one minute on 2026-09-25 08:20, 99% of them
/// under 20M power, while a minute of real logins is ~98% accounts over 20M. A minute with at
/// least 100 last-seen moments of which 85% or more are accounts under 20M power is a touch;
/// last-seen values inside one are ignored (the player's previous real one counts instead).
///
/// idle_seen: last-seen moments with no change in power, kills or HQ level over the 7 days before them (Henry,
/// 2026-11-03). The game moves lastUpdateTime when it changes an account - e.g. its base is attacked or moved, mostly
/// on Saturdays when people come in to attack - so abandoned accounts kept "logging in" (24,664 HQ 25+ accounts with
/// no power change in 5 weeks all had October last-seen moments, half of them on Saturdays). Judged when a moment is
/// stored (ProfileStore.InsertSeen) against the player's reading 7 days earlier (their first reading if we have none
/// that old); older moments by JudgeBacklog.
///
/// Active = last_active_at inside the window (default 7 days) before the moment looked at.
/// </summary>
public sealed class PlayerActivity(string path)
{
    public const int WindowDays = 7;

    /// <summary>Accounts under this power sharing a minute mark a game-side touch, not logins.</summary>
    public const long SmallAccount = 20_000_000;

    /// <summary>SQL for "the window starts here" relative to an ISO timestamp expression.</summary>
    public static string WindowStart(string isoExpr, int days = WindowDays) =>
        Db.MinusIso(isoExpr, $"{days} days");

    /// <summary>
    /// SQL: true when a last-seen value is NOT proof of a login - it falls in a game-side mass touch,
    /// or it is the first value we ever saw for that account (first_look). A value only counts once
    /// we have watched it change: a dead account first profiled today reports whenever it was last
    /// touched, and before this rule every such first look counted as "active" (Henry, 2026-09-30:
    /// level-5 accounts with one last-seen and no change since were padding the low end). The uid
    /// comes from the same row: "p.last_seen_at" -> "p.uid", "last_seen_at" -> "uid".
    /// </summary>
    public static string NotALogin(string isoExpr)
    {
        var dot = isoExpr.LastIndexOf('.');
        var uid = dot < 0 ? "uid" : isoExpr[..dot] + ".uid";
        // IN, not a correlated EXISTS: with an unqualified "uid", EXISTS (... first_look fl WHERE fl.uid = uid)
        // bound uid to fl.uid, so ANY account's first look at the same second hid a real login (fixed 2026-09-30).
        // idle_seen's columns are player / at, not uid / seen_at, so an unqualified uid or seen_at binds to the outer row.
        return $"(substr({isoExpr}, 1, 16) IN (SELECT minute FROM mass_touch) " +
               $"OR ({uid}, {isoExpr}) IN (SELECT uid, seen FROM first_look) " +
               $"OR EXISTS (SELECT 1 FROM idle_seen i WHERE i.player = {uid} AND i.at = {isoExpr}))";
    }

    public const int IdleDays = 7;

    /// <summary>
    /// SQL: files into idle_seen every moment in <paramref name="rows"/> (a query or table with uid, seen_at, power,
    /// army_kill, level) whose stats equal the player's reading from <see cref="IdleDays"/> days before it - or, with no
    /// reading that old, their first reading. Stats = power, kills and HQ level; profiles and last-seen rows both count.
    /// </summary>
    public static string JudgeIdle(string rows) => $"""
        INSERT INTO idle_seen (player, at)
        SELECT n.uid, n.seen_at FROM {rows} n
        CROSS JOIN LATERAL (SELECT {Db.MinusIso("n.seen_at", $"{IdleDays} days")} AS cut) k
        LEFT JOIN LATERAL (
          SELECT power, army_kill, level FROM (
            (SELECT p.taken_at AS at, p.power, p.army_kill, p.level, 0 AS late FROM player_profile p WHERE p.uid = n.uid AND p.taken_at <= k.cut ORDER BY p.taken_at DESC LIMIT 1)
            UNION ALL (SELECT s.seen_at, s.power, s.army_kill, s.level, 0 FROM player_seen s WHERE s.uid = n.uid AND s.seen_at <= k.cut ORDER BY s.seen_at DESC LIMIT 1)
            UNION ALL (SELECT p.taken_at, p.power, p.army_kill, p.level, 1 FROM player_profile p WHERE p.uid = n.uid ORDER BY p.taken_at LIMIT 1)
            UNION ALL (SELECT s.seen_at, s.power, s.army_kill, s.level, 1 FROM player_seen s WHERE s.uid = n.uid ORDER BY s.seen_at LIMIT 1)
          ) x ORDER BY late, CASE WHEN late = 0 THEN at END DESC, at LIMIT 1) ref ON true
        WHERE ref.power IS NULL OR (n.power, n.army_kill, n.level) = (ref.power, ref.army_kill, ref.level)
        ON CONFLICT DO NOTHING
        """;

    public const int Buckets = 64;

    /// <summary>
    /// Judges the last-seen moments stored before idle_seen existed, one 1/64th of the players at a time (about a minute
    /// each on hosted, 2026-11-03), each noted in schema_fix so a restart carries on. Returns how many were done.
    /// </summary>
    public int JudgeBacklog(TimeSpan? budget = null, Action<string>? log = null)
    {
        EnsureSchema();
        using var c = Open();
        if (Db.Rows(c, "SELECT to_regclass('player_seen') IS NOT NULL AS x;")[0]["x"] is not true) return 0;
        var done = Db.Rows(c, "SELECT name FROM schema_fix WHERE name LIKE 'idle_seen_%';").Select(r => (string)r["name"]!).ToHashSet();
        var started = DateTime.UtcNow;
        var n = 0;
        for (var k = 0; k < Buckets; k++)
        {
            if (done.Contains($"idle_seen_{k}")) continue;
            if (budget is { } b && DateTime.UtcNow - started > b) break;
            using var cmd = Db.Command(c, JudgeIdle($"(SELECT uid, seen_at, power, army_kill, level FROM player_seen WHERE (hashtext(uid) & {Buckets - 1}) = {k})"));
            cmd.CommandTimeout = 0;
            var rows = cmd.ExecuteNonQuery();
            Db.Command(c, $"INSERT INTO schema_fix (name, applied_at, rows) VALUES ('idle_seen_{k}', {Db.NowIso} || 'Z', {rows}) ON CONFLICT DO NOTHING;").ExecuteNonQuery();
            log?.Invoke($"idle_seen bucket {k + 1}/{Buckets}: {rows} idle moments");
            n++;
        }
        return n;
    }

    NpgsqlConnection Open() => Db.Open(path);

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "PlayerActivity")) return;   // DDL once per process: it takes table locks
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS player_activity (
                uid          TEXT    NOT NULL PRIMARY KEY,
                samples      BIGINT NOT NULL,
                last_at      TEXT    NOT NULL,
                last_rise_at TEXT    NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS mass_touch (minute TEXT PRIMARY KEY, players BIGINT NOT NULL, small BIGINT NOT NULL);
            -- The first last-seen value each account showed us: not proof of anything (NotALogin).
            CREATE TABLE IF NOT EXISTS first_look (uid TEXT PRIMARY KEY, seen TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS idle_seen (player TEXT NOT NULL, at TEXT NOT NULL, PRIMARY KEY (player, at));
            CREATE TABLE IF NOT EXISTS schema_fix (name TEXT PRIMARY KEY, applied_at TEXT NOT NULL, rows BIGINT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        foreach (var col in new[] { "last_seen_at", "last_active_at", "last_donated_at", "last_chat_at", "last_updated_at" })
        {
            using var alter = c.CreateCommand();
            alter.CommandText = $"ALTER TABLE player_activity ADD COLUMN IF NOT EXISTS {col} TEXT NOT NULL DEFAULT '';";
            alter.ExecuteNonQuery();
        }
    }

    public int Rebuild()
    {
        EnsureSchema();
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        bool Has(string table) { cmd.CommandText = $"SELECT to_regclass('{table}') IS NOT NULL;"; return cmd.ExecuteScalar() is true; }
        void Run(string sql) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        var (profiles, rosters, chat) = (Has("player_profile") && Has("player_seen"), Has("alliance_member_sample"), Has("chat_message"));

        // Power readings: samples, newest reading, newest rise. MAX(power) per moment: the same reading
        // can be filed under two servers (home + based).
        Run("""
            DELETE FROM player_activity;
            INSERT INTO player_activity (uid, samples, last_at, last_rise_at)
            SELECT uid, COUNT(*), MAX(taken_at), COALESCE(MAX(CASE WHEN power > prev THEN taken_at END), '')
            FROM (SELECT uid, taken_at, power, LAG(power) OVER (PARTITION BY uid ORDER BY taken_at) AS prev
                  FROM (SELECT uid, taken_at, MAX(power) AS power FROM power_sample GROUP BY uid, taken_at) m) r
            GROUP BY uid;
            """);
        if (profiles)
        {
            Run($"""
                -- Login moments live in player_seen (one row per uid and last-seen value, with the power seen then).
                -- Last seen only moves forward, so a player's first value we saw is their smallest.
                DELETE FROM mass_touch;
                INSERT INTO mass_touch (minute, players, small)
                SELECT substr(seen_at, 1, 16), COUNT(*), COUNT(*) FILTER (WHERE power < {SmallAccount})
                FROM player_seen
                GROUP BY 1 HAVING COUNT(*) >= 100 AND COUNT(*) FILTER (WHERE power < {SmallAccount}) >= 0.85 * COUNT(*);

                DELETE FROM first_look;
                INSERT INTO first_look (uid, seen) SELECT uid, MIN(seen_at) FROM player_seen GROUP BY uid;

                INSERT INTO player_activity (uid, samples, last_at)
                SELECT uid, 0, MAX(taken_at) FROM player_profile
                WHERE uid NOT IN (SELECT uid FROM player_activity) GROUP BY uid;

                -- lastUpdateTime is UTC seconds, stored as ISO text like every other time.
                -- Written out rather than NOT NotALogin(...): as AND-ed NOTs the idle check plans as an anti-join, not a
                -- lookup per row (26M rows on hosted).
                UPDATE player_activity a SET last_seen_at = s.seen
                FROM (SELECT p.uid, MAX(p.seen_at) AS seen FROM player_seen p
                      WHERE substr(p.seen_at, 1, 16) NOT IN (SELECT minute FROM mass_touch)
                        AND (p.uid, p.seen_at) NOT IN (SELECT uid, seen FROM first_look)
                        AND NOT EXISTS (SELECT 1 FROM idle_seen i WHERE i.player = p.uid AND i.at = p.seen_at)
                      GROUP BY p.uid) s
                WHERE s.uid = a.uid;
                UPDATE player_activity a SET last_updated_at = s.seen
                FROM (SELECT uid, MAX(seen_at) AS seen FROM player_seen GROUP BY uid) s WHERE s.uid = a.uid;
                """);
        }
        if (rosters)
            Run("""
                UPDATE player_activity a SET last_donated_at = d.at
                FROM (SELECT uid, MAX(donated_at) AS at FROM alliance_member_sample WHERE donated_at <> '' GROUP BY uid) d
                WHERE d.uid = a.uid;
                """);
        if (chat)
            Run("""
                UPDATE player_activity a SET last_chat_at = m.at
                FROM (SELECT sender_uid AS uid, MAX(sent_at) AS at FROM chat_message WHERE sender_uid ~ '^[0-9]' GROUP BY sender_uid) m
                WHERE m.uid = a.uid;
                """);
        Run("UPDATE player_activity SET last_active_at = GREATEST(last_seen_at, last_donated_at, last_chat_at);");
        cmd.CommandText = "SELECT COUNT(*) FROM player_activity;";
        var n = Convert.ToInt32(cmd.ExecuteScalar());
        tx.Commit();
        return n;
    }
}
