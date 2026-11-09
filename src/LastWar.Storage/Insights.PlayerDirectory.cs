using System.Collections.Concurrent;

namespace LastWar.Storage;

public sealed partial class Insights
{
    /// <summary>Filters for the all-servers player list (Explore > Players). Empty / null = no filter.</summary>
    public sealed record PlayerQuery(
        string? Q = null, string? Servers = null, string? Alliance = null, string? Country = null, string? Flag = null,
        int? HqMin = null, int? HqMax = null, long? HeroMin = null, long? PowerMin = null,
        string? Seen = null, string? Moved = null, string Sort = "hero", bool Desc = true, int Page = 0, int Size = 100);

    // Sort keys the page offers -> SQL (a whitelist: never a column name from the request).
    static readonly Dictionary<string, string> PlayerSorts = new()
    {
        ["hero"] = "pl.hero", ["power"] = "pl.power", ["kills"] = "pl.kills", ["hq"] = "pl.level", ["name"] = "lower(pl.name)",
        ["server"] = "pl.server", ["seen"] = "NULLIF(pa.last_active_at, '')", ["country"] = "pl.country", ["flag"] = "pl.flag",
        ["tag"] = "lower(NULLIF(pl.tag, ''))", ["vip"] = "pl.svip",
    };

    static readonly ConcurrentDictionary<string, (DateTime At, object Value)> PlayerFacetCache = new();
    const string Tracked = "(SELECT server_id FROM server_track WHERE level = 'full')";

    /// <summary>
    /// Every player we know (player_latest: one row per uid, home server, newest profile's numbers) with activity
    /// (player_activity.last_active_at: login, donation or chat), filtered, sorted and one page of it. Also the total
    /// and the countries / flags / servers there are, for the page's drop-downs.
    /// </summary>
    public Dictionary<string, object?> PlayerDirectory(PlayerQuery f)
    {
        using var c = Open();
        var where = new List<string>();
        var ps = new List<(string Name, object Value)>();
        void P(string name, object v) => ps.Add((name, v));
        if (f.Q is { Length: > 0 } q)
        {
            where.Add("(pl.name ILIKE @q OR pl.uid = @qe)");
            P("@q", "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            P("@qe", q.Trim());
        }
        if (f.Servers is { Length: > 0 } sv)
        {
            var ids = ParseServerList(sv);
            if (ids.Count > 0) { where.Add("pl.server = ANY(@sv)"); P("@sv", ids.Select(x => (long)x).ToArray()); }
        }
        // No servers asked for = the tracked ones only: players seen on a capitol / visit / probe from far-off servers
        // (#600s, #800s...) are a handful each and only clutter the list. Naming a server still finds them.
        if (!where.Any(w => w.StartsWith("pl.server = ANY"))) where.Add($"pl.server IN {Tracked}");
        if (f.Alliance is { Length: > 0 } al)
        {
            if (al == "none") where.Add("pl.tag = ''");
            else { where.Add("lower(pl.tag) = lower(@al)"); P("@al", al.Trim()); }
        }
        if (f.Country is { Length: > 0 } co) { where.Add(co == "unknown" ? "pl.country IS NULL" : "pl.country = @co"); P("@co", co); }
        if (f.Flag is { Length: > 0 } fl) { where.Add(fl == "unknown" ? "pl.flag IS NULL" : "pl.flag = @fl"); P("@fl", fl); }
        if (f.HqMin is { } h1) { where.Add("pl.level >= @h1"); P("@h1", (long)h1); }
        if (f.HqMax is { } h2) { where.Add("pl.level <= @h2"); P("@h2", (long)h2); }
        if (f.HeroMin is { } hm) { where.Add("pl.hero >= @hm"); P("@hm", hm); }
        if (f.PowerMin is { } pm) { where.Add("pl.power >= @pm"); P("@pm", pm); }
        // Last seen = newest login, donation or chat (player_activity.last_active_at).
        switch (f.Seen)
        {
            case "1d": case "3d": case "7d": case "30d":
                where.Add($"pa.last_active_at >= {Db.AgoIso($"{f.Seen[..^1]} days")}"); break;
            case "not7d": where.Add($"(pa.last_active_at IS NULL OR pa.last_active_at = '' OR pa.last_active_at < {Db.AgoIso("7 days")})"); break;
            case "not30d": where.Add($"(pa.last_active_at IS NULL OR pa.last_active_at = '' OR pa.last_active_at < {Db.AgoIso("30 days")})"); break;
            case "never": where.Add("(pa.last_active_at IS NULL OR pa.last_active_at = '')"); break;
        }
        switch (f.Moved)
        {
            case "transferred": where.Add($"{Origin("pl.uid")} <> pl.server"); break;     // created on another server
            case "native": where.Add($"{Origin("pl.uid")} = pl.server"); break;
            case "visiting": where.Add("pl.current_server IS NOT NULL AND pl.current_server <> pl.server"); break;   // base on another server now
        }
        var sql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var from = "FROM player_latest pl LEFT JOIN player_activity pa ON pa.uid = pl.uid";
        var order = PlayerSorts.GetValueOrDefault(f.Sort, PlayerSorts["hero"]);
        var size = f.Size is 100 or 250 or 500 ? f.Size : 100;
        var page = Math.Max(0, f.Page);
        var args = ps.ToArray();
        var total = Convert.ToInt64(Q(c, $"SELECT count(*) AS n {from} {sql};", args)[0]["n"]);
        var rows = Q(c, $"""
            SELECT pl.uid, pl.name, pl.server, pl.alliance_id AS "allianceId", NULLIF(pl.tag, '') AS tag, pl.hero, pl.power, pl.kills,
                   pl.level AS hq, pl.country, pl.flag, pl.current_server AS "currentServer", pl.svip AS vip, {Origin("pl.uid")} AS origin,
                   NULLIF(pa.last_active_at, '') AS "lastSeen"
            {from} {sql}
            ORDER BY {order} {(f.Desc ? "DESC" : "ASC")} NULLS LAST, pl.uid
            LIMIT {size} OFFSET {page * size};
            """, args);
        return new()
        {
            ["total"] = total, ["page"] = page, ["size"] = size, ["rows"] = rows,
            ["facets"] = PlayerFacets(c),
        };
    }

    /// <summary>What the drop-downs can offer: countries, flags and home servers with player counts (cached 10 minutes).</summary>
    public object PlayerFacets() { using var c = Open(); return PlayerFacets(c); }

    object PlayerFacets(Npgsql.NpgsqlConnection c)
    {
        if (PlayerFacetCache.TryGetValue(path, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return hit.Value;
        var value = new Dictionary<string, object?>
        {
            ["countries"] = Q(c, "SELECT country AS code, count(*) AS n FROM player_latest WHERE country IS NOT NULL GROUP BY 1 ORDER BY 2 DESC;"),
            ["flags"] = Q(c, "SELECT flag AS code, count(*) AS n FROM player_latest WHERE flag IS NOT NULL GROUP BY 1 ORDER BY 2 DESC;"),
            ["servers"] = Q(c, $"SELECT server, count(*) AS n FROM player_latest WHERE server IN {Tracked} GROUP BY 1 ORDER BY 1;"),
        };
        PlayerFacetCache[path] = (DateTime.UtcNow, value);
        return value;
    }

    /// <summary>"1967", "1957-1988", "1967,1970-1972" -> server ids (at most 2,000).</summary>
    static List<int> ParseServerList(string spec)
    {
        var outp = new List<int>();
        foreach (var part in spec.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var ab = part.Split('-', 2);
            if (ab.Length == 2 && int.TryParse(ab[0], out var a) && int.TryParse(ab[1], out var b) && b >= a && b - a < 2000)
                outp.AddRange(Enumerable.Range(a, b - a + 1));
            else if (int.TryParse(part, out var one)) outp.Add(one);
        }
        return [.. outp.Distinct().Take(2000)];
    }
}
