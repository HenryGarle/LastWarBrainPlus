using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class PlayerActivityTests
{
    // 100 -> 150 (a rise, 3 days ago) -> 150 -> 150 (two sweeps an hour apart): last_rise_at is
    // the 150 reading, and the window test against "now" finds it. A player who only fell has none.
    [Fact]
    public void LastRiseIsTheNewestReadingAboveTheOneBefore()
    {
        var path = TestDb.Create();
        try
        {
            var db = new LastWarDb(path);
            db.EnsureSchema();
            var now = DateTimeOffset.UtcNow;
            PowerSample S(string uid, double daysAgo, long power) => new()
            { TakenAt = now.AddDays(-daysAgo), Server = new ServerId(1967), Uid = new PlayerUid(uid), Power = power };
            db.InsertPowerSamples([
                S("1", 4, 100), S("1", 3, 150), S("1", 0.05, 150), S("1", 0, 150),
                S("2", 4, 200), S("2", 0, 180),
            ]);
            Assert.Equal(2, new PlayerActivity(path).Rebuild());

            using var c = Db.Open(path);
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                SELECT uid, samples, last_rise_at, last_rise_at >= {PlayerActivity.WindowStart(Db.NowIso)}
                FROM player_activity ORDER BY uid;
                """;
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(("1", 4L, now.AddDays(-3).ToString("O"), 1L), (r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetBoolean(3) ? 1L : 0L));
            Assert.True(r.Read());
            Assert.Equal(("2", ""), (r.GetString(0), r.GetString(2)));
        }
        finally { TestDb.Drop(path); }
    }
}
