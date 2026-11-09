using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// Password-only access to the web UI, for handing the stats out to other people.
///
///   access_key      one password: a friendly name, a role (viewer = the Explorer and reading chat;
///                   admin = everything - runners, operations, commands, raw data), and its counters
///   access_session  a signed-in browser: only the SHA-256 of its cookie token is stored
///   access_usage    requests per key, per day, per area of the site
///
/// Passwords are generated here (22 characters from a 55-letter alphabet, about 127 bits) and shown
/// once; only their SHA-256 is kept. That is enough for random secrets of that size - there is
/// nothing to dictionary-attack - so no slow hash is needed. "Local development" is a key with no
/// password at all: the server uses it for requests from this machine when started with --dev.
/// </summary>
public sealed class AccessStore(string path)
{
    public const string DevKeyId = "local-dev";
    public sealed record Key(string Id, string Name, string Role, bool Disabled)
    {
        public bool IsAdmin => Role == "admin";
    }

    readonly ConcurrentDictionary<string, Key?> _sessions = new();                 // token hash -> key (null = unknown)
    readonly ConcurrentDictionary<(string Key, string Day, string Area), int> _pending = new();
    readonly ConcurrentDictionary<string, string> _seen = new();                  // key -> last request time

    NpgsqlConnection Open() => Db.Open(path);

    void Exec(string sql, params (string, object?)[] ps)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "AccessStore")) return;   // DDL once per process: it takes table locks
        Exec("""
            CREATE TABLE IF NOT EXISTS access_key (
                id TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL, role TEXT NOT NULL, secret_hash TEXT NOT NULL,
                created_at TEXT NOT NULL, disabled BIGINT NOT NULL DEFAULT 0, logins BIGINT NOT NULL DEFAULT 0,
                last_login_at TEXT NOT NULL DEFAULT '', last_seen_at TEXT NOT NULL DEFAULT '', requests BIGINT NOT NULL DEFAULT 0,
                last_ip TEXT NOT NULL DEFAULT '');
            CREATE INDEX IF NOT EXISTS ix_access_secret ON access_key(secret_hash);
            CREATE TABLE IF NOT EXISTS access_session (
                token_hash TEXT NOT NULL PRIMARY KEY, key_id TEXT NOT NULL, created_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL, ip TEXT NOT NULL DEFAULT '', agent TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS access_usage (
                key_id TEXT NOT NULL, day TEXT NOT NULL, area TEXT NOT NULL, count BIGINT NOT NULL,
                PRIMARY KEY (key_id, day, area));
            -- Favourite players, per signed-in password.
            CREATE TABLE IF NOT EXISTS access_favourite (
                key_id TEXT NOT NULL, uid TEXT NOT NULL, created_at TEXT NOT NULL,
                PRIMARY KEY (key_id, uid));
            """);
        // 2026-09-30: favourites are players, alliances or servers. uid holds the id of any of them
        // (player uid, alliance id, server number) - ponytail: they never collide, so the key stays (key_id, uid).
        Exec("ALTER TABLE access_favourite ADD COLUMN IF NOT EXISTS kind TEXT NOT NULL DEFAULT 'player';");
        // Per-password settings (the Account page): myUid = "my player", sendFrom = the account messages go out from.
        Exec("CREATE TABLE IF NOT EXISTS access_pref (key_id TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY (key_id, name));");
        // The local-development key: admin, never usable with a password.
        Exec("INSERT INTO access_key (id, name, role, secret_hash, created_at) VALUES (@i, 'Local development', 'admin', '', @t) ON CONFLICT DO NOTHING;",
            ("@i", DevKeyId), ("@t", Now()));
    }

    public static readonly string[] PrefNames = ["myUid", "sendFrom"];

    public Dictionary<string, string> Prefs(string keyId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, value FROM access_pref WHERE key_id = @k;";
        cmd.Parameters.AddWithValue("@k", keyId);
        using var r = cmd.ExecuteReader();
        var outp = new Dictionary<string, string>();
        while (r.Read()) outp[r.GetString(0)] = r.GetString(1);
        return outp;
    }

    /// <summary>Sets the known settings given; an empty value clears one. Unknown names are ignored.</summary>
    public void SetPrefs(string keyId, IReadOnlyDictionary<string, string?> prefs)
    {
        foreach (var (name, value) in prefs.Where(p => PrefNames.Contains(p.Key)))
            Exec(string.IsNullOrWhiteSpace(value)
                    ? "DELETE FROM access_pref WHERE key_id = @k AND name = @n;"
                    : "INSERT INTO access_pref (key_id, name, value) VALUES (@k, @n, @v) ON CONFLICT (key_id, name) DO UPDATE SET value = @v;",
                ("@k", keyId), ("@n", name), ("@v", value?.Trim() ?? ""));
    }

    public static readonly string[] FavouriteKinds = ["player", "alliance", "server"];

    public void SetFavourite(string keyId, string uid, bool on, string kind = "player")
    {
        if (!FavouriteKinds.Contains(kind)) throw new ArgumentException($"kind must be one of {string.Join(", ", FavouriteKinds)}");
        Exec(on ? "INSERT INTO access_favourite (key_id, uid, created_at, kind) VALUES (@k, @u, @t, @kind) ON CONFLICT DO NOTHING;"
                : "DELETE FROM access_favourite WHERE key_id = @k AND uid = @u;",
            ("@k", keyId), ("@u", uid.Trim()), ("@t", Now()), ("@kind", kind));
    }

    /// <summary>This password's favourites (players, alliances, servers), newest first, with names from the *_latest tables.</summary>
    public List<Dictionary<string, object?>> Favourites(string keyId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT f.kind, f.uid, f.created_at AS at, pl.name, COALESCE(pl.tag, al.tag) AS tag,
                   CASE f.kind WHEN 'alliance' THEN f.uid ELSE pl.alliance_id END AS "allianceId",
                   CASE f.kind WHEN 'server' THEN CAST(f.uid AS BIGINT) ELSE COALESCE(pl.server, al.server) END AS server,
                   pl.hero, al.members
            FROM access_favourite f
            LEFT JOIN player_latest pl ON f.kind = 'player' AND pl.uid = f.uid
            LEFT JOIN alliance_latest al ON f.kind = 'alliance' AND al.alliance_id = f.uid
            WHERE f.key_id = @k ORDER BY f.created_at DESC;
            """;
        cmd.Parameters.AddWithValue("@k", keyId);
        using var r = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    static string Now() => DateTimeOffset.UtcNow.ToString("O");
    static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>xxxx-xxxx-... without look-alike characters (0/O, 1/l/I), so it survives being read out.</summary>
    public static string NewPassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = Enumerable.Range(0, 22).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray();
        return string.Join("-", new string(chars).Chunk(5).Select(c => new string(c)));
    }

    /// <summary>Is there an enabled admin that signs in with a password (the dev key does not count)?</summary>
    public bool HasPasswordAdmin()
    {
        using var c = Db.Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM access_key WHERE role = 'admin' AND disabled = 0 AND secret_hash <> '');";
        return cmd.ExecuteScalar() is true;
    }

    public (string Id, string Password) Create(string name, string role)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var password = NewPassword();
        Exec("INSERT INTO access_key (id, name, role, secret_hash, created_at) VALUES (@i, @n, @r, @h, @t);",
            ("@i", id), ("@n", name.Trim()), ("@r", NormalRole(role)), ("@h", Sha(password)), ("@t", Now()));
        return (id, password);
    }

    // viewerplus (2026-10-07) = viewer + the Tracking page + reading battle reports.
    static string NormalRole(string role) => role is "admin" or "viewerplus" ? role : "viewer";

    /// <summary>A new password for an existing key; every browser signed in with the old one is signed out.</summary>
    public string? Reset(string id)
    {
        if (id == DevKeyId) return null;
        var password = NewPassword();
        Exec("UPDATE access_key SET secret_hash = @h WHERE id = @i;", ("@h", Sha(password)), ("@i", id));
        EndSessions(id);
        return password;
    }

    public void Update(string id, string? name, string? role, bool? disabled)
    {
        if (name is { Length: > 0 }) Exec("UPDATE access_key SET name = @n WHERE id = @i;", ("@n", name.Trim()), ("@i", id));
        if (role is not null && id != DevKeyId) Exec("UPDATE access_key SET role = @r WHERE id = @i;", ("@r", NormalRole(role)), ("@i", id));
        if (disabled is not null && id != DevKeyId) Exec("UPDATE access_key SET disabled = @d WHERE id = @i;", ("@d", disabled.Value ? 1 : 0), ("@i", id));
        EndSessions(id, onlyCache: disabled != true);
    }

    public void Delete(string id)
    {
        if (id == DevKeyId) return;
        Exec("DELETE FROM access_key WHERE id = @i;", ("@i", id));
        EndSessions(id);
    }

    void EndSessions(string id, bool onlyCache = false)
    {
        if (!onlyCache) Exec("DELETE FROM access_session WHERE key_id = @i;", ("@i", id));
        _sessions.Clear();   // re-read on the next request: role or name may have changed
    }

    /// <summary>Signs a password in: a new session token, or null for a wrong or disabled password.</summary>
    public (string Token, Key Key)? Login(string password, string ip, string agent)
    {
        if (string.IsNullOrWhiteSpace(password)) return null;
        var hash = Sha(password.Trim());
        Key? key;
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, role, disabled FROM access_key WHERE secret_hash = @h AND secret_hash <> '';";
            cmd.Parameters.AddWithValue("@h", hash);
            using var r = cmd.ExecuteReader();
            key = r.Read() ? new Key(r.GetString(0), r.GetString(1), r.GetString(2), (int)r.GetInt64(3) != 0) : null;
        }
        if (key is null || key.Disabled) return null;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = Now();
        Exec("INSERT INTO access_session VALUES (@t, @k, @n, @n, @ip, @a);", ("@t", Sha(token)), ("@k", key.Id), ("@n", now), ("@ip", ip), ("@a", agent));
        Exec("UPDATE access_key SET logins = logins + 1, last_login_at = @n, last_ip = @ip WHERE id = @k;", ("@n", now), ("@ip", ip), ("@k", key.Id));
        return (token, key);
    }

    public void Logout(string token) { Exec("DELETE FROM access_session WHERE token_hash = @t;", ("@t", Sha(token))); _sessions.TryRemove(Sha(token), out _); }

    /// <summary>The key behind a session cookie (cached; changes to keys clear the cache).</summary>
    public Key? Session(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var th = Sha(token);
        return _sessions.GetOrAdd(th, t =>
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT k.id, k.name, k.role, k.disabled FROM access_session s JOIN access_key k ON k.id = s.key_id
                WHERE s.token_hash = @t AND k.disabled = 0;
                """;
            cmd.Parameters.AddWithValue("@t", t);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new Key(r.GetString(0), r.GetString(1), r.GetString(2), false) : null;
        });
    }

    public Key? Get(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, role, disabled FROM access_key WHERE id = @i;";
        cmd.Parameters.AddWithValue("@i", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new Key(r.GetString(0), r.GetString(1), r.GetString(2), (int)r.GetInt64(3) != 0) : null;
    }

    /// <summary>Counts a request in memory; Flush writes the counts (every request writing would be wasteful).</summary>
    public void Touch(string keyId, string area)
    {
        _pending.AddOrUpdate((keyId, DateTime.UtcNow.ToString("yyyy-MM-dd"), area), 1, (_, n) => n + 1);
        _seen[keyId] = Now();
    }

    public void Flush()
    {
        if (_pending.IsEmpty) return;
        var batch = _pending.Keys.Select(k => (k, n: _pending.TryRemove(k, out var v) ? v : 0)).Where(x => x.n > 0).ToList();
        var seen = _seen.ToArray();
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        foreach (var ((key, day, area), n) in batch)
        {
            cmd.CommandText = """
                INSERT INTO access_usage VALUES (@k, @d, @a, @n) ON CONFLICT(key_id, day, area) DO UPDATE SET count = access_usage.count + @n;
                UPDATE access_key SET requests = requests + @n WHERE id = @k;
                """;
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@k", key); cmd.Parameters.AddWithValue("@d", day);
            cmd.Parameters.AddWithValue("@a", area); cmd.Parameters.AddWithValue("@n", n);
            cmd.ExecuteNonQuery();
        }
        foreach (var (key, at) in seen)
        {
            cmd.CommandText = "UPDATE access_key SET last_seen_at = @t WHERE id = @k AND last_seen_at < @t;";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@k", key); cmd.Parameters.AddWithValue("@t", at);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Every key with its counters, requests by area (all time and last 7 days), and its signed-in browsers.</summary>
    public List<Dictionary<string, object?>> List()
    {
        using var c = Open();
        List<Dictionary<string, object?>> Q(string sql, params (string, object)[] ps)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            using var r = cmd.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (r.Read())
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        var keys = Q("""
            SELECT id, name, role, disabled, created_at AS "createdAt", logins, NULLIF(last_login_at, '') AS "lastLoginAt",
                   NULLIF(last_seen_at, '') AS "lastSeenAt", requests, last_ip AS "lastIp", secret_hash = '' AS passwordless
            FROM access_key ORDER BY id = 'local-dev' DESC, created_at;
            """);
        var week = DateTime.UtcNow.AddDays(-6).ToString("yyyy-MM-dd");
        foreach (var k in keys)
        {
            var id = (string)k["id"]!;
            k["areas"] = Q("SELECT area, SUM(count) AS n, SUM(CASE WHEN day >= @w THEN count ELSE 0 END) AS week FROM access_usage WHERE key_id = @k GROUP BY area ORDER BY n DESC;",
                ("@k", id), ("@w", week));
            k["days"] = Q("SELECT day, SUM(count) AS n FROM access_usage WHERE key_id = @k GROUP BY day ORDER BY day DESC LIMIT 14;", ("@k", id));
            k["sessions"] = Q("SELECT created_at AS \"createdAt\", last_seen_at AS \"lastSeenAt\", ip, agent FROM access_session WHERE key_id = @k ORDER BY created_at DESC;", ("@k", id));
            k["disabled"] = Convert.ToInt64(k["disabled"]) != 0;
            k["passwordless"] = Convert.ToInt64(k["passwordless"]) != 0;
        }
        return keys;
    }

    /// <summary>Keeps a session's last-seen fresh at most once a minute per session.</summary>
    readonly ConcurrentDictionary<string, DateTime> _sessionTouched = new();
    public void TouchSession(string token, string ip)
    {
        var th = Sha(token);
        var now = DateTime.UtcNow;
        if (_sessionTouched.TryGetValue(th, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
        _sessionTouched[th] = now;
        Exec("UPDATE access_session SET last_seen_at = @n, ip = @ip WHERE token_hash = @t;", ("@n", Now()), ("@ip", ip), ("@t", th));
    }
}
