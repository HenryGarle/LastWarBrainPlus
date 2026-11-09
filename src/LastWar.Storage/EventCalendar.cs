using System.Globalization;

namespace LastWar.Storage;

/// <summary>
/// Important game events with their times, kept for good so activity can be lined up against them
/// later ("who was on for last week's Meteorite round 1 - they will probably turn up again").
///
///   game_event   one row per event (a Meteorite round, a Rampage Boss spawn, an SvS / war window,
///                a season battle added by hand). id is stable per occurrence, so re-seeding is safe.
///
/// Attendance is not stored: it is worked out on demand from the evidence tables (profile last seen,
/// donations, chat - see Insights.OnlineHours), which are themselves kept for good.
/// source: schedule (from the game's texts/tables, recurring), table (a game table row), manual (admin).
/// A manual row is never overwritten by the schedule; the servers of an event that has started are frozen.
/// </summary>
public sealed class EventCalendar(string path)
{
    public static readonly Dictionary<string, string> Kinds = new()
    {
        ["meteorite"] = "Meteorite Iron War", ["rampage"] = "Rampage Boss", ["svs"] = "Warzone Duel (SvS)",
        ["war"] = "4 v 4 war", ["battle"] = "Season battle", ["defence"] = "Defence", ["attack"] = "Attack", ["custom"] = "Other",
    };

    public sealed record GameEvent(string Id, string Kind, string Title, DateTime Start, DateTime End, List<int> Servers, int Round, string Source, string Detail);

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "EventCalendar")) return;
        Db.Exec(path, """
            CREATE TABLE IF NOT EXISTS game_event (
                id TEXT PRIMARY KEY, kind TEXT NOT NULL, title TEXT NOT NULL, starts_at TEXT NOT NULL, ends_at TEXT NOT NULL,
                servers TEXT NOT NULL DEFAULT '', round BIGINT NOT NULL DEFAULT 0, source TEXT NOT NULL, detail TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_game_event_start ON game_event(starts_at);
            """);
    }

    static string Iso(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    static DateTime Parse(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    static List<int> Ids(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

    public void Upsert(GameEvent e)
    {
        EnsureSchema();
        // Schedule rows never replace a manual row; an event that has started keeps its servers.
        Db.Exec(path, """
            INSERT INTO game_event (id, kind, title, starts_at, ends_at, servers, round, source, detail, updated_at)
            VALUES (@id, @k, @t, @s, @e, @sv, @r, @src, @d, @now)
            ON CONFLICT (id) DO UPDATE SET title = EXCLUDED.title, starts_at = EXCLUDED.starts_at, ends_at = EXCLUDED.ends_at,
                servers = CASE WHEN game_event.starts_at <= @now THEN game_event.servers ELSE EXCLUDED.servers END,
                detail = EXCLUDED.detail, updated_at = EXCLUDED.updated_at
            WHERE game_event.source <> 'manual' OR EXCLUDED.source = 'manual';
            """, ("@id", e.Id), ("@k", e.Kind), ("@t", e.Title), ("@s", Iso(e.Start)), ("@e", Iso(e.End)),
            ("@sv", string.Join(",", e.Servers.Distinct().Order())), ("@r", (long)e.Round), ("@src", e.Source), ("@d", e.Detail), ("@now", Iso(DateTime.UtcNow)));
    }

    public List<GameEvent> List(DateTime? from = null, DateTime? to = null, string? kind = null)
    {
        EnsureSchema();
        using var c = Db.Open(path);
        return Db.Rows(c, """
            SELECT * FROM game_event WHERE starts_at >= @f AND starts_at < @t AND (@k = '' OR kind = @k) ORDER BY starts_at;
            """, ("@f", Iso(from ?? DateTime.UnixEpoch)), ("@t", Iso(to ?? new DateTime(2100, 1, 1))), ("@k", kind ?? ""))
            .Select(Row).ToList();
    }

    public GameEvent? Get(string id)
    {
        EnsureSchema();
        using var c = Db.Open(path);
        return Db.Rows(c, "SELECT * FROM game_event WHERE id = @id;", ("@id", id)).Select(Row).FirstOrDefault();
    }

    public bool Delete(string id) { EnsureSchema(); return Db.Exec(path, "DELETE FROM game_event WHERE id = @id;", ("@id", id)) > 0; }

    /// <summary>Points the Meteorite rounds that have not started yet at these servers (the page's saved crater).</summary>
    public int SetUpcomingServers(string kind, IEnumerable<int> servers)
    {
        EnsureSchema();
        return Db.Exec(path, "UPDATE game_event SET servers = @sv, updated_at = @now WHERE kind = @k AND starts_at > @now AND source <> 'manual';",
            ("@sv", string.Join(",", servers.Distinct().Order())), ("@k", kind), ("@now", Iso(DateTime.UtcNow)));
    }

    static GameEvent Row(Dictionary<string, object?> r) => new((string)r["id"]!, (string)r["kind"]!, (string)r["title"]!,
        Parse((string)r["starts_at"]!), Parse((string)r["ends_at"]!), Ids((string)r["servers"]!), Convert.ToInt32(r["round"]), (string)r["source"]!, (string)r["detail"]!);

    public sealed record Pool(DateTime Open, DateTime Close, string Groups);
    public sealed record War(long Id, long Type, DateTime Open, DateTime End, string Servers, string Season);

    /// <summary>
    /// The events we can work out from the game's schedule between <paramref name="from"/> and <paramref name="to"/>:
    /// Meteorite rounds (Saturday 00:10 and 12:30 UTC, 30 min) in weeks when a pool holding <paramref name="home"/>
    /// is on - or, past the end of the pool table, upcoming Saturdays marked expected; Rampage Boss spawns
    /// (Sunday 00:00 / 06:00 / 12:00 UTC); and every SvS / 4 v 4 war window involving one of <paramref name="ours"/>.
    /// </summary>
    public static List<GameEvent> Schedule(DateTime from, DateTime to, DateTime now, int home, List<int> crater,
        IReadOnlyList<Pool> pools, IReadOnlyList<War> wars, IReadOnlyCollection<int> ours)
    {
        var outp = new List<GameEvent>();
        var lastPool = pools.Count > 0 ? pools.Max(p => p.Close) : DateTime.MinValue;
        for (var d = from.Date; d <= to; d = d.AddDays(1))
        {
            var day = d.ToString("yyyy-MM-dd");
            if (d.DayOfWeek == DayOfWeek.Saturday)
            {
                var on = pools.Any(p => p.Open <= d && d < p.Close && ServerRegistry.Sides(p.Groups).Any(s => s.Contains(home)));
                var expected = !on && d >= lastPool && d >= now.Date;
                if (on || expected)
                    foreach (var (round, at) in new[] { (1, d.AddMinutes(10)), (2, d.AddHours(12).AddMinutes(30)) })
                        outp.Add(new($"meteorite:{day}:{round}", "meteorite", $"Meteorite Iron War round {round}", at, at.AddMinutes(30), crater, round, "schedule",
                            expected ? "expected: the game's pool table does not reach this week yet" : ""));
            }
            if (d.DayOfWeek == DayOfWeek.Sunday)
                foreach (var h in new[] { 0, 6, 12 })
                    outp.Add(new($"rampage:{day}:{h:00}", "rampage", $"Rampage Boss spawn {h:00}:00", d.AddHours(h), d.AddHours(h + 1), [home], h / 6 + 1, "schedule",
                        "spawn times from the game text (0:00, 6:00, 12:00 server time); Sunday from Henry - confirm in game"));
        }
        foreach (var w in wars.Where(w => w.Type is 4 or 8 && w.End >= from && w.Open <= to))
        {
            var servers = ServerRegistry.Sides(w.Servers).SelectMany(s => s).Distinct().ToList();
            if (!servers.Any(ours.Contains)) continue;
            var kind = w.Type == 4 ? "svs" : "war";
            outp.Add(new($"{kind}:{w.Id}", kind, $"{Kinds[kind]} {w.Season}".Trim() + ": " + w.Servers.Replace(";", ",").Replace("|", " vs "),
                w.Open, w.End, servers, 0, "table", "window from the game's war table (lw_zone_war)"));
        }
        return outp;
    }

    /// <summary>Re-seeds the schedule from 6 weeks back to 2 weeks ahead. Safe to repeat.</summary>
    public int Seed(ServerRegistry reg, DateTime now)
    {
        EnsureSchema();
        reg.EnsureSchema();
        static DateTime? D(object? v) => DateTime.TryParse(v as string, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
        using var c = Db.Open(path);
        var pools = Db.Rows(c, "SELECT open_time, close_time, group_list FROM game_event_group;")
            .Where(r => D(r["open_time"]) is not null && D(r["close_time"]) is not null)
            .Select(r => new Pool(D(r["open_time"])!.Value, D(r["close_time"])!.Value, (string?)r["group_list"] ?? "")).ToList();
        var wars = Db.Rows(c, "SELECT id, type, open_time, end_time, server_ids, season_tag FROM game_zone_war WHERE type IN (4, 8);")
            .Where(r => D(r["open_time"]) is not null && D(r["end_time"]) is not null)
            .Select(r => new War(Convert.ToInt64(r["id"]), Convert.ToInt64(r["type"]), D(r["open_time"])!.Value, D(r["end_time"])!.Value,
                (string?)r["server_ids"] ?? "", ((string?)r["season_tag"] ?? "").Split(',').ElementAtOrDefault(1) ?? "")).ToList();
        var ours = reg.Tracking().Where(t => t.Value.Level == "full").Select(t => t.Key).ToHashSet();
        var crater = reg.EventServers("meteorite") is { Count: > 0 } saved ? saved : [ServerRegistry.Home];
        var events = Schedule(now.AddDays(-42), now.AddDays(14), now, ServerRegistry.Home, crater, pools, wars, ours);
        foreach (var e in events) Upsert(e);
        return events.Count;
    }
}
