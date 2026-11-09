using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class HeroModelTests
{
    // Board hero = 0.8 x (power - army) for three players; the profile an hour off the board is
    // matched, one 5 h off is not. k comes out 0.8 and every error 0.
    [Fact]
    public void FitsKFromBoardAndNearestProfile()
    {
        var path = TestDb.Create();
        try
        {
            new WaveStore(path).EnsureSchema();
            var profiles = new ProfileStore(path);
            profiles.EnsureSchema();
            var t = new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
            using (var c = Db.Open(path))
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"""
                    INSERT INTO board_sample (taken_at, server_id, board_type, rank, uid, value) VALUES
                      ('{t:O}', 1967, 13, 1, 'a', 80000000), ('{t:O}', 1967, 13, 2, 'b', 60000000), ('{t:O}', 1967, 13, 3, 'c', 40000000);
                    """;
                cmd.ExecuteNonQuery();
            }
            PlayerProfile P(string uid, long power, long army, double hoursOff) =>
                new() { Uid = uid, TakenAt = t.AddHours(hoursOff), HomeServer = 1967, Power = power, ArmyPower = army };
            profiles.Insert([P("a", 120_000_000, 20_000_000, 1), P("b", 100_000_000, 25_000_000, -1),
                             P("c", 60_000_000, 10_000_000, 0.5), P("c", 999_000_000, 0, 5)]);

            Assert.Equal(3, new HeroModel(path).Rebuild());
            var fit = new Insights(path).HeroFit();
            var all = fit.Single(r => Convert.ToInt32(r["server"]) == 0);
            Assert.Equal(0.8, (double)all["k"]!, 6);
            Assert.Equal(0.0, (double)all["worstErr"]!, 6);
        }
        finally { TestDb.Drop(path); }
    }
}
