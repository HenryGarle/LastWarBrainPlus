using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class RoomStateTests
{
    static ChatMessage M(string room, long seq) => new()
    {
        RoomId = room, SeqId = seq, Text = "hi", SenderUid = "1234567890001967",
        SentAt = DateTimeOffset.UtcNow.AddMinutes(-seq),
    };

    // Archive keeps every message but drops the room from chat stats; delete empties it and keeps
    // it empty against later pushes until it is followed (restored) again.
    [Fact]
    public void ArchiveHidesFromStatsDeleteEmptiesAndStaysEmpty()
    {
        var path = TestDb.Create();
        try
        {
            var store = new ChatStore(path);
            store.EnsureSchema();
            new PlayerLatest(path).EnsureSchema();
            store.InsertNew([M("country_1", 1), M("country_1", 2), M("country_2", 1)]);
            var insights = new Insights(path);
            int Rooms() => ((System.Collections.ICollection)insights.ChatStats(30)["rooms"]!).Count;
            Assert.Equal(2, Rooms());

            store.Archive("country_1", true);
            Assert.Equal(1, Rooms());
            Assert.Equal(2, store.RoomStats("country_1").Messages);   // nothing lost
            store.Archive("country_1", false);
            Assert.Equal(2, Rooms());

            Assert.Equal(1, store.DeleteRoom("country_2"));
            Assert.Empty(store.InsertNew([M("country_2", 5)]));        // a late push is dropped
            Assert.Equal("deleted", store.States()["country_2"].State);
            store.Archive("country_2", false);                        // followed again
            Assert.Single(store.InsertNew([M("country_2", 5)]));
            Assert.Equal(2, store.RoomStats("country_1").Messages);
        }
        finally { TestDb.Drop(path); }
    }

    [Fact]
    public void VipHistoryLogsLevelAndTimeChangesOnly()
    {
        var t = Insights.VipSince.AddHours(1);
        PlayerProfile P(int h, int lv, long end) => new() { Uid = "1", TakenAt = t.AddHours(h), SvipLevel = lv, SvipEnd = end };
        var rows = Insights.VipHistory([
            new PlayerProfile { Uid = "1", TakenAt = Insights.VipSince.AddDays(-1) },   // before capture: ignored
            P(0, 12, 0), P(1, 12, 0), P(2, 12, 100), P(3, 13, 100), P(4, 13, 200), P(5, 13, 200)]);
        Assert.Equal(["first", "level", "until"], rows.Select(r => (string)r["kind"]!));
        Assert.Equal(12, rows[1]["from"]);
        Assert.Equal(13, rows[1]["level"]);
    }
}
