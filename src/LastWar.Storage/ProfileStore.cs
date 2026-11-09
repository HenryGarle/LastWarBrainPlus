using LastWar.Core.Model;
using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// Player profiles over time - the record of which server each player is FROM, and when.
///
/// Append-only like every other snapshot table: one row per (uid, taken_at), never updated, so a
/// transfer adds a row rather than rewriting who someone was. A range sweep stamps every row with
/// the sweep's start time, which makes a sweep a snapshot that comparisons can pick as a whole.
/// </summary>
public sealed class ProfileStore(string path)
{
    // FixHomeServers: a one-off repair of the SQLite-era data (applied in September 2026; its record table came
    // across to Postgres). Removed with the move - a fresh database has nothing to repair.

    NpgsqlConnection Open() => Db.Open(path);

    const string Columns = """
        uid, taken_at, name, home_server, server_id, current_server, src_server, cross_src_server,
        alliance_id, alliance_tag, alliance_name, power, army_power, army_kill, level, lang, country, source,
        last_seen_at, svip_level, month_card_end, thumbs_up, svip_end, flag
        """;

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "ProfileStore")) return;   // DDL once per process: it takes table locks
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS player_profile (
                uid              TEXT    NOT NULL,
                taken_at         TEXT    NOT NULL,
                name             TEXT    NOT NULL DEFAULT '',
                home_server      BIGINT NOT NULL DEFAULT 0,  -- the server they are FROM (moves on transfer)
                server_id        BIGINT NOT NULL DEFAULT 0,  -- raw serverId
                current_server   BIGINT NOT NULL DEFAULT 0,  -- where their base is right now (moves on teleport)
                src_server       BIGINT NOT NULL DEFAULT 0,
                cross_src_server BIGINT NOT NULL DEFAULT 0,  -- set only during cross-server fights
                alliance_id      TEXT    NOT NULL DEFAULT '',
                alliance_tag     TEXT    NOT NULL DEFAULT '',
                alliance_name    TEXT    NOT NULL DEFAULT '',
                power            BIGINT NOT NULL DEFAULT 0,
                army_power       BIGINT NOT NULL DEFAULT 0,
                army_kill        BIGINT NOT NULL DEFAULT 0,
                level            BIGINT NOT NULL DEFAULT 0,
                lang             TEXT    NOT NULL DEFAULT '',
                country          TEXT    NOT NULL DEFAULT '',
                source           TEXT    NOT NULL DEFAULT '',
                PRIMARY KEY (uid, taken_at)
            );
            CREATE INDEX IF NOT EXISTS ix_profile_time ON player_profile(taken_at);
            CREATE INDEX IF NOT EXISTS ix_profile_home ON player_profile(home_server, taken_at);
            -- Login moments (2026-10-09): one row per (uid, last seen) the first time we see that value, with what the
            -- player had then. The hourly pulse writes only this; the daily sweep adds its values too. Everything that
            -- asks "when was this player in the game" reads here, not the full profiles.
            CREATE TABLE IF NOT EXISTS player_seen (
                uid        TEXT   NOT NULL,
                seen_at    TEXT   NOT NULL,   -- profile lastUpdateTime, ISO
                power      BIGINT NOT NULL DEFAULT 0,
                army_power BIGINT NOT NULL DEFAULT 0,
                army_kill  BIGINT NOT NULL DEFAULT 0,
                level      BIGINT NOT NULL DEFAULT 0,
                PRIMARY KEY (uid, seen_at)
            );
            CREATE INDEX IF NOT EXISTS ix_seen_at ON player_seen(seen_at);
            -- When profiles were read (sweep or pulse) and how many answered: the hours readings were taken.
            CREATE TABLE IF NOT EXISTS profile_reading (
                taken_at TEXT   NOT NULL,
                source   TEXT   NOT NULL,
                players  BIGINT NOT NULL,
                PRIMARY KEY (taken_at, source)
            );
            """;
        cmd.ExecuteNonQuery();
        // Added 2026-09-27 (lastUpdateTime and spending signals); older rows keep the defaults.
        foreach (var (col, type) in new[] { ("last_seen_at", "TEXT NOT NULL DEFAULT ''"), ("svip_level", "BIGINT NOT NULL DEFAULT 0"),
                                             ("month_card_end", "BIGINT NOT NULL DEFAULT 0"), ("thumbs_up", "BIGINT NOT NULL DEFAULT 0"),
                                             ("svip_end", "BIGINT NOT NULL DEFAULT 0"), ("flag", "TEXT NOT NULL DEFAULT ''") })   // svip_end, flag added 2026-09-30
        {
            using var alter = c.CreateCommand();
            alter.CommandText = $"ALTER TABLE player_profile ADD COLUMN IF NOT EXISTS {col} {type};";
            alter.ExecuteNonQuery();
        }

        // chat_user was the first home for these (chat-sender lookups only). Carry its rows over
        // once and retire it, so there is one table for "where is this player from".
        using var has = c.CreateCommand();
        has.CommandText = "SELECT to_regclass('chat_user') IS NOT NULL;";
        if (has.ExecuteScalar() is not true) return;
        using var move = c.CreateCommand();
        move.CommandText = """
            INSERT INTO player_profile
              (uid, taken_at, name, home_server, server_id, current_server, src_server, alliance_tag,
               power, level, lang, country, source)
            SELECT uid, fetched_at, name, server, server, current_server, src_server, alliance,
                   power, level, lang, country, 'chat' FROM chat_user ON CONFLICT DO NOTHING;
            DROP TABLE chat_user;
            """;
        move.ExecuteNonQuery();
    }

    /// <summary>Stores profiles; existing (uid, taken_at) rows are never touched. Returns how many were new.</summary>
    public int Insert(IReadOnlyCollection<PlayerProfile> rows)
    {
        if (rows.Count == 0) return 0;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO player_profile ({Columns})
            VALUES (@u,@t,@n,@h,@s,@cs,@ss,@x,@aid,@at,@an,@p,@ap,@ak,@lv,@lang,@c,@src,@seen,@svip,@mc,@tu,@se,@fl)
            ON CONFLICT DO NOTHING;
            """;
        foreach (var n in new[] { "@u", "@t", "@n", "@h", "@s", "@cs", "@ss", "@x", "@aid", "@at", "@an", "@p", "@ap", "@ak", "@lv", "@lang", "@c", "@src", "@seen", "@svip", "@mc", "@tu", "@se", "@fl" })
            cmd.Parameters.Add(new NpgsqlParameter(n, null));
        var added = 0;
        foreach (var r in rows)
        {
            var p = cmd.Parameters;
            p["@u"].Value = r.Uid; p["@t"].Value = r.TakenAt.ToString("O"); p["@n"].Value = r.Name;
            p["@h"].Value = r.HomeServer; p["@s"].Value = r.ServerId; p["@cs"].Value = r.CurrentServer;
            p["@ss"].Value = r.SrcServer; p["@x"].Value = r.CrossFightSrcServer;
            p["@aid"].Value = r.AllianceId; p["@at"].Value = r.AllianceTag; p["@an"].Value = r.AllianceName;
            p["@p"].Value = r.Power; p["@ap"].Value = r.ArmyPower; p["@ak"].Value = r.ArmyKill;
            p["@lv"].Value = r.Level; p["@lang"].Value = r.Lang; p["@c"].Value = r.Country; p["@src"].Value = r.Source;
            p["@seen"].Value = r.LastSeen?.ToString("O") ?? ""; p["@svip"].Value = r.SvipLevel; p["@mc"].Value = r.MonthCardEnd; p["@tu"].Value = r.ThumbsUp; p["@se"].Value = r.SvipEnd; p["@fl"].Value = r.Flag;
            added += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return added;
    }

    /// <summary>
    /// Login moments: a row per (uid, last seen) the first time that value is seen (rows without one are skipped).
    /// Returns how many were new.
    /// </summary>
    public int InsertSeen(IReadOnlyCollection<PlayerProfile> rows)
    {
        var list = rows.Where(r => r.LastSeen is not null).ToList();
        if (list.Count == 0) return 0;
        using var c = Open();
        using var cmd = c.CreateCommand();
        // New moments are judged as they land: no change in stats over the week before = the game touching the
        // account, not a login (PlayerActivity.JudgeIdle).
        new PlayerActivity(path).EnsureSchema();
        cmd.CommandText = $"""
            WITH n AS (
                INSERT INTO player_seen (uid, seen_at, power, army_power, army_kill, level)
                SELECT * FROM unnest(@u, @s, @p, @ap, @ak, @lv) ON CONFLICT DO NOTHING
                RETURNING uid, seen_at, power, army_kill, level),
            idle AS ({PlayerActivity.JudgeIdle("n").Trim()} RETURNING 1)
            SELECT count(*) FROM n;
            """;
        cmd.Parameters.AddWithValue("@u", list.Select(r => r.Uid).ToArray());
        cmd.Parameters.AddWithValue("@s", list.Select(r => r.LastSeen!.Value.ToString("O")).ToArray());
        cmd.Parameters.AddWithValue("@p", list.Select(r => (long)r.Power).ToArray());
        cmd.Parameters.AddWithValue("@ap", list.Select(r => (long)r.ArmyPower).ToArray());
        cmd.Parameters.AddWithValue("@ak", list.Select(r => (long)r.ArmyKill).ToArray());
        cmd.Parameters.AddWithValue("@lv", list.Select(r => (long)r.Level).ToArray());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>The newest login moment held for every player (uid -> ISO text).</summary>
    public Dictionary<string, string> LatestSeen()
    {
        using var c = Open();
        var outp = new Dictionary<string, string>();
        foreach (var r in Db.Rows(c, "SELECT uid, MAX(seen_at) AS s FROM player_seen GROUP BY uid;"))
            outp[(string)r["uid"]!] = (string)r["s"]!;
        return outp;
    }

    /// <summary>Notes that profiles were read at this moment (a sweep or a pulse) and how many answered.</summary>
    public void AddReading(DateTimeOffset takenAt, string source, int players) =>
        Db.Exec(path, """
            INSERT INTO profile_reading (taken_at, source, players) VALUES (@t, @s, @n)
            ON CONFLICT (taken_at, source) DO UPDATE SET players = EXCLUDED.players;
            """, ("@t", takenAt.ToString("O")), ("@s", source), ("@n", (long)players));

    /// <summary>The newest profile for each uid, optionally only from rows inside (after, upTo].</summary>
    public Dictionary<string, PlayerProfile> Latest(IEnumerable<string>? uids = null,
        DateTimeOffset? after = null, DateTimeOffset? upTo = null)
    {
        var outp = new Dictionary<string, PlayerProfile>();
        using var c = Open();
        var from = after?.ToString("O") ?? "";
        var to = (upTo ?? DateTimeOffset.MaxValue).ToString("O");
        if (uids is null)
        {
            using var cmd = c.CreateCommand();
            // Whole window at once: ordered scan, last row per uid wins. Cheaper than a
            // correlated MAX() over hundreds of thousands of rows.
            cmd.CommandText = $"SELECT {Columns} FROM player_profile WHERE taken_at > @f AND taken_at <= @t ORDER BY uid, taken_at;";
            cmd.Parameters.AddWithValue("@f", from);
            cmd.Parameters.AddWithValue("@t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read()) { var p = Read(r); outp[p.Uid] = p; }
            return outp;
        }
        foreach (var chunk in uids.Distinct().Chunk(400))
        {
            using var cmd = c.CreateCommand();
            var names = chunk.Select((_, i) => $"@u{i}").ToArray();
            cmd.CommandText = $"""
                SELECT {Columns} FROM player_profile
                WHERE uid IN ({string.Join(",", names)}) AND taken_at > @f AND taken_at <= @t
                ORDER BY uid, taken_at;
                """;
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
            cmd.Parameters.AddWithValue("@f", from);
            cmd.Parameters.AddWithValue("@t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read()) { var p = Read(r); outp[p.Uid] = p; }
        }
        return outp;
    }

    public List<PlayerProfile> History(string uid)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM player_profile WHERE uid = @u ORDER BY taken_at;";
        cmd.Parameters.AddWithValue("@u", uid);
        using var r = cmd.ExecuteReader();
        var outp = new List<PlayerProfile>();
        while (r.Read()) outp.Add(Read(r));
        return outp;
    }

    /// <summary>
    /// Every uid with any tie to servers lo..hi: seen on their maps, in their power or board
    /// snapshots, applying to or from them, or profiled as from them. This is the population a
    /// sweep asks about - including people who have since left, which is the point.
    /// </summary>
    public List<string> KnownUids(int lo, int hi)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT owner_uid FROM map_tile WHERE server_id BETWEEN @lo AND @hi AND owner_uid <> ''
            UNION SELECT uid FROM power_sample WHERE server_id BETWEEN @lo AND @hi
            UNION SELECT uid FROM board_sample WHERE server_id BETWEEN @lo AND @hi
            UNION SELECT uid FROM migration_applicant WHERE from_server BETWEEN @lo AND @hi OR to_server BETWEEN @lo AND @hi
            UNION SELECT uid FROM player_profile WHERE home_server BETWEEN @lo AND @hi;
            """;
        cmd.Parameters.AddWithValue("@lo", lo);
        cmd.Parameters.AddWithValue("@hi", hi);
        using var r = cmd.ExecuteReader();
        var outp = new List<string>();
        while (r.Read()) if (r.GetString(0) is { Length: > 6 } u && u.All(char.IsAsciiDigit)) outp.Add(u);
        return outp;
    }

    /// <summary>Range sweeps held: when, how many players, and how many were away from home.</summary>
    public List<(string TakenAt, long Players, long Visiting)> Sweeps()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT taken_at, COUNT(*), SUM(CASE WHEN current_server > 0 AND current_server <> home_server THEN 1 ELSE 0 END)
            FROM player_profile WHERE source = 'sweep' GROUP BY taken_at ORDER BY taken_at;
            """;
        using var r = cmd.ExecuteReader();
        var outp = new List<(string, long, long)>();
        while (r.Read()) outp.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return outp;
    }

    static PlayerProfile Read(NpgsqlDataReader r) => new()
    {
        Uid = r.GetString(0), TakenAt = DateTimeOffset.Parse(r.GetString(1)), Name = r.GetString(2),
        HomeServer = (int)r.GetInt64(3), ServerId = (int)r.GetInt64(4), CurrentServer = (int)r.GetInt64(5),
        SrcServer = (int)r.GetInt64(6), CrossFightSrcServer = (int)r.GetInt64(7),
        AllianceId = r.GetString(8), AllianceTag = r.GetString(9), AllianceName = r.GetString(10),
        Power = r.GetInt64(11), ArmyPower = r.GetInt64(12), ArmyKill = r.GetInt64(13), Level = (int)r.GetInt64(14),
        Lang = r.GetString(15), Country = r.GetString(16), Source = r.GetString(17),
        LastSeen = r.GetString(18) is { Length: > 0 } seen ? DateTimeOffset.Parse(seen) : null,
        SvipLevel = (int)r.GetInt64(19), MonthCardEnd = r.GetInt64(20), ThumbsUp = r.GetInt64(21), SvipEnd = r.GetInt64(22), Flag = r.GetString(23),
    };
}
