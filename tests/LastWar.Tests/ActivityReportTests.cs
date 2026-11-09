using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class ActivityReportTests
{
    // Two roster reads with different last-donation moments: both become events, both land in
    // their UTC hour, and a donation minutes ago scores full marks for recency.
    [Fact]
    public void DonationsBecomeTimedEvents()
    {
        var path = TestDb.Create();
        try
        {
            new LastWarDb(path).EnsureSchema(); new WaveStore(path).EnsureSchema(); new ProfileStore(path).EnsureSchema(); new PlayerActivity(path).EnsureSchema(); new PlayerLatest(path).EnsureSchema(); new IntelStore(path).EnsureSchema(); new HeroModel(path).EnsureSchema(); new ChatStore(path).EnsureSchema();
            var store = new IntelStore(path);
            var now = DateTimeOffset.UtcNow;
            AllianceRoster R(DateTimeOffset at, DateTimeOffset donated) => new("A", at, Enumerable.Range(0, 3).Select(i => new RosterMember
            {
                Uid = $"100{i}", Name = $"p{i}", Rank = 1, Power = 100_000_000, Hq = 30,
                DonatedAt = donated, DonateToday = 100 * (3 - i), DonateWeek = 1000 * (3 - i),
            }).ToList(), [], []);
            var (d1, d2) = (now.AddHours(-5), now.AddMinutes(-10));
            store.Insert(R(now.AddHours(-4), d1));
            store.Insert(R(now, d2));

            var a = new Insights(path).PlayerActivityReport("1000");
            var events = (List<Dictionary<string, object?>>)a["events"]!;
            Assert.Equal(2, events.Count(e => (string)e["kind"]! == "donated"));
            var hours = (Dictionary<string, int[]>)a["hours"]!;
            Assert.Equal(2, hours["donated"].Sum());
            Assert.True(hours["donated"][d2.UtcDateTime.Hour] >= 1);
            var don = (Dictionary<string, object?>)a["donation"]!;
            Assert.Equal(1, don["place"]);   // 1000 gave the most this week
            var recency = ((List<Dictionary<string, object?>>)a["parts"]!)[0];
            Assert.Equal(40, recency["score"]);

            // Alliance view: all 3 members were on in the latest donation's hour.
            var al = new Insights(path).AllianceActivity("A");
            var hour = ((List<Dictionary<string, object?>>)al["hours"]!)[d2.UtcDateTime.Hour];
            Assert.Equal(3, hour["members"]);
        }
        finally { TestDb.Drop(path); }
    }
}
