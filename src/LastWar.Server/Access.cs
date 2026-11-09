using System.Collections.Concurrent;
using System.Net;
using LastWar.Storage;

namespace LastWar.Server;

/// <summary>
/// The web UI's gate (AccessStore holds the keys). Every /api request needs a signed-in browser,
/// except /api/auth/*. Viewers get the Explorer and reading chat; everything else - runners,
/// operations, commands, raw data, sessions, mail, chat follow/backfill, access - needs admin.
///
/// Local development: started with --dev true (or in the Development environment), a request
/// straight from this machine (loopback, no proxy header) is the "Local development" admin key, so
/// nothing asks for a password while testing and its usage is still counted like anyone's. A
/// reverse proxy on the same machine forwards with X-Forwarded-For, so it never gets that pass.
/// </summary>
public static class Access
{
    public const string Cookie = "lw_session";

    /// <summary>Which part of the site a request is for - the usage stats are counted by this.</summary>
    public static string Area(string path, string method) => path switch
    {
        _ when path.StartsWith("/api/x/rankings") => "rankings",
        _ when path.StartsWith("/api/x/compare") || path.StartsWith("/api/x/matchup") => "compare",
        _ when path.StartsWith("/api/x/search") => "search",
        _ when path.StartsWith("/api/x") => "explorer",
        _ when path.StartsWith("/api/chat/stats") => "chat stats",
        _ when path.StartsWith("/api/chat/search") => "chat search",
        _ when path.StartsWith("/api/chat") => "chat",
        _ when path.Contains("/operation") => "operations",
        _ when path.Contains("/call") || path.StartsWith("/api/commands") => "commands",
        _ when path.Contains("/mail") => "mail",
        _ when path.StartsWith("/api/db") => "raw data",
        _ when path.StartsWith("/api/access") => "access",
        _ when path.StartsWith("/api/schedule") => "scheduled",
        _ when path.StartsWith("/api/runners") || path.StartsWith("/api/sessions") => "fleet",
        _ when path.StartsWith("/api/game") => "game data",
        _ => "other",
    };

    /// <summary>What a viewer may do: read the Explorer and chat (and translate a message).</summary>
    public static bool ViewerMay(string path, string method, string role = "viewer")
    {
        // Viewer plus: the Tracking page (read and set) and battle reports, read only.
        if (role == "viewerplus")
        {
            if (path == "/api/servers/track") return method is "GET" or "PUT";
            if (path == "/api/reports" || path == "/api/reports/analysis" || (path.StartsWith("/api/reports/") && path.Count(ch => ch == '/') == 3)) return method == "GET";
        }
        if (path.StartsWith("/api/x/") || path.StartsWith("/api/game/") || path == "/api/svs" || path == "/api/svs/setups" || path == "/api/svs/predict" || path == "/api/svs/observations") return method == "GET";
        if (path.StartsWith("/api/me/favourites")) return method is "GET" or "PUT" or "DELETE";   // their own list only
        if (path == "/api/chat/rooms" || path == "/api/chat/status" || path == "/api/chat/live" || path == "/api/chat/search"
            || path == "/api/chat/stats" || path == "/api/chat/backfill") return method == "GET";
        if (path.StartsWith("/api/chat/rooms/") && path.EndsWith("/messages")) return method == "GET";
        if (path.StartsWith("/api/chat/rooms/") && path.EndsWith("/translate")) return method == "POST";
        return false;
    }

    static readonly ConcurrentDictionary<string, (int Fails, DateTime Since)> Failures = new();

    /// <summary>At most 10 wrong passwords per address per 10 minutes.</summary>
    public static bool Throttled(string ip) =>
        Failures.TryGetValue(ip, out var f) && f.Fails >= 10 && DateTime.UtcNow - f.Since < TimeSpan.FromMinutes(10);

    public static void Failed(string ip) =>
        Failures.AddOrUpdate(ip, (1, DateTime.UtcNow), (_, f) => DateTime.UtcNow - f.Since > TimeSpan.FromMinutes(10) ? (1, DateTime.UtcNow) : (f.Fails + 1, f.Since));

    public static bool IsLocalDirect(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip) && !ctx.Request.Headers.ContainsKey("X-Forwarded-For");

    public static string Ip(HttpContext ctx) =>
        ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "";

    /// <summary>The key for this request: its session cookie, else the dev key for a local request in dev mode.</summary>
    public static AccessStore.Key? Who(HttpContext ctx, AccessStore store, bool dev)
    {
        if (ctx.Request.Cookies.TryGetValue(Cookie, out var token) && store.Session(token) is { } key)
        {
            store.TouchSession(token, Ip(ctx));
            return key;
        }
        return dev && IsLocalDirect(ctx) ? store.Get(AccessStore.DevKeyId) : null;
    }
}
