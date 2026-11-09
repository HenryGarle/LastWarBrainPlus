using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class RetentionTests
{
    // Recent days keep their biggest sweep, older weeks keep one, today is left alone, and the old pulse rows
    // become login moments.
    [Fact]
    public void ThinKeepsOneSnapshotPerDayThenPerWeek()
    {
        var path = TestDb.Create();
        try
        {
            new LastWarDb(path).EnsureSchema();
            new IntelStore(path).EnsureSchema();
            var profiles = new ProfileStore(path);
            profiles.EnsureSchema();
            var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
            PlayerProfile P(string uid, DateTimeOffset at, string source = "sweep") =>
                new() { Uid = uid, TakenAt = at, HomeServer = 1967, Power = 1, LastSeen = at.AddMinutes(-1), Source = source };
            var day3a = now.AddDays(-3).AddHours(-2);           // full sweep
            var day3b = now.AddDays(-3).AddHours(1);            // later, but one server only
            var old1 = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);   // Tuesday
            var old2 = new DateTimeOffset(2026, 9, 3, 6, 0, 0, TimeSpan.Zero);   // Thursday, same week
            var today1 = now.AddHours(-6);
            var today2 = now.AddHours(-1);
            profiles.Insert([P("1", day3a), P("2", day3a), P("1", day3b), P("1", old1), P("2", old1), P("1", old2), P("2", old2),
                             P("1", today1), P("1", today2), P("1", now.AddDays(-2), "pulse")]);

            Assert.Equal(1, new Retention(path).Backfill());
            Assert.Equal(0, new Retention(path).Backfill());    // once only
            new Retention(path).Thin(now);

            using var c = Db.Open(path);
            var left = Db.Rows(c, "SELECT DISTINCT taken_at AS t FROM player_profile ORDER BY 1;").Select(r => (string)r["t"]!).ToList();
            Assert.Equal(new[] { old2, day3a, today1, today2 }.Select(t => t.ToString("O")), left);
            Assert.Equal(1L, Db.Rows(c, "SELECT COUNT(*) AS n FROM player_seen WHERE seen_at = @s;", ("@s", now.AddDays(-2).AddMinutes(-1).ToString("O")))[0]["n"]);
        }
        finally { TestDb.Drop(path); }
    }
}
