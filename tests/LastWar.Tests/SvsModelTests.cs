using LastWar.Storage;

namespace LastWar.Tests;

public class SvsModelTests
{
    // The two Weekly Results screenshots the v1 model was fitted on (2026-10-08, ~3.5 days in), with our data for each server.
    static readonly SvsModel.Side S1974 = new(213e6, 10.96e9, 64.0e9), S1978 = new(172e6, 9.85e9, 64.5e9);
    static readonly SvsModel.Side S1967 = new(136e6, 9.53e9, 67.3e9), S1979 = new(140e6, 9.12e9, 33.8e9);

    [Fact]
    public void CallsTheBossesAndTheLeaderOnTheFittedScreenshots()
    {
        var (a, b) = SvsModel.Predict(S1974, S1978, 3.5);
        Assert.Equal(750_000, a[2]);                                // both boss prizes went to #1974
        Assert.InRange(a[3], 360_000, 450_000);                     // 1st place: a chance now, a strong one
        Assert.Equal(0, b[2]);
        Assert.True(b[3] < 90_000);
        Assert.True(a.Sum() > b.Sum());                             // 3.48M v 2.30M seen

        (a, b) = SvsModel.Predict(S1967, S1979, 3.5);
        Assert.Equal(750_000, a[2]);                                // warzone damage: #1967
        Assert.True(b[3] > a[3]);                                   // 1st place: #1979's whale, more likely than not
        Assert.True(a.Sum() > b.Sum());                             // 2.82M v 2.26M seen
        Assert.InRange(a[4], 300_000, 480_000);                     // trucks seen 387.1K
    }

    [Fact]
    public void MondayHasOnlySundaysArmsRaceAndTrucksAndTheDuelStopsAtThursday()
    {
        var (a, b) = SvsModel.Predict(S1974, S1978, 0.5);
        Assert.Equal(0, a[0] + a[1] + a[2] + a[3] + b[0] + b[1] + b[2] + b[3]);   // no duel, MVP or boss yet
        Assert.True(a[4] > 0 && a[5] > 0 && b[4] > 0 && b[5] > 0);              // Sunday's trucks and Arms Race count on Monday
        Assert.Equal(SvsModel.Predict(S1974, S1978, 4.5).A[0], SvsModel.Predict(S1974, S1978, 5).A[0]);   // Friday's duel lands after the decision
        Assert.Equal(0, SvsModel.Predict(S1974, S1978, 4.9).A[6]);                                       // Desert Storm: Friday, in at the end of it
        Assert.True(SvsModel.Predict(S1974, S1978, 5).A[6] > 0);
    }

    [Fact]
    public void EndOfDayCountsWhatHappenedThatDay()
    {
        var (a, b) = SvsModel.EndOfDay(S1974, S1978, 1);
        Assert.True(a[0] > 0 && a[1] > 0);                  // Monday's duel and MVP belong to day 1
        Assert.Equal(250_000, a[2]);                        // one boss day
        Assert.InRange(a[3], 120_000, 150_000);             // 1st place as a chance
        var mon = SvsModel.EndOfDay(S1974, S1978, 1).A[5];
        var tue = SvsModel.EndOfDay(S1974, S1978, 2).A[5];
        Assert.Equal(2, mon / (tue - mon), 3);              // Arms Race: Sunday + Monday in day 1, one day each after
        var fri = SvsModel.EndOfDay(S1974, S1978, 5);
        Assert.Equal(SvsModel.EndOfDay(S1974, S1978, 4).A[0], fri.A[0]);   // Friday's duel is scored after the decision
        Assert.Equal(SvsModel.Predict(S1974, S1978, 5).A, fri.A);         // day 5 = the board at the end of Friday
    }

    [Fact]
    public void CappedAtTheEnd()
    {
        var end = SvsModel.Predict(S1974, S1978, SvsModel.RaceDays);
        var later = SvsModel.Predict(S1974, S1978, 9);
        Assert.Equal(end.A, later.A);
    }


    // v1.5: the servers as read on 2026-10-09 (top-1 / top-100 / active hero power, 5 strongest active, alliances with 10+ active).
    static SvsModel.Side S(double top1, double top100, double act, double[] heroes, double alliances) =>
        new(top1 * 1e6, top100 * 1e9, act * 1e9, [.. heroes.Select(h => h * 1e6)], alliances);
    static readonly SvsModel.Side N1967 = S(136.2, 9.54, 67.0, [136.2, 120.9, 120.6, 115.2, 114.4], 23);
    static readonly SvsModel.Side N1979 = S(140.2, 9.14, 33.6, [140.2, 139.9, 121.0, 116.3, 115.4], 17);
    static readonly SvsModel.Side N1962 = S(140.0, 9.81, 60.1, [140.0, 125.4, 124.3, 122.5, 122.0], 21);
    static readonly SvsModel.Side N1972 = S(158.1, 8.68, 37.5, [158.1, 131.3, 123.8, 114.1, 109.8], 26);
    static readonly SvsModel.Side N1974 = S(213.2, 10.98, 64.2, [213.2, 162.8, 160.6, 157.4, 148.4], 17);
    static readonly SvsModel.Side N1978 = S(172.2, 9.86, 64.0, [172.2, 149.0, 130.4, 128.2, 124.4], 21);
    static readonly SvsModel.Side N1981 = S(137.4, 9.97, 61.0, [137.4, 118.1, 118.0, 115.5, 114.3], 16);
    static readonly SvsModel.Side N1970 = S(153.8, 9.62, 45.0, [153.8, 141.5, 137.6, 129.9, 128.5], 15);

    // The Thursday-evening boards it was fitted on: leader right, totals close, match-ups and boss 1st as seen.
    [Theory]
    [InlineData(1967, 1979, 3.742, 2_843_500, 2_268_400, 20)]
    [InlineData(1962, 1972, 3.774, 3_425_600, 2_217_700, 23.5)]
    [InlineData(1974, 1978, 3.823, 3_499_200, 2_310_000, 19)]
    public void RefitMatchesThursdayEvening(int a, int b, double days, double seenA, double seenB, double matchUps)
    {
        var all = new Dictionary<int, SvsModel.Side> { [1967] = N1967, [1979] = N1979, [1962] = N1962, [1972] = N1972, [1974] = N1974, [1978] = N1978 };
        var (pa, pb) = SvsModel.Predict(all[a], all[b], days);
        Assert.Equal(matchUps, SvsModel.MatchUps(all[a], all[b]));
        Assert.True(pa.Sum() > pb.Sum());
        Assert.InRange(pa.Sum() / seenA, 0.8, 1.2);
        Assert.InRange(pb.Sum() / seenB, 0.8, 1.2);
    }

    // Boss 1st place is a chance between whales: right way round for the three seen, and #1981 v #1970 (Henry: #1970's call
    // was wrong) is no longer a certainty.
    [Fact]
    public void FirstPlaceIsAChance()
    {
        Assert.True(SvsModel.FirstPlaceChance(N1974, N1978) > 0.8);
        Assert.True(SvsModel.FirstPlaceChance(N1972, N1962) > 0.6);
        Assert.True(SvsModel.FirstPlaceChance(N1979, N1967) > 0.5);
        Assert.InRange(SvsModel.FirstPlaceChance(N1981, N1970), 0.05, 0.4);
        Assert.Equal(SvsModel.FirstPlaceChance(N1981, N1970), SvsModel.FirstPlaceChance(N1981, N1970));   // same inputs, same answer
    }}
