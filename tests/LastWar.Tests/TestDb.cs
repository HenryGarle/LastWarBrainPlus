using LastWar.Storage;
using LastWar.Storage.Postgres;
using Npgsql;

namespace LastWar.Tests;

/// <summary>
/// A throwaway Postgres schema per test (the local docker database, or LASTWAR_PG): Create hands
/// back a connection string whose search path is the new schema, Drop removes it again.
/// </summary>
public static class TestDb
{
    static readonly string Admin = PgConfig.Resolve(null, Environment.GetEnvironmentVariable("LASTWAR_ROOT") ?? @"C:\Temp\LWC");

    public static string Create()
    {
        var schema = $"test_{Guid.NewGuid():N}";
        Db.Exec(Admin, $"CREATE SCHEMA {schema};");
        return new NpgsqlConnectionStringBuilder(Admin) { SearchPath = schema }.ConnectionString;
    }

    public static void Drop(string connectionString)
    {
        var schema = new NpgsqlConnectionStringBuilder(connectionString).SearchPath;
        Db.Forget(connectionString);
        if (schema is { Length: > 0 } && schema.StartsWith("test_")) Db.Exec(Admin, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
    }
}
