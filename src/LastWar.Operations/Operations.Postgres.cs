using LastWar.Storage.Postgres;

namespace LastWar.Operations;

public static partial class Operations
{
    /// <summary>
    /// Moving to Postgres without losing anything: copy a SQLite file (opened read-only) into a
    /// Postgres schema and prove every table matches, or just re-run the proof.
    /// </summary>
    static void AddPostgresOperations()
    {
        Add("db.postgres.copy", "Data", "Copy SQLite into Postgres",
            "Copies every table of a SQLite database into a Postgres schema with binary COPY, builds the same "
            + "keys and indexes, then checks row counts and per-column checksums on both sides. The SQLite file is "
            + "opened read-only. Point it at a stopped copy (a backup) for an exact snapshot.",
            c =>
            {
                var sqlite = c.Text("sqlite", Path.Combine(c.DataDir, "lastwar.db"));
                var schema = c.Text("schema", "lastwar");
                var copy = new SqliteToPostgres(sqlite, PgConfig.Resolve(c.Text("pg"), RootOf(c.DataDir)), schema, c.Report);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var checks = copy.Run(c.Flag("replace"), c.Cancellation);
                return Task.FromResult(Result(checks, $"{sqlite} -> Postgres schema {schema} in {sw.Elapsed.TotalMinutes:0.0} min"));
            },
            needsSession: false,
            ps:
            [
                new OperationParam("sqlite", "SQLite file", "text", "", Help: "Empty = data\\lastwar.db. A stopped backup copy gives an exact snapshot."),
                new OperationParam("schema", "Postgres schema", "text", "lastwar"),
                new OperationParam("replace", "Rebuild the schema if it has tables", "bool", "false"),
                new OperationParam("pg", "Connection string", "text", "", Help: "Empty = LASTWAR_PG, else LastWar\\.env (local docker)."),
            ]);

        Add("db.postgres.verify", "Data", "Check Postgres against SQLite",
            "Recomputes row counts and per-column checksums on a SQLite file and the Postgres copy and lists any difference.",
            c =>
            {
                var sqlite = c.Text("sqlite", Path.Combine(c.DataDir, "lastwar.db"));
                var schema = c.Text("schema", "lastwar");
                var checks = new SqliteToPostgres(sqlite, PgConfig.Resolve(c.Text("pg"), RootOf(c.DataDir)), schema, c.Report).Verify();
                return Task.FromResult(Result(checks, $"{sqlite} vs Postgres schema {schema}"));
            },
            needsSession: false,
            ps:
            [
                new OperationParam("sqlite", "SQLite file", "text", ""),
                new OperationParam("schema", "Postgres schema", "text", "lastwar"),
                new OperationParam("pg", "Connection string", "text", ""),
            ]);
    }

    static string? RootOf(string dataDir) => Path.GetDirectoryName(dataDir);   // <root>\data

    static OperationResult Result(List<SqliteToPostgres.Check> checks, string what)
    {
        var bad = checks.Where(x => !x.Same).ToList();
        var perTable = checks.GroupBy(x => x.Table).Select(g => new object?[]
        {
            g.Key, g.First(x => x.What == "rows").Sqlite, g.First(x => x.What == "rows").Postgres, g.Count(), g.Count(x => !x.Same),
        }).ToList();
        var summary = bad.Count == 0
            ? $"{what}: all {checks.Count:N0} checks identical across {perTable.Count} tables"
            : $"{what}: {bad.Count} of {checks.Count:N0} checks DIFFER - do not switch over";
        return new OperationResult(summary, [], [])
        {
            Tables =
            [
                OperationTable.Of("Tables", ["table", "rows (SQLite)", "rows (Postgres)", "checks", "differences"], perTable),
                OperationTable.Of("Differences", ["table", "check", "SQLite", "Postgres"], bad.Select(x => new object?[] { x.Table, x.What, x.Sqlite, x.Postgres })),
            ],
        };
    }
}
