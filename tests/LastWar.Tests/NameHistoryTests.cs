using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class NameHistoryTests
{
    // Every source counts, in time order; the same name from different sources is not a change,
    // and a rename back is logged as its own change.
    [Fact]
    public void RenamesAreLoggedWithWhenTheyWereFirstSeen()
    {
        var path = TestDb.Create();
        try
        {
            var db = new LastWarDb(path);
            db.EnsureSchema();
            new WaveStore(path).EnsureSchema();
            new ChatStore(path).EnsureSchema();
            new Insights(path).EnsureSchema();
            var profiles = new ProfileStore(path);
            profiles.EnsureSchema();
            var t0 = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
            const string uid = "1234567890001967";
            PowerSample P(DateTimeOffset at, string name) => new()
                { TakenAt = at, Server = new ServerId(1967), Uid = new PlayerUid(uid), Power = 1, Name = name, AllianceId = "al1", AllianceTag = "OLD" };
            db.InsertPowerSamples([P(t0, "Bob")]);
            profiles.Insert([new PlayerProfile { Uid = uid, TakenAt = t0.AddHours(1), Name = "Bob", AllianceId = "al1", AllianceTag = "OLD", Source = "sweep" }]);
            db.InsertPowerSamples([P(t0.AddHours(2), "Prime Bob")]);
            profiles.Insert([new PlayerProfile { Uid = uid, TakenAt = t0.AddHours(3), Name = "Bob", AllianceId = "al1", AllianceTag = "NEW", Source = "sweep" }]);

            var names = new NameHistory(path);
            names.Rebuild();
            var player = names.For("player", uid);
            Assert.Equal(2, player.Count);
            Assert.Equal("Bob", player[0]["old"]);
            Assert.Equal("Prime Bob", player[0]["new"]);
            Assert.Equal(t0.AddHours(2).ToString("O"), player[0]["detected"]);
            Assert.Equal(t0.AddHours(1).ToString("O"), player[0]["lastSeenOld"]);
            var tag = Assert.Single(names.For("alliance", "al1"));
            Assert.Equal("NEW", tag["new"]);
            Assert.Equal(3, names.Rebuild());   // rebuilding is idempotent
        }
        finally { TestDb.Drop(path); }
    }
}
