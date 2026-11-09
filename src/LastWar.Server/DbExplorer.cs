using System.Text.Json.Nodes;
using Npgsql;

namespace LastWar.Server;

/// <summary>
/// Read-only window onto the collected data. Every query runs inside a READ ONLY transaction
/// that is rolled back, which is what actually guarantees it cannot write - the statement check
/// on top of it is there to give a clear error. A 60 s statement timeout stops a runaway query.
/// </summary>
public sealed class DbExplorer(string db)
{
    public const int MaxRows = 2000;

    NpgsqlConnection Open() => LastWar.Storage.Db.Open(db);

    public IEnumerable<object> Tables()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT t.table_name, (SELECT string_agg(k.column_name, ',' ORDER BY k.ordinal_position) FROM information_schema.columns k
                                  WHERE k.table_schema = t.table_schema AND k.table_name = t.table_name) AS cols
            FROM information_schema.tables t WHERE t.table_schema = current_schema() AND t.table_type = 'BASE TABLE' ORDER BY 1;
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read())
            list.Add(new { name = r.GetString(0), columns = r.IsDBNull(1) ? "" : r.GetString(1) });
        return list;
    }

    public object Count(string table)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\";";
        return new { table, rows = Convert.ToInt64(cmd.ExecuteScalar()) };
    }

    public JsonObject Query(string sql, int limit)
    {
        var trimmed = sql.Trim().TrimEnd(';').Trim();
        if (trimmed.Length == 0) throw new ArgumentException("empty query");
        if (trimmed.Contains(';')) throw new ArgumentException("one statement at a time");
        if (!trimmed.StartsWith("select", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("with", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("only SELECT and WITH queries are allowed");

        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var ro = c.CreateCommand())
        {
            ro.Transaction = tx;
            ro.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '60s';";
            ro.ExecuteNonQuery();
        }
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT * FROM ({trimmed}) q LIMIT {Math.Clamp(limit, 1, MaxRows)};";
        using var r = cmd.ExecuteReader();

        var cols = new JsonArray();
        for (var i = 0; i < r.FieldCount; i++) cols.Add(r.GetName(i));

        var rows = new JsonArray();
        while (r.Read())
        {
            var row = new JsonArray();
            for (var i = 0; i < r.FieldCount; i++)
                row.Add(r.IsDBNull(i) ? null : JsonValue.Create(r.GetValue(i)?.ToString()));
            rows.Add(row);
        }
        return new JsonObject { ["columns"] = cols, ["rows"] = rows, ["count"] = rows.Count };
    }
}
