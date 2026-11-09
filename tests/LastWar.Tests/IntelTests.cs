using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class IntelTests
{
    // Two identical alliances: every fight estimate is exactly even once the defender's +10% is
    // taken out (open fight), and attacking is symmetric. A member who
    // donated an hour ago counts as in the game today even with no profile.
    [Fact]
    public void MatchupIsEvenForEqualAlliancesAndDonationsCountAsActivity()
    {
        var path = TestDb.Create();
        try
        {
            new LastWarDb(path).EnsureSchema(); new WaveStore(path).EnsureSchema(); new ProfileStore(path).EnsureSchema(); new PlayerActivity(path).EnsureSchema(); new PlayerLatest(path).EnsureSchema(); new IntelStore(path).EnsureSchema(); new HeroModel(path).EnsureSchema();
            var store = new IntelStore(path);
            var now = DateTimeOffset.UtcNow;
            AllianceRoster R(string id) => new(id, now, Enumerable.Range(0, 25).Select(i => new RosterMember
            {
                Uid = $"{id}{i}", Name = $"p{i}", Rank = 1, Power = 100_000_000 - i * 1_000_000, Hq = 30,
                DonatedAt = now.AddHours(-1), DonateToday = 100,
            }).ToList(), [], []);
            store.Insert(R("A"));
            store.Insert(R("B"));

            var m = new Insights(path).Matchup("A", "B");
            Assert.Equal(0.5, (double)m["open"]!, 3);
            Assert.Equal((double)m["aAttacks"]!, (double)m["bAttacks"]!);   // same alliance either way round
            Assert.Equal(25, ((Dictionary<string, object?>)m["a"]!)["seen1d"]);

            var a = new Insights(path).AllianceIntel("A");
            var act = ((List<Dictionary<string, object?>>)a["activity"]!)[^1];
            Assert.Equal((25L, 25L), (Convert.ToInt64(act["members"]), Convert.ToInt64(act["seen1d"])));
        }
        finally { TestDb.Drop(path); }
    }
}
