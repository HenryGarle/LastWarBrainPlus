using System.Text;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

namespace LastWar.Storage.Postgres;

/// <summary>
/// Copies every table of a SQLite database into a Postgres schema, then proves nothing was lost.
///
/// SQLite is opened READ-ONLY and never changed. Column types are chosen from what is actually
/// stored, not what was declared (SQLite lets any column hold any type): a column holding only
/// integers becomes bigint, integers and reals become double precision, anything holding text
/// becomes text (numbers in it are written the way SQLite itself prints them), blobs bytea. Primary
/// keys, AUTOINCREMENT ids and indexes come across. Rows go in with binary COPY.
///
/// Verification, per table, computed independently on both sides: row count, and per column the
/// non-null count plus a checksum - SUM for numbers, total character length for text, total bytes
/// for blobs. Any difference fails the copy.
/// </summary>
public sealed class SqliteToPostgres(string sqlitePath, string pgConnection, string schema, Action<string> report)
{
    public sealed record Column(string Name, string Declared, string PgType, bool NotNull, string? Default, int PkOrder, bool Identity, string Kinds);
    public sealed record Table(string Name, List<Column> Columns, List<string> Indexes, long Rows);
    public sealed record Check(string Table, string What, string Sqlite, string Postgres, bool Approx = false)
    {
        // Float sums may differ in the last bits (the engines add in different orders): 1 part in 10^12.
        public bool Same => Approx && double.TryParse(Sqlite, System.Globalization.CultureInfo.InvariantCulture, out var a)
                                   && double.TryParse(Postgres, System.Globalization.CultureInfo.InvariantCulture, out var b)
            ? Math.Abs(a - b) <= 1e-12 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)))
            : Sqlite == Postgres;
    }

    static string Q(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    SqliteConnection OpenSqlite()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        return c;
    }

    NpgsqlConnection OpenPg()
    {
        var c = new NpgsqlConnection(pgConnection);
        c.Open();
        using var cmd = new NpgsqlCommand($"SET search_path TO {Q(schema)}", c);
        try { cmd.ExecuteNonQuery(); } catch { /* schema may not exist yet */ }
        return c;
    }

    public List<Table> Plan()
    {
        using var s = OpenSqlite();
        var tables = new List<Table>();
        var names = new List<(string Name, string Sql)>();
        using (var cmd = s.CreateCommand())
        {
            cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        }
        foreach (var (name, sql) in names)
        {
            var cols = new List<(string Name, string Type, bool NotNull, string? Dflt, int Pk)>();
            using (var cmd = s.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({Q(name)});";
                using var r = cmd.ExecuteReader();
                while (r.Read()) cols.Add((r.GetString(1), r.GetString(2), r.GetInt32(3) == 1, r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5)));
            }
            // One pass over the table: what each column really holds.
            var probe = string.Join(", ", cols.Select(c =>
                $"SUM(typeof({Q(c.Name)})='integer'), SUM(typeof({Q(c.Name)})='real'), SUM(typeof({Q(c.Name)})='text'), SUM(typeof({Q(c.Name)})='blob'), "
                + $"MAX(CASE WHEN typeof({Q(c.Name)})='integer' THEN abs({Q(c.Name)}) END), SUM(typeof({Q(c.Name)})='text' AND instr({Q(c.Name)}, char(0)) > 0)"));
            long rows;
            var kinds = new List<(long I, long R, long T, long B, long MaxInt, long Nul)>();
            using (var cmd = s.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*), {probe} FROM {Q(name)};";
                using var r = cmd.ExecuteReader();
                r.Read();
                rows = r.GetInt64(0);
                long V(int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);
                for (var i = 0; i < cols.Count; i++)
                {
                    var o = 1 + i * 6;
                    kinds.Add((V(o), V(o + 1), V(o + 2), V(o + 3), V(o + 4), V(o + 5)));
                }
            }
            var columns = cols.Select((c, i) =>
            {
                var k = kinds[i];
                if (k.Nul > 0) throw new InvalidOperationException($"{name}.{c.Name}: {k.Nul} text value(s) contain a NUL character, which Postgres text cannot hold");
                if (k.B > 0 && k.I + k.R + k.T > 0) throw new InvalidOperationException($"{name}.{c.Name} mixes blobs with other values");
                var pg = Map(c.Type, k);
                // An INTEGER PRIMARY KEY is SQLite's rowid: it hands out ids whether or not AUTOINCREMENT was written.
                var identity = c.Pk == 1 && cols.Count(x => x.Pk > 0) == 1 && pg == "bigint" && c.Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);
                return new Column(c.Name, c.Type, pg, c.NotNull, c.Dflt, c.Pk, identity, $"int {k.I} real {k.R} text {k.T} blob {k.B}");
            }).ToList();

            var indexes = new List<string>();
            using (var cmd = s.CreateCommand())
            {
                cmd.CommandText = $"SELECT name, sql FROM sqlite_master WHERE type = 'index' AND tbl_name = $t AND sql IS NOT NULL ORDER BY name;";
                cmd.Parameters.AddWithValue("$t", name);
                using var r = cmd.ExecuteReader();
                while (r.Read()) indexes.Add(r.GetString(1));
            }
            tables.Add(new Table(name, columns, indexes, rows));
        }
        return tables;
    }

    /// <summary>What the column really holds decides; an empty or all-NULL column goes by its declaration.</summary>
    static string Map(string declared, (long I, long R, long T, long B, long MaxInt, long Nul) k) => (k.I, k.R, k.T, k.B) switch
    {
        ( > 0, 0, 0, 0) => "bigint",
        (_, > 0, 0, 0) => k.MaxInt > (1L << 53) ? "numeric" : "double precision",
        (0, 0, 0, > 0) => "bytea",
        (_, _, > 0, _) => "text",
        _ => Declared(declared),
    };

    static string Declared(string t)
    {
        t = t.ToUpperInvariant();
        return t.Contains("INT") ? "bigint" : t.Contains("CHAR") || t.Contains("TEXT") || t.Contains("CLOB") ? "text"
             : t.Contains("BLOB") ? "bytea" : t.Contains("REAL") || t.Contains("FLOA") || t.Contains("DOUB") ? "double precision" : "text";
    }

    /// <summary>SQLite default literal -> Postgres, only the forms this schema uses ('' / numbers / quoted text).</summary>
    static string? Default(Column c)
    {
        if (c.Default is null || c.Identity) return null;
        var d = c.Default.Trim();
        if (d.StartsWith('\'') || double.TryParse(d, System.Globalization.CultureInfo.InvariantCulture, out _))
            return c.PgType == "text" && !d.StartsWith('\'') ? $"'{d}'" : d;
        return null;   // expressions are left off and reported by the plan
    }

    /// <summary>A SQLite CREATE INDEX statement re-aimed at the target schema (column lists are plain here).</summary>
    string IndexSql(string sqliteSql)
    {
        var sql = sqliteSql.Replace("IF NOT EXISTS ", "", StringComparison.OrdinalIgnoreCase);
        var on = sql.IndexOf(" ON ", StringComparison.OrdinalIgnoreCase);
        var head = sql[..on];
        var rest = sql[(on + 4)..].TrimStart();
        var paren = rest.IndexOf('(');
        var table = rest[..paren].Trim().Trim('"');
        var parts = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ix = parts[^1].Trim('"');
        var unique = head.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ? "UNIQUE " : "";
        return $"CREATE {unique}INDEX {Q(ix)} ON {Q(schema)}.{Q(table)} {rest[paren..]}";
    }

    public string Ddl(Table t)
    {
        var sb = new StringBuilder($"CREATE TABLE {Q(schema)}.{Q(t.Name)} (\n");
        sb.Append(string.Join(",\n", t.Columns.Select(c =>
            $"  {Q(c.Name)} {c.PgType}{(c.Identity ? " GENERATED BY DEFAULT AS IDENTITY" : "")}{(c.NotNull || c.PkOrder > 0 ? " NOT NULL" : "")}"
            + (Default(c) is { } d ? $" DEFAULT {d}" : ""))));
        var pk = t.Columns.Where(c => c.PkOrder > 0).OrderBy(c => c.PkOrder).Select(c => Q(c.Name)).ToList();
        if (pk.Count > 0) sb.Append($",\n  PRIMARY KEY ({string.Join(", ", pk)})");
        sb.Append("\n);");
        return sb.ToString();
    }

    /// <summary>Creates the schema fresh (refuses to touch a non-empty one unless replace), copies, indexes, verifies.</summary>
    public List<Check> Run(bool replace, CancellationToken ct)
    {
        var plan = Plan();
        report($"plan: {plan.Count} tables, {plan.Sum(t => t.Rows):N0} rows");
        foreach (var t in plan)
            foreach (var c in t.Columns.Where(c => c.Default is not null && Default(c) is null && !c.Identity))
                report($"  note: {t.Name}.{c.Name} default {c.Default} not carried over");

        using (var pg = OpenPg())
        {
            using var exists = new NpgsqlCommand("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = $1", pg) { Parameters = { new() { Value = schema } } };
            var n = Convert.ToInt64(exists.ExecuteScalar());
            if (n > 0 && !replace) throw new InvalidOperationException($"schema {schema} already has {n} tables - pass replace=true to rebuild it from SQLite");
            using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {Q(schema)} CASCADE; CREATE SCHEMA {Q(schema)};", pg);
            drop.ExecuteNonQuery();
            foreach (var t in plan)
            {
                using var create = new NpgsqlCommand(Ddl(t), pg);
                create.ExecuteNonQuery();
            }
        }

        foreach (var t in plan)
        {
            ct.ThrowIfCancellationRequested();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CopyTable(t, ct);
            report($"copied {t.Name}: {t.Rows:N0} rows in {sw.Elapsed.TotalSeconds:0.0}s");
        }

        using (var pg = OpenPg())
        {
            foreach (var t in plan)
            {
                foreach (var ix in t.Indexes)
                {
                    using var cmd = new NpgsqlCommand(IndexSql(ix), pg);
                    cmd.ExecuteNonQuery();
                }
                var id = t.Columns.FirstOrDefault(c => c.Identity);
                if (id is not null)
                {
                    using var seq = new NpgsqlCommand(
                        $"SELECT setval(pg_get_serial_sequence('{Q(schema)}.{Q(t.Name)}', '{id.Name}'), GREATEST(COALESCE((SELECT MAX({Q(id.Name)}) FROM {Q(schema)}.{Q(t.Name)}), 0), 1))", pg);
                    seq.ExecuteNonQuery();
                }
            }
            report("indexes built");
            using var analyze = new NpgsqlCommand($"ANALYZE", pg);
            analyze.ExecuteNonQuery();
        }

        var checks = Verify(plan);
        var bad = checks.Where(c => !c.Same).ToList();
        report(bad.Count == 0 ? $"verified: {checks.Count} checks, all identical" : $"VERIFY FAILED: {bad.Count} of {checks.Count} checks differ");
        return checks;
    }

    void CopyTable(Table t, CancellationToken ct)
    {
        using var s = OpenSqlite();
        using var pg = OpenPg();
        // Text-bound columns are read through CAST so a number stored in them keeps SQLite's own spelling.
        var select = string.Join(", ", t.Columns.Select(c => c.PgType == "text" ? $"CAST({Q(c.Name)} AS TEXT)" : Q(c.Name)));
        using var cmd = s.CreateCommand();
        cmd.CommandText = $"SELECT {select} FROM {Q(t.Name)};";
        using var r = cmd.ExecuteReader();
        using var w = pg.BeginBinaryImport($"COPY {Q(schema)}.{Q(t.Name)} ({string.Join(", ", t.Columns.Select(c => Q(c.Name)))}) FROM STDIN (FORMAT BINARY)");
        long n = 0;
        while (r.Read())
        {
            w.StartRow();
            for (var i = 0; i < t.Columns.Count; i++)
            {
                if (r.IsDBNull(i)) { w.WriteNull(); continue; }
                switch (t.Columns[i].PgType)
                {
                    case "bigint": w.Write(r.GetInt64(i), NpgsqlDbType.Bigint); break;
                    case "double precision": w.Write(r.GetDouble(i), NpgsqlDbType.Double); break;
                    case "numeric": w.Write(Convert.ToDecimal(r.GetValue(i)), NpgsqlDbType.Numeric); break;
                    case "bytea": w.Write((byte[])r.GetValue(i), NpgsqlDbType.Bytea); break;
                    default: w.Write(r.GetString(i), NpgsqlDbType.Text); break;
                }
            }
            if (++n % 250_000 == 0) { report($"  {t.Name}: {n:N0}/{t.Rows:N0}"); ct.ThrowIfCancellationRequested(); }
        }
        w.Complete();
    }

    /// <summary>The same checksums worked out by each database on its own copy.</summary>
    public List<Check> Verify(List<Table>? plan = null)
    {
        plan ??= Plan();
        var checks = new List<Check>();
        using var s = OpenSqlite();
        using var pg = OpenPg();
        foreach (var t in plan)
        {
            var sParts = new List<string> { "COUNT(*)" };
            var pParts = new List<string> { "COUNT(*)" };
            var what = new List<string> { "rows" };
            var approx = new List<bool> { false };
            foreach (var c in t.Columns)
            {
                var col = Q(c.Name);
                sParts.Add($"COUNT({col})"); pParts.Add($"COUNT({col})"); what.Add($"{c.Name} non-null"); approx.Add(false);
                approx.Add(c.PgType is "double precision" or "numeric");
                switch (c.PgType)
                {
                    case "bigint":
                        sParts.Add($"CAST(SUM({col}) AS TEXT)"); pParts.Add($"CAST(SUM({col}) AS TEXT)"); what.Add($"{c.Name} sum"); break;
                    case "double precision" or "numeric":
                        sParts.Add($"TOTAL({col})"); pParts.Add($"COALESCE(SUM({col}::float8),0)"); what.Add($"{c.Name} sum"); break;
                    case "bytea":
                        sParts.Add($"TOTAL(length({col}))"); pParts.Add($"COALESCE(SUM(octet_length({col})),0)"); what.Add($"{c.Name} bytes"); break;
                    default:
                        sParts.Add($"TOTAL(length(CAST({col} AS TEXT)))"); pParts.Add($"COALESCE(SUM(length({col})),0)"); what.Add($"{c.Name} characters"); break;
                }
            }
            var sv = Row(s, $"SELECT {string.Join(", ", sParts)} FROM {Q(t.Name)};");
            var pv = Row(pg, $"SELECT {string.Join(", ", pParts)} FROM {Q(schema)}.{Q(t.Name)};");
            for (var i = 0; i < what.Count; i++) checks.Add(new Check(t.Name, what[i], Norm(sv[i]), Norm(pv[i]), approx[i]));
        }
        return checks;
    }

    static string Norm(object? v) => v switch
    {
        null or DBNull => "0",
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "0",
    };

    static object?[] Row(System.Data.Common.DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        using var r = cmd.ExecuteReader();
        r.Read();
        var vals = new object[r.FieldCount];
        r.GetValues(vals);
        return vals;
    }
}
