using LastWar.Storage;

namespace LastWar.Tests;

public class MeteoriteTests
{
    // Top players mix the exact hero board with profile estimates for players off the board; the saved
    // server list is what the page opens on; the game's pool row that holds a picked server is listed.
    [Fact]
    public void RanksAcrossServersAndKeepsTheSavedList()
    {
        var path = TestDb.Create();
        try
        {
            new ChatStore(path).EnsureSchema();   // Insights indexes chat_message
            new Insights(path).EnsureSchema();
            var reg = new ServerRegistry(path);
            reg.SetEventServers("meteorite", [2, 1, 2]);
            Assert.Equal([1, 2], reg.EventServers("meteorite"));

            var at = "2026-09-29T10:00:00.0000000+00:00";
            void Board(long server, string uid, long v) => Db.Exec(path,
                "INSERT INTO board_sample (taken_at, server_id, board_type, rank, uid, value) VALUES (@t, @s, 13, 1, @u, @v);", ("@t", at), ("@s", server), ("@u", uid), ("@v", v));
            void Latest(string uid, long server, long? hero) => Db.Exec(path,
                "INSERT INTO player_latest (uid, name, server, seen, source, hero) VALUES (@u, @u, @s, @t, 'test', @h);", ("@u", uid), ("@s", server), ("@t", at), ("@h", hero));
            Board(1, "100001", 100); Board(1, "100002", 90);
            Latest("100001", 1, 80); Latest("100002", 1, null);
            Latest("200003", 2, 95);   // off the board: the estimate counts
            Latest("300004", 3, 999);  // another server entirely
            Db.Exec(path, "INSERT INTO game_event_group (id, season, open_time, close_time, group_list) VALUES (1, 2, '2020/1/1', '2099/1/1', '1-4|5-8');");

            var d = new Insights(path).Meteorite(null);
            var top = (List<Dictionary<string, object?>>)d["top"]!;
            Assert.Equal(["100001", "200003", "100002"], top.Select(p => (string)p["uid"]!));
            Assert.Equal([true, false, true], top.Select(p => (bool)p["exact"]!));
            Assert.Equal(100L, top[0]["hero"]);

            var servers = (List<Dictionary<string, object?>>)d["serverStats"]!;
            Assert.Equal(190L, servers.Single(s => (int)s["server"]! == 1)["top10"]);
            var pools = (List<Dictionary<string, object?>>)d["pools"]!;
            Assert.True((bool)pools.Single()["current"]!);

            // A picked list overrides the saved one without changing it.
            Assert.Equal([3], (List<int>)new Insights(path).Meteorite([3])["servers"]!);
            Assert.Equal([1, 2], reg.EventServers("meteorite"));
        }
        finally { TestDb.Drop(path); }
    }
}

public class EventCalendarTests
{
    // Saturdays in a week our pool is on get two Meteorite rounds; past the end of the pool table only
    // upcoming Saturdays are added (marked expected); Sundays get three boss spawns; a war window is kept
    // only when one of our servers is in it.
    [Fact]
    public void ScheduleFromTheGameTables()
    {
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);   // a Wednesday
        List<EventCalendar.Pool> pools = [new(new(2026, 9, 21), new(2026, 9, 28), "1893-1956|1957-2020")];
        List<EventCalendar.War> wars =
        [
            new(1, 8, new(2026, 9, 1), new(2026, 9, 20), "1957;1967|1970;1971", "S2"),
            new(2, 8, new(2026, 9, 1), new(2026, 9, 20), "100;101|102;103", "S2"),
        ];
        var ev = EventCalendar.Schedule(new(2026, 9, 14), new(2026, 10, 11), now, 1967, [1967, 1970], pools, wars, [1957, 1967]);
        var met = ev.Where(e => e.Kind == "meteorite").ToList();
        // 19 Sep: no pool row covers it; 26 Sep: pool on; 3 and 10 Oct: expected (table ends 28 Sep).
        Assert.Equal(["meteorite:2026-09-26:1", "meteorite:2026-09-26:2", "meteorite:2026-10-03:1", "meteorite:2026-10-03:2", "meteorite:2026-10-10:1", "meteorite:2026-10-10:2"],
            met.Select(e => e.Id));
        Assert.Equal(new DateTime(2026, 9, 26, 0, 10, 0), met[0].Start);
        Assert.Equal(new DateTime(2026, 9, 26, 13, 0, 0), met[1].End);
        Assert.StartsWith("expected", met[2].Detail);
        Assert.Equal(12, ev.Count(e => e.Kind == "rampage"));   // 4 Sundays x 3 spawns
        Assert.Equal(["war:1"], ev.Where(e => e.Kind == "war").Select(e => e.Id));
    }

    // Schedule rows never replace a manual row, and an event that has started keeps its servers.
    [Fact]
    public void UpsertKeepsManualRowsAndPastServers()
    {
        var path = TestDb.Create();
        try
        {
            var cal = new EventCalendar(path);
            var past = DateTime.UtcNow.AddDays(-1);
            cal.Upsert(new("a", "meteorite", "R1", past, past.AddMinutes(30), [1967], 1, "schedule", ""));
            cal.Upsert(new("a", "meteorite", "R1", past, past.AddMinutes(30), [1970], 1, "schedule", ""));
            Assert.Equal([1967], cal.Get("a")!.Servers);
            cal.Upsert(new("m", "battle", "Mine", past, past.AddHours(1), [1967], 0, "manual", ""));
            cal.Upsert(new("m", "battle", "Overwritten", past, past.AddHours(1), [1967], 0, "schedule", ""));
            Assert.Equal("Mine", cal.Get("m")!.Title);
            var soon = DateTime.UtcNow.AddDays(2);
            cal.Upsert(new("b", "meteorite", "R2", soon, soon.AddMinutes(30), [1967], 2, "schedule", ""));
            cal.SetUpcomingServers("meteorite", [1975, 1970]);
            Assert.Equal([1970, 1975], cal.Get("b")!.Servers);
            Assert.Equal([1967], cal.Get("a")!.Servers);
        }
        finally { TestDb.Drop(path); }
    }
}

public class NearHourTests
{
    [Fact]
    public void HoursWrapAroundMidnight()
    {
        Assert.True(Insights.Near(23, 0));
        Assert.True(Insights.Near(1, 0));
        Assert.False(Insights.Near(2, 0));
        Assert.True(Insights.Near(13, 12));
        Assert.False(Insights.Near(10, 12));
    }
}

public class PresidentTests
{
    // A temporary president inside a term keeps the term's start date: the change, and the change back,
    // are dated when seen - and a newer reading from any scan replaces the registry's president.
    [Fact]
    public void TemporaryPresidentSwitchesBack()
    {
        var path = TestDb.Create();
        try
        {
            var reg = new ServerRegistry(path);
            const string term = "2026-09-16T18:00:00.2160000+00:00";
            reg.SeenKing(1967, "1001690072001967", "Ghost", "SaFa", 100, term);
            reg.SeenKing(1967, "1005330916001967", "Temp", "SaFa", 90, term);
            reg.SeenKing(1967, "1001690072001967", "Ghost", "SaFa", 100, term);
            using var c = Db.Open(path);
            Assert.Equal("Ghost", Db.Rows(c, "SELECT king_name FROM server_info WHERE server_id = 1967;")[0]["king_name"]);
            var events = Db.Rows(c, "SELECT at, value FROM server_event WHERE server_id = 1967 AND kind = 'king' ORDER BY at;");
            Assert.Equal(3, events.Count);                     // first sighting (term start), handover, handover back
            Assert.Equal(term, events[0]["at"]);
            Assert.NotEqual(term, events[1]["at"]);
            Assert.Equal("1001690072001967", events[2]["value"]);
            Assert.Equal("2026-10-01", ServerRegistry.KingChangeAt("2026-10-01", term, "now"));   // a new term: its own start
        }
        finally { TestDb.Drop(path); }
    }
}
