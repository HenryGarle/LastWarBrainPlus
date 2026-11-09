using LastWar.Core.Analysis;
using LastWar.Core.Model;

namespace LastWar.Tests;

public class ActivityAnalyzerTests
{
    static Dictionary<PlayerUid, PowerSample> Snap(params (string Uid, long Power)[] rows) =>
        rows.ToDictionary(
            r => new PlayerUid(r.Uid),
            r => new PowerSample
            {
                TakenAt = DateTimeOffset.UnixEpoch,
                Server = new ServerId(1967),
                Uid = new PlayerUid(r.Uid),
                Power = r.Power,
            });

    [Fact]
    public void RiseIsActivity() =>
        Assert.Equal(ActivityStatus.Active, ActivityAnalyzer.Classify(500, 600));

    [Fact]
    public void FallIsNotEvidence() =>
        Assert.Equal(ActivityStatus.Down, ActivityAnalyzer.Classify(1000, 500));

    [Fact]
    public void UnchangedIsFlat() =>
        Assert.Equal(ActivityStatus.Flat, ActivityAnalyzer.Classify(700, 700));

    [Fact]
    public void UnseenIsNew() =>
        Assert.Equal(ActivityStatus.New, ActivityAnalyzer.Classify(null, 1));

    /// <summary>
    /// The rule Henry corrected: judged step by step, never against an all-time peak.
    /// 1000 -> 500 -> 600 must read as ACTIVE on the last step even though 600 is far
    /// below the old peak of 1000.
    /// </summary>
    [Fact]
    public void RecoveryAfterDropCountsAsActive()
    {
        var history = new[]
        {
            Snap(("1001", 1000)),
            Snap(("1001", 500)),
            Snap(("1001", 600)),
        };

        var result = Assert.Single(ActivityAnalyzer.Compare(history));

        Assert.Equal(ActivityStatus.Active, result.Status);
        Assert.Equal(100, result.Delta);
        Assert.True(result.ActiveEver);
    }

    [Fact]
    public void StillFallingIsDownAndNeverShowedLife()
    {
        var history = new[] { Snap(("1001", 1000)), Snap(("1001", 900)), Snap(("1001", 800)) };

        var result = Assert.Single(ActivityAnalyzer.Compare(history));

        Assert.Equal(ActivityStatus.Down, result.Status);
        Assert.Equal(-100, result.Delta);
        Assert.False(result.ActiveEver);
    }

    static Dictionary<PlayerUid, PowerSample> At(double daysAgo, long power) => new()
    {
        [new PlayerUid("1001")] = new PowerSample
        {
            TakenAt = DateTimeOffset.UnixEpoch.AddDays(30 - daysAgo), Server = new ServerId(1967),
            Uid = new PlayerUid("1001"), Power = power,
        },
    };

    /// <summary>
    /// The flaw Henry found: two snapshots an hour apart read as "flat" for nearly everyone.
    /// A rise earlier in the window must still count.
    /// </summary>
    [Fact]
    public void RiseEarlierInTheWindowStillCountsAsActive()
    {
        var history = new[] { At(3, 100), At(2, 150), At(0.05, 150), At(0, 150) };

        Assert.Equal(ActivityStatus.Flat, Assert.Single(ActivityAnalyzer.Compare(history)).Status);
        var windowed = Assert.Single(ActivityAnalyzer.Compare(history, TimeSpan.FromDays(7)));
        Assert.Equal(ActivityStatus.Active, windowed.Status);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(28), windowed.LastRise);
    }

    [Fact]
    public void RiseOutsideTheWindowDoesNotCount()
    {
        var history = new[] { At(10, 100), At(9, 150), At(1, 150), At(0, 140) };

        Assert.Equal(ActivityStatus.Down, Assert.Single(ActivityAnalyzer.Compare(history, TimeSpan.FromDays(7))).Status);
    }

    // Teleport day, real rows: Silent Trigger (SaFa) teleported to 2010 - serverId 2010, srcServer
    // 1967 - is still homed on 1967. A cross-fight source wins when set; serverId is the last resort.
    [Fact]
    public void HomeServerIgnoresTeleports()
    {
        Assert.Equal(1967, LastWar.Client.Game.ProfileLookup.HomeOf(serverId: 2010, srcServer: 1967, crossFightSrc: 0));
        Assert.Equal(1970, LastWar.Client.Game.ProfileLookup.HomeOf(serverId: 2010, srcServer: 1967, crossFightSrc: 1970));
        Assert.Equal(1959, LastWar.Client.Game.ProfileLookup.HomeOf(serverId: 1959, srcServer: 0, crossFightSrc: 0));
    }

    [Fact]
    public void DisappearedAccountsAreReported()
    {
        var before = Snap(("1001", 10), ("1002", 20));
        var now = Snap(("1001", 10));

        var gone = ActivityAnalyzer.Disappeared(before, now);

        Assert.Equal(new PlayerUid("1002"), Assert.Single(gone));
    }

    [Fact]
    public void OriginServerComesFromUidSuffix()
    {
        Assert.Equal(new ServerId(1963), new PlayerUid("1240780578001963").OriginServer);
        Assert.Equal(new ServerId(1967), new PlayerUid("2359053554001967").OriginServer);
        Assert.Null(new PlayerUid("abc").OriginServer);
    }

    [Fact]
    public void ServerIdKnowsItsPortAndZone()
    {
        Assert.Equal(11967, new ServerId(1967).GamePort);
        Assert.Equal(10348, new ServerId(348).GamePort);
        Assert.Equal("APS1967", new ServerId(1967).Zone);
    }

    [Fact]
    public void TileCoordinatesRoundTrip()
    {
        var tile = new MapTile
        {
            Server = new ServerId(1967), PointId = 14502, PointType = 6,
            OwnerUid = new PlayerUid("1240780578001963"),
        };

        var (x, y) = tile.Coordinates();

        Assert.Equal((501, 14), (x, y));
        Assert.Equal(14502, x + y * 1000 + 1);
    }
}
