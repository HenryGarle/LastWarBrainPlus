using System.Collections.Concurrent;
using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// The one way into the database. Every store is handed a Postgres connection string (see
/// PgConfig) and opens connections here, so they share one pool per database.
///
/// Conventions the stores follow:
///   - timestamps are ISO-8601 text ("O" format, UTC). They sort and compare correctly as text,
///     which is what the SQLite era relied on, so the data came across unchanged.
///   - parameters are written @name.
///   - "insert if new" is INSERT ... ON CONFLICT DO NOTHING; "insert or replace" is an upsert on the
///     table's primary key.
/// </summary>
public static class Db
{
    static readonly ConcurrentDictionary<string, NpgsqlDataSource> Sources = new();

    // ---- SQL fragments for the ISO-text timestamps. '' (unknown) becomes NULL rather than an error.
    const string IsoFormat = "'YYYY-MM-DD\"T\"HH24:MI:SS'";

    /// <summary>Now (UTC) as ISO text to the second, e.g. 2026-09-28T15:00:00.</summary>
    public const string NowIso = $"to_char(now() AT TIME ZONE 'UTC', {IsoFormat})";

    /// <summary>ISO text for now minus an interval such as "7 days" or "1 day".</summary>
    public static string AgoIso(string interval) => $"to_char(now() AT TIME ZONE 'UTC' - interval '{interval}', {IsoFormat})";

    /// <summary>ISO text for an ISO-text expression minus an interval.</summary>
    public static string MinusIso(string isoExpr, string interval) =>
        $"to_char(NULLIF({isoExpr}, '')::timestamptz AT TIME ZONE 'UTC' - interval '{interval}', {IsoFormat})";

    /// <summary>The UTC hour (0-23) of an ISO-text expression.</summary>
    public static string HourOf(string isoExpr) => $"CAST(extract(hour FROM NULLIF({isoExpr}, '')::timestamptz AT TIME ZONE 'UTC') AS BIGINT)";

    /// <summary>Days (fractional) from ISO-text b to ISO-text a.</summary>
    public static string DaysBetween(string a, string b) =>
        $"(extract(epoch FROM NULLIF({a}, '')::timestamptz - NULLIF({b}, '')::timestamptz) / 86400.0)";

    static readonly ConcurrentDictionary<(string, string), bool> Done = new();

    /// <summary>
    /// True the first time a store asks for a database in this process. Schema DDL (even
    /// CREATE INDEX IF NOT EXISTS) takes table locks in Postgres, so EnsureSchema runs its DDL once
    /// at start-up rather than on every read that calls it.
    /// </summary>
    public static bool FirstTime(string connectionString, string store) => Done.TryAdd((connectionString, store), true);

    /// <summary>Closes and drops the pool for a database that is going away (test schemas).</summary>
    public static void Forget(string connectionString)
    {
        if (Sources.TryRemove(connectionString, out var s)) s.Dispose();
    }

    public static NpgsqlConnection Open(string connectionString) =>
        Sources.GetOrAdd(connectionString, cs => new NpgsqlDataSourceBuilder(cs).Build()).OpenConnection();

    public static int Exec(string connectionString, string sql, params (string Name, object? Value)[] ps)
    {
        using var c = Open(connectionString);
        using var cmd = Command(c, sql, ps);
        return cmd.ExecuteNonQuery();
    }

    public static NpgsqlCommand Command(NpgsqlConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 300 };
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    /// <summary>
    /// Bulk load with binary COPY - hundreds of times faster than row-by-row INSERTs for the
    /// rebuilt history tables. Values are text or whole numbers (int / long / bool become bigint).
    /// No ON CONFLICT here: use it for tables the caller has just emptied.
    /// </summary>
    public static int Copy(NpgsqlConnection c, string table, string[] columns, IEnumerable<object?[]> rows)
    {
        using var w = c.BeginBinaryImport($"COPY {table} ({string.Join(", ", columns)}) FROM STDIN (FORMAT BINARY)");
        var n = 0;
        foreach (var row in rows)
        {
            w.StartRow();
            foreach (var v in row)
                switch (v)
                {
                    case null: w.WriteNull(); break;
                    case string s: w.Write(s, NpgsqlTypes.NpgsqlDbType.Text); break;
                    case bool b: w.Write(b ? 1L : 0L, NpgsqlTypes.NpgsqlDbType.Bigint); break;
                    case int i: w.Write((long)i, NpgsqlTypes.NpgsqlDbType.Bigint); break;
                    case long l: w.Write(l, NpgsqlTypes.NpgsqlDbType.Bigint); break;
                    case double d: w.Write(d, NpgsqlTypes.NpgsqlDbType.Double); break;
                    default: throw new ArgumentException($"Db.Copy: {v.GetType().Name} is not handled");
                }
            n++;
        }
        w.Complete();
        return n;
    }

    /// <summary>Rows as column name -> value, in column order.</summary>
    public static List<Dictionary<string, object?>> Rows(NpgsqlConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Command(c, sql, ps);
        using var r = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>(r.FieldCount);
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
