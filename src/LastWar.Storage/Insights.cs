using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// The read side behind the explorer pages. Everything in the store already relates on two keys:
/// the player uid (map tiles, power samples, board rows, chat, transfer applications) and the
/// alliance id / tag. This class is the one place those joins are written down.
///
/// Queries return plain rows - column name to value - rather than a type per page. The SQL is
/// the shape; the page renders what it gets. A new insight is a new query here, not a new class.
///
/// Origin server comes from the uid itself: its last six digits are the server the account was
/// created on, which does not change when the player transfers.
/// </summary>
public sealed partial class Insights(string path)
{
    public const string OriginSql = "CAST(NULLIF(right({0}, 6), '') AS BIGINT)";

    NpgsqlConnection Open() => Db.Open(path);

    /// <summary>
    /// Indexes for the joins the pages make, plus the applicant table. Idempotent. Creating an
    /// index briefly takes the write lock; a collector writing at the same moment waits on the
    /// driver's busy timeout rather than failing.
    /// </summary>
    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "Insights")) return;   // DDL once per process: it takes table locks
        new LastWarDb(path).EnsureSchema();   // also applies its one-off data moves (MovePulsePower)
        new PlayerActivity(path).EnsureSchema();
        new PlayerLatest(path).EnsureSchema();
        new IntelStore(path).EnsureSchema();
        new HeroModel(path).EnsureSchema();
        // The stores own their one-off data fixes (schema_fix); running them at startup applies any pending.
        new WaveStore(path).EnsureSchema();
        new ProfileStore(path).EnsureSchema();
        new KingdomStore(path).EnsureSchema();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_power_uid      ON power_sample(uid, taken_at);
            CREATE INDEX IF NOT EXISTS ix_tile_alliance  ON map_tile(alliance_id);
            CREATE INDEX IF NOT EXISTS ix_scan_server    ON map_scan(server_id, taken_at);
            CREATE INDEX IF NOT EXISTS ix_chat_al        ON chat_message(sender_al);

            CREATE TABLE IF NOT EXISTS migration_applicant (
                captured_at  TEXT    NOT NULL,
                uid          TEXT    NOT NULL,
                name         TEXT    NOT NULL DEFAULT '',
                power        BIGINT NOT NULL DEFAULT 0,
                from_server  BIGINT NOT NULL DEFAULT 0,
                to_server    BIGINT NOT NULL DEFAULT 0,
                alliance     TEXT    NOT NULL DEFAULT '',
                abbr         TEXT    NOT NULL DEFAULT '',
                apply_score  BIGINT NOT NULL DEFAULT 0,
                apply_state  BIGINT NOT NULL DEFAULT 0,
                hero_power   BIGINT NULL,
                thp_rank     BIGINT NULL,
                message      TEXT    NOT NULL DEFAULT '',
                PRIMARY KEY (to_server, captured_at, uid)
            );
            CREATE INDEX IF NOT EXISTS ix_applicant_uid ON migration_applicant(uid);
            """;
        cmd.CommandTimeout = 120;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Loads one applicants CSV. Safe to repeat: the key is (to, captured, uid).</summary>
    public int ImportApplicants(string csvPath, DateTimeOffset capturedAt)
    {
        var n = 0;
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var r in Import.Csv.Read(csvPath))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO migration_applicant
                VALUES (@t, @u, @n, @p, @f, @to, @al, @ab, @sc, @st, @hp, @hr, @m) ON CONFLICT DO NOTHING;
                """;
            string V(string k) => r.TryGetValue(k, out var v) ? v : "";
            long L(string k) => long.TryParse(V(k), out var x) ? x : 0;
            object N(string k) => long.TryParse(V(k), out var x) ? x : DBNull.Value;
            cmd.Parameters.AddWithValue("@t", capturedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@u", V("uid"));
            cmd.Parameters.AddWithValue("@n", V("name"));
            cmd.Parameters.AddWithValue("@p", L("power"));
            cmd.Parameters.AddWithValue("@f", L("fromServer"));
            cmd.Parameters.AddWithValue("@to", L("toServer"));
            cmd.Parameters.AddWithValue("@al", V("alName"));
            cmd.Parameters.AddWithValue("@ab", V("abbr"));
            cmd.Parameters.AddWithValue("@sc", L("applyScore"));
            cmd.Parameters.AddWithValue("@st", L("applyState"));
            cmd.Parameters.AddWithValue("@hp", N("heroPower"));
            cmd.Parameters.AddWithValue("@hr", N("thpRank"));
            cmd.Parameters.AddWithValue("@m", V("message"));
            if (V("uid").Length > 0) n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    /// <summary>Runs a query and returns its rows as name -> value, in column order.</summary>
    List<Dictionary<string, object?>> Q(NpgsqlConnection c, string sql, params (string Name, object Value)[] ps) =>
        Db.Rows(c, sql, [.. ps.Select(p => (p.Name, (object?)p.Value))]);

    static string Origin(string column) => string.Format(OriginSql, column);
}
