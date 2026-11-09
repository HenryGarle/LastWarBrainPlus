namespace LastWar.Storage.Postgres;

/// <summary>
/// Where the Postgres connection string comes from, in order: an explicit value, the LASTWAR_PG
/// environment variable, or the local docker-compose setup (POSTGRES_PASSWORD in LastWar\.env,
/// localhost:5432, user and database "lastwar"). The password never needs to be typed anywhere.
/// </summary>
public static class PgConfig
{
    /// <summary>The connection string, with the app's schema ("lastwar") on the search path unless one is given.</summary>
    public static string Resolve(string? explicitValue = null, string? root = null)
    {
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(Raw(explicitValue, root));
        if (string.IsNullOrEmpty(cs.SearchPath)) cs.SearchPath = "lastwar";
        return cs.ConnectionString;
    }

    static string Raw(string? explicitValue, string? root)
    {
        if (!string.IsNullOrWhiteSpace(explicitValue)) return explicitValue;
        if (Environment.GetEnvironmentVariable("LASTWAR_PG") is { Length: > 0 } env) return env;
        foreach (var dir in new[] { root is null ? null : Path.Combine(root, "LastWar"), root, Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (dir is null) continue;
            var file = Path.Combine(dir, ".env");
            if (!File.Exists(file)) continue;
            var pw = File.ReadAllLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("POSTGRES_PASSWORD="))?["POSTGRES_PASSWORD=".Length..];
            if (pw is { Length: > 0 })
                return $"Host=localhost;Port=5432;Username=lastwar;Password={pw};Database=lastwar;Command Timeout=0;Timeout=30";
        }
        throw new InvalidOperationException("no Postgres connection: set LASTWAR_PG, or POSTGRES_PASSWORD in LastWar\\.env");
    }
}
