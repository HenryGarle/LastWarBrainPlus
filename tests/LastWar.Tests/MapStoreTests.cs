using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class MapStoreTests
{
    // A map scan gets its id back from the database (RETURNING), its tiles come back as the
    // server's newest scan, and a re-sent tile replaces the old one rather than failing.
    [Fact]
    public void MapScanRoundTrips()
    {
        var path = TestDb.Create();
        try
        {
            var db = new LastWarDb(path);
            db.EnsureSchema();
            new WaveStore(path).EnsureSchema();
            var at = DateTimeOffset.UtcNow;
            MapTile T(int point, string uid, string tag) => new()
            {
                Server = new ServerId(1967), PointId = point, PointType = 1, OwnerUid = new PlayerUid(uid), Name = "n" + uid,
                AllianceId = "a" + tag, AllianceTag = tag, HqLevel = 30, BuildId = 7,
            };
            var first = db.InsertMapScan(new ServerId(1967), at.AddHours(-1), [T(1, "u1", "OLD")]);
            var second = db.InsertMapScan(new ServerId(1967), at, [T(1, "u1", "X"), T(2, "u2", "Y"), T(2, "u2", "Z")]);
            Assert.True(second > first);
            var (tiles, takenAt) = new WaveStore(path).LatestTiles(1967, at.AddHours(-2));
            Assert.Equal(at.ToString("O"), takenAt);
            Assert.Equal(["X", "Z"], tiles.OrderBy(t => t.PointId).Select(t => t.AllianceTag));
        }
        finally { TestDb.Drop(path); }
    }
}
