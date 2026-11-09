using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class TransferCompareTests
{
    // The transfer-day rule: home server comes from profiles. A player whose base is over on
    // another server for a war has not moved; a player whose home changed has, even when the
    // map still shows them where they were.
    [Fact]
    public void ProfilesDecideHomeAndVisitorsAreNotMoves()
    {
        var path = TestDb.Create();
        try
        {
            new LastWarDb(path).EnsureSchema();
            var wave = new WaveStore(path);
            wave.EnsureSchema();
            var profiles = new ProfileStore(path);
            profiles.EnsureSchema();

            var t0 = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
            var t1 = t0.AddDays(1);
            const string stayer = "1111111111001967", mover = "2222222222001967";
            // Before: both at home on 1967. After: the stayer's base sits on 1970 for a fight,
            // the mover transferred to 1972.
            PlayerProfile P(string u, DateTimeOffset at, int home, int based) =>
                new() { Uid = u, TakenAt = at, HomeServer = home, CurrentServer = based, Power = 100, Source = "sweep" };
            profiles.Insert([P(stayer, t0, 1967, 1967), P(mover, t0, 1967, 1967)]);
            profiles.Insert([P(stayer, t1, 1967, 1970), P(mover, t1, 1972, 1972)]);
            // The after-map of 1970 shows the visiting stayer there.
            new LastWarDb(path).InsertMapScan(new ServerId(1970), t1, [new MapTile
            {
                Server = new ServerId(1970), PointId = 1, PointType = 1, OwnerUid = new PlayerUid(stayer), Name = "s",
            }]);

            var cut = t0.AddHours(12);
            var (before, _) = wave.StateIn(1957, 1988, null, cut);
            var (after, covered) = wave.StateIn(1957, 1988, cut, t1.AddDays(1));

            Assert.Equal(1967, after[stayer].Server);          // still from 1967
            Assert.True(after[stayer].Visiting);                // though based on 1970
            Assert.Equal(before[stayer].Server, after[stayer].Server);
            Assert.Equal(1972, after[mover].Server);            // a real move
            Assert.Contains(1988, covered);                     // a sweep covers the whole range
        }
        finally { TestDb.Drop(path); }
    }
}
