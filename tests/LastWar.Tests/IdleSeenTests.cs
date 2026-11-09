using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class IdleSeenTests
{
    // The game moves lastUpdateTime when it changes an account (its base attacked or moved), so a last-seen moment only
    // counts as a login when power, kills or HQ moved in the week before it. Henry, 2026-11-03.
    [Fact]
    public void LastSeenWithoutAStatChangeIsNotALogin()
    {
        var path = TestDb.Create();
        try
        {
            new LastWarDb(path).EnsureSchema();
            var profiles = new ProfileStore(path);
            profiles.EnsureSchema();
            new IntelStore(path).EnsureSchema();
            new ChatStore(path).EnsureSchema();
            var now = DateTimeOffset.UtcNow;
            PlayerProfile P(string uid, long power, DateTimeOffset at, DateTimeOffset seen) =>
                new() { Uid = uid, TakenAt = at, HomeServer = 1967, Power = power, LastSeen = seen, Source = "sweep" };
            void Store(params PlayerProfile[] rows) { profiles.Insert(rows); profiles.InsertSeen(rows); }

            // ten days ago both were the same; since then the dead one only had its base touched, the other one played
            Store(P("8000001967", 89_241_970, now.AddDays(-10), now.AddDays(-11)), P("8100001967", 89_241_970, now.AddDays(-10), now.AddDays(-11)));
            Store(P("8000001967", 89_241_970, now.AddMinutes(-5), now.AddHours(-1)), P("8100001967", 95_000_000, now.AddMinutes(-5), now.AddHours(-1)));

            void Check()
            {
                new PlayerActivity(path).Rebuild();
                using var c = Db.Open(path);
                var rows = Db.Rows(c, $"SELECT uid, last_seen_at, last_updated_at, last_active_at >= {Db.AgoIso("7 days")} AS active FROM player_activity;")
                    .ToDictionary(r => (string)r["uid"]!);
                Assert.Equal("", rows["8000001967"]["last_seen_at"]);
                Assert.Equal(false, rows["8000001967"]["active"]);
                Assert.Equal(now.AddHours(-1).ToString("O"), rows["8000001967"]["last_updated_at"]);   // kept as a point of interest
                Assert.Equal(true, rows["8100001967"]["active"]);
            }
            Check();

            // the same verdicts for moments stored before idle_seen existed
            using (var c = Db.Open(path)) { Db.Command(c, "DELETE FROM idle_seen; DELETE FROM schema_fix WHERE name LIKE 'idle_seen_%';").ExecuteNonQuery(); }
            Assert.Equal(PlayerActivity.Buckets, new PlayerActivity(path).JudgeBacklog());
            Check();
        }
        finally { TestDb.Drop(path); }
    }
}
