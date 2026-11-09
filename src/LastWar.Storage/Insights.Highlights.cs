using System.Collections.Concurrent;

namespace LastWar.Storage;

public sealed partial class Insights
{
    static readonly ConcurrentDictionary<(string, int, string, int), (DateTime At, object Value)> HighlightCache = new();

    /// <summary>
    /// What moved over the last N days, per player: each player's newest profile (player_latest)
    /// against their newest profile AT OR BEFORE the start of the period. When the data does not
    /// reach back that far, the start is our first full sweep - the closest we can measure - and
    /// the result says so ("short"). A baseline value of 0 is a field the old sweeps did not
    /// record, not a real zero, so that player is left out of that measure: otherwise their whole
    /// kill count would read as "killed this week".
    ///   dhero   estimated hero power gained     dtroops  troop power lost (fights, hospital overflow)
    ///   dkills  troops killed                   dpower   total power lost
    /// </summary>
    // Per player, built once per request into a temp table (it reads every profile), then listed.
    const string PlayerMoves = """
        DROP TABLE IF EXISTS hl_d;
        CREATE TEMP TABLE hl_d AS
        WITH t AS (
            SELECT DISTINCT ON (uid) uid, power, army_power, army_kill, taken_at
            FROM player_profile WHERE taken_at <= @cut ORDER BY uid, taken_at DESC
        ),
        d AS (
            SELECT pl.uid, pl.name, pl.tag, pl.alliance_id AS "allianceId", pl.server, pl.hero, t.taken_at AS since,
                   CASE WHEN t.power > 0 AND pl.power > pl.troops
                        THEN pl.hero - CAST(GREATEST(t.power - t.army_power, 0) * pl.hero::numeric / (pl.power - pl.troops) AS BIGINT) END AS dhero,
                   CASE WHEN t.power > 0 THEN pl.power - t.power END AS dpower,
                   CASE WHEN t.army_power > 0 AND pl.troops > 0 THEN pl.troops - t.army_power END AS dtroops,
                   CASE WHEN t.army_kill > 0 AND pl.kills >= t.army_kill THEN pl.kills - t.army_kill END AS dkills
            FROM player_latest pl JOIN t ON t.uid = pl.uid
            WHERE pl.profiled_at IS NOT NULL AND pl.profiled_at > t.taken_at
        )
        SELECT * FROM d;
        """;

    const string Moves = """
        WITH d AS (SELECT * FROM hl_d),
        a AS (SELECT "allianceId", MAX(tag) AS tag, MODE() WITHIN GROUP (ORDER BY server) AS server, COUNT(*) AS members,
                     SUM(hero) AS hero, SUM(dhero) AS dhero, SUM(dtroops) AS dtroops, SUM(dkills) AS dkills, SUM(dpower) AS dpower
              FROM d WHERE "allianceId" <> '' GROUP BY "allianceId"),
        s AS (SELECT server, COUNT(*) AS members, SUM(hero) AS hero, SUM(dhero) AS dhero, SUM(dtroops) AS dtroops, SUM(dkills) AS dkills, SUM(dpower) AS dpower
              FROM d GROUP BY server)
        """;

    /// <summary>Every list the dashboard shows, and what each is ordered by.</summary>
    static readonly Dictionary<string, (string From, string Order, string Where)> ChangeKinds = new()
    {
        ["hero"] = ("d", "dhero DESC", "dhero > 0"),
        ["troops"] = ("d", "dtroops", "dtroops < 0"),
        ["kills"] = ("d", "dkills DESC", "dkills > 0"),
        ["power"] = ("d", "dpower", "dpower < 0"),
        ["allianceHero"] = ("a", "dhero DESC", "dhero > 0"),
        ["allianceTroops"] = ("a", "dtroops", "dtroops < 0"),
        ["allianceKills"] = ("a", "dkills DESC", "dkills > 0"),
        ["serverHero"] = ("s", "dhero DESC NULLS LAST", "true"),
        ["serverTroops"] = ("s", "dtroops NULLS LAST", "true"),
    };

    (string Cut, bool Short, string? First) PeriodStart(Npgsql.NpgsqlConnection c, int days)
    {
        var first = Q(c, "SELECT MIN(taken_at) AS t FROM player_profile WHERE source = 'sweep';")[0]["t"] as string;
        var cut = DateTimeOffset.UtcNow.AddDays(-days).ToString("O");
        var isShort = first is not null && string.CompareOrdinal(cut, first) < 0;
        return (isShort ? first! : cut, isShort, first);
    }

    void BuildMoves(Npgsql.NpgsqlConnection c, string cut)
    {
        using var cmd = Db.Command(c, PlayerMoves, ("@cut", cut));
        cmd.CommandTimeout = 120;
        cmd.ExecuteNonQuery();
    }

    List<Dictionary<string, object?>> ChangeRows(Npgsql.NpgsqlConnection c, string kind, string cut, int limit)
    {
        var (from, order, where) = ChangeKinds[kind];
        var cols = from switch
        {
            "d" => "uid, name, tag, \"allianceId\", server, hero, dhero, dtroops, dkills, dpower, since",
            "a" => "\"allianceId\", tag, server, members, hero, dhero, dtroops, dkills, dpower",
            _ => "server, members, hero, dhero, dtroops, dkills, dpower",
        };
        return Q(c, $"{Moves} SELECT '{kind}' AS kind, {cols} FROM {from} WHERE {where} ORDER BY {order} LIMIT {Math.Clamp(limit, 1, 1000)};");
    }

    /// <summary>The landing page's callouts: the top few of every list. Cached for ten minutes.</summary>
    public Dictionary<string, object?> Highlights(int days = 7)
    {
        days = Math.Clamp(days, 1, 90);
        if (HighlightCache.TryGetValue((path, days, "", 0), out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10))
            return (Dictionary<string, object?>)hit.Value;
        using var c = Open();
        var (cut, isShort, first) = PeriodStart(c, days);
        if (first is null) return new() { ["days"] = days, ["players"] = new List<object>(), ["groups"] = new List<object>() };
        BuildMoves(c, cut);
        var lists = ChangeKinds.Keys.ToDictionary(k => k, k => ChangeRows(c, k, cut, k.StartsWith("server") || k.StartsWith("alliance") ? 6 : 8));
        var value = new Dictionary<string, object?>
        {
            ["days"] = days, ["from"] = cut, ["to"] = DateTimeOffset.UtcNow.ToString("O"), ["short"] = isShort,
            ["players"] = lists.Where(l => ChangeKinds[l.Key].From == "d").SelectMany(l => l.Value).ToList(),
            ["groups"] = lists.Where(l => ChangeKinds[l.Key].From != "d").SelectMany(l => l.Value).ToList(),
        };
        HighlightCache[(path, days, "", 0)] = (DateTime.UtcNow, value);
        return value;
    }

    /// <summary>One of those lists in full (the page behind each callout). Cached for ten minutes.</summary>
    public Dictionary<string, object?> Changes(string kind, int days = 7, int limit = 200)
    {
        if (!ChangeKinds.ContainsKey(kind)) kind = "troops";
        days = Math.Clamp(days, 1, 90);
        if (HighlightCache.TryGetValue((path, days, kind, limit), out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10))
            return (Dictionary<string, object?>)hit.Value;
        using var c = Open();
        var (cut, isShort, first) = PeriodStart(c, days);
        List<Dictionary<string, object?>> Build() { BuildMoves(c, cut); return ChangeRows(c, kind, cut, limit); }
        var value = new Dictionary<string, object?>
        {
            ["kind"] = kind, ["days"] = days, ["from"] = cut, ["short"] = isShort,
            ["kinds"] = ChangeKinds.Keys.ToList(),
            ["rows"] = first is null ? new List<Dictionary<string, object?>>() : Build(),
        };
        HighlightCache[(path, days, kind, limit)] = (DateTime.UtcNow, value);
        return value;
    }
}
