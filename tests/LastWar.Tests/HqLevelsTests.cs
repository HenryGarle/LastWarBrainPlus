using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class HqLevelsTests
{
    // The HQ board's updateTime dates a level exactly; off the board the date is bracketed by our readings.
    [Fact]
    public void DatesEachLevelFromTheBoardOrTheReadings()
    {
        var path = TestDb.Create();
        try
        {
            new LastWarDb(path).EnsureSchema();
            new WaveStore(path).EnsureSchema();
            new ServerRegistry(path).EnsureSchema();
            new PlayerActivity(path).EnsureSchema();
            var profiles = new ProfileStore(path);
            profiles.EnsureSchema();
            Db.Exec(path, "INSERT INTO server_track (server_id, level, visible, updated_at) VALUES (1967, 'full', true, '');");
            var t0 = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
            PlayerProfile P(string uid, DateTimeOffset at, int level, string source = "sweep") =>
                new() { Uid = uid, TakenAt = at, HomeServer = 1967, Level = level, Power = 1, LastSeen = at.AddMinutes(-1), Source = source };
            // A: 30 -> 31 between two readings, then 32 (on the HQ board). B: already 31 at our first reading.
            List<PlayerProfile> rows = [P("A", t0, 30), P("A", t0.AddHours(14), 31), P("A", t0.AddDays(8), 32), P("B", t0.AddDays(8), 31), P("C", t0.AddDays(8), 30)];
            profiles.InsertSeen(rows);
            profiles.Insert([.. rows.Where(r => r.TakenAt == t0.AddDays(8))]);
            var board = t0.AddDays(7).AddHours(14);   // A reached 32 at this moment
            Db.Exec(path, "INSERT INTO board_sample (taken_at, server_id, board_type, rank, uid, name, alliance_tag, value, alliance_id, src_server, extra) VALUES (@t, 1967, 5, 1, 'A', 'a', '', 1, '', 0, @x);",
                ("@t", t0.AddDays(8).ToString("O")), ("@x", $"level=32;updateTime={board.ToUnixTimeMilliseconds()}"));

            var r = new Insights(path).HqLevels(31);
            var players = (List<Dictionary<string, object?>>)r["players"]!;
            Assert.Equal(["A", "B"], players.Select(p => (string)p["uid"]!).ToArray());
            var a = (Dictionary<string, object?>)players[0]["reached"]!;
            Assert.Equal(board.ToString("O"), ((Dictionary<string, object?>)a["32"]!)["at"]);
            var a31 = (Dictionary<string, object?>)a["31"]!;
            Assert.Equal(t0.AddMinutes(-1).ToString("O"), a31["after"]);
            Assert.Equal(t0.AddHours(14).AddMinutes(-1).ToString("O"), a31["by"]);
            var b31 = (Dictionary<string, object?>)((Dictionary<string, object?>)players[1]["reached"]!)["31"]!;
            Assert.Null(b31["after"]);
            var counts = (List<Dictionary<string, object?>>)r["counts"]!;
            Assert.Equal(3, counts.Count);   // one each at 30, 31, 32 on #1967
        }
        finally { TestDb.Drop(path); }
    }
}
