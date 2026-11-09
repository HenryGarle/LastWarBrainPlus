using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// Who held the kingdom (ministry) positions, snapshot by snapshot. Append-only like every other
/// observation table: one row per (taken_at, server, position). Position 10001 is the President
/// (GovernmentConst.King_Position_id); 10002-10007 are the ministers.
/// </summary>
public sealed class KingdomStore(string path)
{
    public sealed record PositionRow(DateTimeOffset TakenAt, int Server, string PositionId, string Uid, string Name,
        string AllianceId, string Tag, long Power, long AppointedMs);

    NpgsqlConnection Open() => Db.Open(path);

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "KingdomStore")) return;   // DDL once per process: it takes table locks
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS kingdom_position (
                taken_at     TEXT    NOT NULL,
                server_id    BIGINT NOT NULL,
                position_id  TEXT    NOT NULL,
                uid          TEXT    NOT NULL DEFAULT '',
                name         TEXT    NOT NULL DEFAULT '',
                alliance_id  TEXT    NOT NULL DEFAULT '',
                alliance_tag TEXT    NOT NULL DEFAULT '',
                power        BIGINT NOT NULL DEFAULT 0,
                appointed_ms BIGINT NOT NULL DEFAULT 0,
                PRIMARY KEY (taken_at, server_id, position_id)
            );
            CREATE INDEX IF NOT EXISTS ix_kingdom_uid ON kingdom_position(uid);
            """;
        cmd.ExecuteNonQuery();
    }

    public int Insert(IEnumerable<PositionRow> rows)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO kingdom_position (taken_at, server_id, position_id, uid, name, alliance_id, alliance_tag, power, appointed_ms)
            VALUES (@t, @s, @p, @u, @n, @a, @g, @w, @m) ON CONFLICT DO NOTHING;
            """;
        var n = 0;
        foreach (var r in rows)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@t", r.TakenAt.ToString("O"));
            cmd.Parameters.AddWithValue("@s", r.Server);
            cmd.Parameters.AddWithValue("@p", r.PositionId);
            cmd.Parameters.AddWithValue("@u", r.Uid);
            cmd.Parameters.AddWithValue("@n", r.Name);
            cmd.Parameters.AddWithValue("@a", r.AllianceId);
            cmd.Parameters.AddWithValue("@g", r.Tag);
            cmd.Parameters.AddWithValue("@w", r.Power);
            cmd.Parameters.AddWithValue("@m", r.AppointedMs);
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }
}
