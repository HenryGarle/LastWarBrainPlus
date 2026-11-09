using LastWar.Core.Model;
using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// Where one player belonged, and what they had, as of one side of a comparison. Server is the
/// HOME server when a profile sweep covers the window (Source "profile"), otherwise the server
/// whose map showed their base (Source "map"). Based is where the base physically sat, which
/// differs from Server for a player over on another server to fight.
/// </summary>
public sealed record PlayerState(
    string Uid, int Server, string Name, string AllianceTag,
    long? Power, long? HeroPower, string SeenAt)
{
    public string AllianceId { get; init; } = "";
    public int Based { get; init; }
    public string Source { get; init; } = "map";
    public bool Visiting => Based > 0 && Based != Server;
}

/// <summary>One leaderboard row. Uid is an alliance id on the alliance boards (1, 2).</summary>
public sealed record BoardRow(int Rank, string Uid, string Name, string Tag, long Value)
{
    public string AllianceId { get; init; } = "";
    public int SrcServer { get; init; }
    public string Extra { get; init; } = "";
}

/// <summary>
/// The view a before/after comparison needs: every player across a RANGE of servers, located by
/// the map and valued by the power and board snapshots, as of a window of time.
///
/// It is a window rather than a single "as of" because the after side must only ever use data
/// taken after the event. A server with no fresh scan would otherwise fall back to its old one,
/// and everyone on it would look like they stayed put and did nothing.
/// </summary>
public sealed class WaveStore(string path)
{
    NpgsqlConnection Open() => Db.Open(path);

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "WaveStore")) return;   // DDL once per process: it takes table locks
        using var c = Open();
        using var cmd = c.CreateCommand();
        // Leaderboards are the only source of hero power, so they are kept as snapshots like
        // power is: append-only, never overwritten.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS board_sample (
                taken_at     TEXT    NOT NULL,
                server_id    BIGINT NOT NULL,
                board_type   BIGINT NOT NULL,
                rank         BIGINT NOT NULL,
                uid          TEXT    NOT NULL,
                name         TEXT    NOT NULL DEFAULT '',
                alliance_tag TEXT    NOT NULL DEFAULT '',
                value        BIGINT NOT NULL,
                PRIMARY KEY (server_id, board_type, taken_at, uid)
            );
            CREATE INDEX IF NOT EXISTS ix_board_uid ON board_sample(uid);
            """;
        cmd.ExecuteNonQuery();
        // Added 2026-09-24. Older rows keep the defaults; nothing existing is rewritten.
        //   alliance_id - player boards: the player's alliance. Alliance boards: uid IS the alliance.
        //   src_server  - the server the row says it is from (crossFightSrcServerId when set).
        //   extra       - the board's secondary figures, "key=value;..." (kills, members, hero id...).
        foreach (var (col, ddl) in new[]
        {
            ("alliance_id", "TEXT NOT NULL DEFAULT ''"),
            ("src_server", "BIGINT NOT NULL DEFAULT 0"),
            ("extra", "TEXT NOT NULL DEFAULT ''"),
        })
        {
            using var alter = c.CreateCommand();
            alter.CommandText = $"ALTER TABLE board_sample ADD COLUMN IF NOT EXISTS {col} {ddl};";
            alter.ExecuteNonQuery();
        }
    }

    // FillOldBoardAlliances: a one-off repair of the SQLite-era data (applied in September 2026; its record table came
    // across to Postgres). Removed with the move - a fresh database has nothing to repair.

    public int InsertBoard(
        DateTimeOffset takenAt, int server, int type,
        IEnumerable<(int Rank, string Uid, string Name, string Tag, long Value)> rows) =>
        InsertBoard(takenAt, server, type, rows.Select(r => new BoardRow(r.Rank, r.Uid, r.Name, r.Tag, r.Value)));

    public int InsertBoard(DateTimeOffset takenAt, int server, int type, IEnumerable<BoardRow> rows)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO board_sample
              (taken_at, server_id, board_type, rank, uid, name, alliance_tag, value, alliance_id, src_server, extra)
            VALUES (@t, @s, @b, @r, @u, @n, @a, @v, @aid, @src, @x) ON CONFLICT DO NOTHING;
            """;
        var n = 0;
        foreach (var row in rows)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@t", takenAt.ToString("O"));
            cmd.Parameters.AddWithValue("@s", server);
            cmd.Parameters.AddWithValue("@b", type);
            cmd.Parameters.AddWithValue("@r", row.Rank);
            cmd.Parameters.AddWithValue("@u", row.Uid);
            cmd.Parameters.AddWithValue("@n", row.Name);
            cmd.Parameters.AddWithValue("@a", row.Tag);
            cmd.Parameters.AddWithValue("@v", row.Value);
            cmd.Parameters.AddWithValue("@aid", row.AllianceId);
            cmd.Parameters.AddWithValue("@src", row.SrcServer);
            cmd.Parameters.AddWithValue("@x", row.Extra);
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }
    /// <summary>
    /// Every player tied to servers lo..hi as of the window (after, upTo]. Profiles are the
    /// authority: the newest profile in the window gives home server, alliance and power, for
    /// everyone profiled - including players now homed OUTSIDE the range, so a move out reads as
    /// a move rather than a disappearance. Players with no profile in the window fall back to the
    /// newest map scan of their server plus the newest power snapshot. Hero power always comes
    /// from the boards. Covered lists the servers the window actually has data for, so a caller
    /// can tell an empty server from an unscanned one; a profile sweep covers the whole range.
    /// </summary>
    public (Dictionary<string, PlayerState> Players, HashSet<int> Covered) StateIn(
        int lo, int hi, DateTimeOffset? after, DateTimeOffset upTo)
    {
        var from = after?.ToString("O") ?? "";
        var to = upTo.ToString("O");
        using var c = Open();

        // Power and hero power first, keyed by uid, then laid over the map.
        var power = new Dictionary<string, long>();
        var hero = new Dictionary<string, long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT p.uid, p.power FROM power_sample p
                WHERE p.server_id BETWEEN @lo AND @hi
                  AND p.taken_at = (SELECT MAX(taken_at) FROM power_sample q
                                    WHERE q.server_id = p.server_id AND q.taken_at > @from AND q.taken_at <= @to);
                """;
            Bind(cmd);
            using var r = cmd.ExecuteReader();
            while (r.Read()) power[r.GetString(0)] = r.GetInt64(1);
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT b.uid, b.value FROM board_sample b
                WHERE b.board_type = 13 AND b.server_id BETWEEN @lo AND @hi
                  AND b.taken_at = (SELECT MAX(taken_at) FROM board_sample q
                                    WHERE q.server_id = b.server_id AND q.board_type = 13
                                      AND q.taken_at > @from AND q.taken_at <= @to);
                """;
            Bind(cmd);
            using var r = cmd.ExecuteReader();
            while (r.Read()) hero[r.GetString(0)] = r.GetInt64(1);
        }

        var players = new Dictionary<string, PlayerState>();
        var covered = new HashSet<int>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT t.owner_uid, t.server_id, t.name, t.alliance_tag, s.taken_at, t.alliance_id
                FROM map_tile t JOIN map_scan s ON s.scan_id = t.scan_id
                WHERE s.scan_id IN (
                    SELECT (SELECT m2.scan_id FROM map_scan m2
                            WHERE m2.server_id = m.server_id AND m2.taken_at > @from AND m2.taken_at <= @to
                            ORDER BY m2.taken_at DESC LIMIT 1)
                    FROM map_scan m WHERE m.server_id BETWEEN @lo AND @hi GROUP BY m.server_id);
                """;
            Bind(cmd);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var uid = r.GetString(0);
                var server = (int)r.GetInt64(1);
                covered.Add(server);
                if (uid.Length == 0) continue;
                // A player seen on two servers inside one window moved during it; keep the later.
                var seen = r.GetString(4);
                if (players.TryGetValue(uid, out var had) && string.CompareOrdinal(had.SeenAt, seen) > 0) continue;
                players[uid] = new PlayerState(uid, server, r.GetString(2), r.GetString(3),
                    power.TryGetValue(uid, out var p) ? p : null,
                    hero.TryGetValue(uid, out var h) ? h : null, seen)
                { AllianceId = r.GetString(5), Based = server };
            }
        }

        var profiles = new ProfileStore(path);
        profiles.EnsureSchema();
        var swept = false;
        foreach (var (uid, pr) in profiles.Latest(null, after, upTo))
        {
            swept |= pr.Source == "sweep";
            players.TryGetValue(uid, out var seenOnMap);
            players[uid] = new PlayerState(uid, pr.HomeServer, pr.Name, pr.AllianceTag, pr.Power,
                hero.TryGetValue(uid, out var h) ? h : null, pr.TakenAt.ToString("O"))
            {
                AllianceId = pr.AllianceId,
                // The profile says where the base is; a map sighting is the fallback evidence.
                Based = pr.CurrentServer > 0 ? pr.CurrentServer : seenOnMap?.Server ?? 0,
                Source = "profile",
            };
        }
        if (swept) for (var s = lo; s <= hi; s++) covered.Add(s);
        return (players, covered);

        void Bind(NpgsqlCommand cmd)
        {
            cmd.Parameters.AddWithValue("@lo", lo);
            cmd.Parameters.AddWithValue("@hi", hi);
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@to", to);
        }
    }

    /// <summary>
    /// The tiles from a server's newest map scan, provided it was taken after <paramref name="since"/>.
    /// This is what lets power collection run from an account that is not allowed to view another
    /// server's map: one account that can do the sweep, any number that can then ask about players.
    /// </summary>
    /// <summary>
    /// Newest known total hero power per player. Only two places publish it: the hero power board
    /// (top 200 per server) and transfer applications - so players on neither have no figure.
    /// </summary>
    public Dictionary<string, long> LatestHeroPower(IEnumerable<string> uids)
    {
        var outp = new Dictionary<string, long>();
        using var c = Open();
        foreach (var chunk in uids.Distinct().Chunk(400))
        {
            using var cmd = c.CreateCommand();
            var names = chunk.Select((_, i) => $"@u{i}").ToArray();
            var inList = string.Join(",", names);
            cmd.CommandText = $"""
                SELECT uid, value AS hp, taken_at AS at FROM board_sample WHERE board_type = 13 AND uid IN ({inList})
                UNION ALL
                SELECT uid, hero_power, captured_at FROM migration_applicant WHERE hero_power > 0 AND uid IN ({inList})
                ORDER BY at;
                """;
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) outp[r.GetString(0)] = r.GetInt64(1);   // ordered by time: newest wins
        }
        return outp;
    }

    /// <summary>The server each player most recently applied to transfer to, where we captured one.</summary>
    public Dictionary<string, int> LatestApplication(IEnumerable<string> uids)
    {
        var outp = new Dictionary<string, int>();
        using var c = Open();
        foreach (var chunk in uids.Distinct().Chunk(400))
        {
            using var cmd = c.CreateCommand();
            var names = chunk.Select((_, i) => $"@u{i}").ToArray();
            cmd.CommandText = $"SELECT uid, to_server FROM migration_applicant WHERE uid IN ({string.Join(",", names)}) ORDER BY captured_at;";
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) outp[r.GetString(0)] = (int)r.GetInt64(1);
        }
        return outp;
    }

    public (List<MapTile> Tiles, string? TakenAt) LatestTiles(int server, DateTimeOffset since)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.taken_at, t.point_id, t.point_type, t.owner_uid, t.name,
                   t.alliance_id, t.alliance_tag, t.hq_level, t.build_id
            FROM map_tile t JOIN map_scan s ON s.scan_id = t.scan_id
            WHERE s.scan_id = (SELECT scan_id FROM map_scan WHERE server_id = @s AND taken_at > @since
                               ORDER BY taken_at DESC LIMIT 1);
            """;
        cmd.Parameters.AddWithValue("@s", server);
        cmd.Parameters.AddWithValue("@since", since.ToString("O"));
        using var r = cmd.ExecuteReader();
        var tiles = new List<MapTile>();
        string? at = null;
        while (r.Read())
        {
            at ??= r.GetString(0);
            tiles.Add(new MapTile
            {
                Server = new ServerId(server),
                PointId = (int)r.GetInt64(1),
                PointType = (int)r.GetInt64(2),
                OwnerUid = new PlayerUid(r.GetString(3)),
                Name = r.GetString(4),
                AllianceId = r.GetString(5),
                AllianceTag = r.GetString(6),
                HqLevel = (int)r.GetInt64(7),
                BuildId = (int)r.GetInt64(8),
            });
        }
        return (tiles, at);
    }
}