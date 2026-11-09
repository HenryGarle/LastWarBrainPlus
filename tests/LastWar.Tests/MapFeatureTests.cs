using Google.Protobuf;
using LastWar.Client.Scanning;
using LastWar.Core.Model;
using LastWar.Protocol.Sfs;

namespace LastWar.Tests;

public class MapFeatureTests
{
    static byte[] Point(int x, int y, int type, IMessage? extra = null, int server = 1967, Protobuf.AllianceBossS0BuildPointInfo? trap = null)
    {
        var p = new Protobuf.WorldPointInfo { Id = x + y * 1000 + 1, PointType = type, ServerId = server };
        if (extra is not null) p.ExtraInfo = extra.ToByteString();
        if (trap is not null) p.AllianceBossS0BuildPointInfo = trap;
        return p.ToByteArray();
    }

    [Fact]
    public void ReadsAlliancePointsAndSkipsTheRest()
    {
        var points = new SfsArray();
        foreach (var b in new[]
        {
            Point(520, 420, 15, new Protobuf.AllianceBuildingPointInfo { BuildId = 300001, Level = 1, AlAbbr = "SaDe", AllianceId = "A1" }),
            Point(549, 449, 25, new Protobuf.CityStrongholdPointInfo { StrongholdId = 14, AlAbbr = "BTW", AllianceId = "B2", AlName = "Between" }),
            Point(49, 49, 35, new Protobuf.CityTradePointInfo { TradeId = 44, Uid = "123", UserName = "Holder", AlAbbr = "KYZ", AllianceId = "K3" }),
            Point(540, 440, 59, trap: new Protobuf.AllianceBossS0BuildPointInfo { AllianceId = "A1", Abbr = "SaDe", CfgId = 3 }),
            Point(10, 8, 7),                                                                  // a resource: not ours
            Point(149, 49, 11, new Protobuf.AllianceCityPointInfo { CityId = 34 }, server: 374), // a neighbour's
        })
            points.Add(SfsValue.Of(b));
        var reply = new SfsObject();
        reply.PutArray("points", points);

        var f = MapScanner.ReadFeatures(reply, new ServerId(1967)).ToDictionary(x => x.PointType);
        Assert.Equal([15, 25, 35, 59], f.Keys.Order());
        Assert.Equal((300001, "SaDe", "A1"), (f[15].BuildId, f[15].AllianceTag, f[15].AllianceId));
        Assert.Equal((14, "Between"), (f[25].BuildId, f[25].AllianceName));
        Assert.Equal(("Holder", "123", "K3"), (f[35].Label, f[35].HolderUid, f[35].AllianceId));
        Assert.Equal((3, "A1"), (f[59].Level, f[59].AllianceId));
        Assert.Equal(540 + 440 * 1000 + 1, f[59].PointId);
    }

    // Secret Tasks: point type 17, detail in WorldPointInfo field 10 (the game's schema, LastWar.Client.Proto).
    [Fact]
    public void ReadsSecretTasksOnTheScannedServerOnly()
    {
        LastWar.Client.Proto.WorldPointInfo Task(int x, int y, int server) => new()
        {
            Id = x + y * 1000 + 1, PointType = 17, ServerId = server,
            HeroDispatchMissionPointInfo = new() { OwnerUid = "42", CfgId = 50000504, CompletionTime = 1790000000, StealList = { "a", "b" } },
        };
        var points = new SfsArray();
        foreach (var p in new[] { Task(298, 33, 1559), Task(1, 1, 1560), new LastWar.Client.Proto.WorldPointInfo { Id = 5, PointType = 7, ServerId = 1559 } })
            points.Add(SfsValue.Of(p.ToByteArray()));
        var reply = new SfsObject();
        reply.PutArray("points", points);
        var t = Assert.Single(SecretTasks.Read(reply, 1559));
        Assert.Equal((298, 33, 50000504, "42", 1790000000L, 2), (t.X, t.Y, t.CfgId, t.OwnerUid, t.CompletionTime, t.Steals));
    }

    [Fact]
    public void ReadsGreenTilesIntoAGzipBitset()
    {
        var g = new Protobuf.IndexGreenPoints { Index = 0, TimeStamp = 1 };
        g.Points.AddRange([1, 2, 1001, 1_000_000]);
        var arr = new SfsArray();
        arr.Add(SfsValue.Of(g.ToByteArray()));
        var reply = new SfsObject();
        reply.PutArray("greenPoints", arr);
        var green = new HashSet<int>();
        MapScanner.ReadGreen(reply, new ServerId(1967), green);
        Assert.Equal(4, green.Count);

        var gz = LastWar.Storage.LastWarDb.GreenBits(green);
        using var z = new System.IO.Compression.GZipStream(new MemoryStream(gz), System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream(); z.CopyTo(ms);
        var bits = ms.ToArray();
        bool On(int p) => (bits[(p - 1) >> 3] & (1 << ((p - 1) & 7))) != 0;
        Assert.True(On(1) && On(2) && On(1001) && On(1_000_000));
        Assert.False(On(3));
        Assert.True(gz.Length < 2000);
    }
}