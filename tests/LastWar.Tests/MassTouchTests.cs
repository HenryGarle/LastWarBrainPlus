using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class MassTouchTests
{
    // 120 small accounts whose last update is the same minute is the game touching them, not 120
    // logins: their last seen is ignored and they are not active. A big account seen at its own
    // moment, and a small one that donated, are active; power rising alone never is.
    [Fact]
    public void MassTouchIsNotALoginAndRisingPowerIsNotActivity()
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
            var t30 = now.AddHours(-30);
            var touch = new DateTimeOffset(t30.Year, t30.Month, t30.Day, t30.Hour, t30.Minute, 0, TimeSpan.Zero);   // one whole minute
            PlayerProfile P(string uid, long power, DateTimeOffset seen, int agoMinutes = 5) =>
                new() { Uid = uid, TakenAt = now.AddMinutes(-agoMinutes), HomeServer = 1967, Power = power, LastSeen = seen, Source = "sweep" };
            // The big account was first profiled a day ago (that first look proves nothing), then seen
            // again 2 h ago: the change is the login. The second big account has only its first look.
            List<PlayerProfile> rows0 = [.. Enumerable.Range(0, 120).Select(i => P($"{1000 + i}001967", 3_000_000, touch.AddSeconds(i % 50))),
                             P("9000001967", 150_000_000, now.AddDays(-2), 24 * 60), P("9000001967", 150_000_900, now.AddHours(-2)),   // played: power moved
                             P("9100001967", 150_000_000, now.AddHours(-3))];
            profiles.Insert(rows0);
            profiles.InsertSeen(rows0);   // a sweep stores both: full profiles and the login moments (player_seen)
            // one touched small account also donated: that makes it active
            new IntelStore(path).Insert(new AllianceRoster("A", now, [new RosterMember { Uid = "1000001967", Name = "d", DonatedAt = now.AddHours(-3) }], [], []));
            // power rose for a touched account - still not active
            new LastWarDb(path).InsertPowerSamples([
                new PowerSample { TakenAt = now.AddDays(-2), Server = new ServerId(1967), Uid = new PlayerUid("1001001967"), Power = 1 },
                new PowerSample { TakenAt = now.AddDays(-1), Server = new ServerId(1967), Uid = new PlayerUid("1001001967"), Power = 2 }]);

            new PlayerActivity(path).Rebuild();
            using var c = Db.Open(path);
            var rows = Db.Rows(c, $"SELECT uid, last_seen_at, last_rise_at, last_active_at >= {Db.AgoIso("7 days")} AS active FROM player_activity;")
                .ToDictionary(r => (string)r["uid"]!);
            Assert.Equal("", rows["1002001967"]["last_seen_at"]);                 // touched only
            Assert.Equal(false, rows["1002001967"]["active"]);
            Assert.Equal(true, rows["9000001967"]["active"]);                      // a real login: last seen moved
            Assert.Equal(false, rows["9100001967"]["active"]);                     // one look, never seen to change
            Assert.Equal(true, rows["1000001967"]["active"]);                      // donated
            Assert.NotEqual("", rows["1001001967"]["last_rise_at"]);
            Assert.Equal(false, rows["1001001967"]["active"]);                     // rose, never played
        }
        finally { TestDb.Drop(path); }
    }
}
