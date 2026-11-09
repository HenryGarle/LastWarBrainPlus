namespace LastWar.Storage;

/// <summary>The event calendar pages: the events, and who showed a sign of life during one.</summary>
public sealed partial class Insights
{
    /// <summary>Events longer than this are campaign windows (a 3-week war): no attendance for them.</summary>
    static readonly TimeSpan AttendanceMax = TimeSpan.FromHours(6);

    static DateTimeOffset HourFloor(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero);
    static DateTimeOffset HourCeil(DateTime t) => HourFloor(t) == new DateTimeOffset(t, TimeSpan.Zero) ? HourFloor(t) : HourFloor(t).AddHours(1);

    /// <summary>uids with a sign of life in the event's hours (whole UTC hours: our evidence is hour-grained).</summary>
    HashSet<string> SeenDuring(Npgsql.NpgsqlConnection c, EventCalendar.GameEvent e, IEnumerable<string>? uids) =>
        OnlineHours(c, HourFloor(e.Start), uids, HourCeil(e.End)).Keys.ToHashSet();

    public Dictionary<string, object?> Calendar(DateTime? from, DateTime? to, string? kind)
    {
        var cal = new EventCalendar(path);
        return new()
        {
            ["kinds"] = EventCalendar.Kinds,
            ["events"] = cal.List(from, to, kind).Select(EventJson).ToList(),
        };
    }

    static Dictionary<string, object?> EventJson(EventCalendar.GameEvent e) => new()
    {
        ["id"] = e.Id, ["kind"] = e.Kind, ["title"] = e.Title, ["start"] = e.Start.ToString("O"), ["end"] = e.End.ToString("O"),
        ["servers"] = e.Servers, ["round"] = e.Round, ["source"] = e.Source, ["detail"] = e.Detail,
    };

    /// <summary>One event and everyone on its servers who showed a sign of life during it, strongest first.</summary>
    public Dictionary<string, object?> CalendarEvent(string id)
    {
        var e = new EventCalendar(path).Get(id);
        if (e is null) return new() { ["found"] = false };
        var outp = new Dictionary<string, object?> { ["found"] = true, ["event"] = EventJson(e) };
        if (e.End - e.Start > AttendanceMax) { outp["note"] = "A campaign window, not one fight: no attendance."; return outp; }
        if (e.Start > DateTime.UtcNow) { outp["note"] = "Not started yet."; return outp; }
        using var c = Open();
        var servers = e.Servers.Select(s => (long)s).ToArray();
        var players = Q(c, $"""
            SELECT pl.uid, pl.name, pl.tag, pl.alliance_id AS "allianceId", pl.server, pl.hero AS est, pl.power, {F(LatestHero, "pl.uid")} AS board
            FROM player_latest pl WHERE pl.server = ANY(@s);
            """, ("@s", servers));
        var seen = SeenDuring(c, e, players.Select(p => (string)p["uid"]!));
        var rows = players.Where(p => seen.Contains((string)p["uid"]!)).Select(p =>
        {
            p["hero"] = p["board"] ?? p["est"];
            p["exact"] = p["board"] is not null;
            p.Remove("board"); p.Remove("est");
            return p;
        }).OrderByDescending(p => p["hero"] is null ? 0 : Convert.ToInt64(p["hero"])).ToList();
        outp["seen"] = rows;
        outp["perServer"] = e.Servers.Select(s => new Dictionary<string, object?>
        {
            ["server"] = s,
            ["known"] = players.Count(p => Convert.ToInt32(p["server"]) == s),
            ["seen"] = rows.Count(p => Convert.ToInt32(p["server"]) == s),
        }).ToList();
        var first = Q(c, "SELECT MIN(taken_at) AS at FROM player_profile;").FirstOrDefault()?["at"] as string;
        if (first is null || string.CompareOrdinal(first, e.End.ToString("O")) > 0) outp["note"] = "Before our first profile reading: nothing to compare.";
        else outp["note"] = "Seen = any sign of life (login, donation, chat) in the event's UTC hours. Our readings are a floor: many who played are missed.";
        return outp;
    }

    /// <summary>For each uid: how many of the last past Meteorite round-N events they were seen in, and of how many.</summary>
    Dictionary<string, (int R1, int R2)> MeteoriteHistory(Npgsql.NpgsqlConnection c, IReadOnlyCollection<string> uids, out int r1Of, out int r2Of)
    {
        var first = Q(c, "SELECT MIN(taken_at) AS at FROM player_profile;").FirstOrDefault()?["at"] as string;
        var firstAt = first is null ? DateTime.MaxValue : DateTime.Parse(first, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var past = new EventCalendar(path).List(DateTime.UtcNow.AddDays(-56), DateTime.UtcNow, "meteorite")
            .Where(e => e.End <= DateTime.UtcNow && e.Start >= firstAt).ToList();
        r1Of = past.Count(e => e.Round == 1); r2Of = past.Count(e => e.Round == 2);
        var outp = new Dictionary<string, (int, int)>();
        foreach (var e in past)
            foreach (var u in SeenDuring(c, e, uids))
            {
                var (a, b) = outp.GetValueOrDefault(u);
                outp[u] = e.Round == 1 ? (a + 1, b) : (a, b + 1);
            }
        return outp;
    }
}
