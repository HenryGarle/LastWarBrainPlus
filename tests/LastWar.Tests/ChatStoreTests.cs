using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class ChatStoreTests
{
    static ChatMessage M(long seq, string text = "hi") => new()
    {
        RoomId = "country_1967", SeqId = seq, Text = text, SenderUid = "1234567890001967",
        SentAt = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000 + seq),
    };

    // A push, a poll and a catch-up all hand the same messages in; only the first copy counts
    // and the stored text is never replaced.
    [Fact]
    public void InsertNewReturnsOnlyUnseenAndNeverOverwrites()
    {
        var path = TestDb.Create();
        try
        {
            var store = new ChatStore(path);
            store.EnsureSchema();
            Assert.Equal(2, store.InsertNew([M(1), M(2)]).Count);
            var fresh = store.InsertNew([M(2, "changed"), M(3)]);
            Assert.Equal([3L], fresh.Select(m => m.SeqId));
            Assert.Equal(3, store.NewestSeqId("country_1967"));

            var page = store.Page("country_1967", beforeSeq: 3, limit: 10);
            Assert.Equal([2L, 1L], page.Select(m => m.SeqId));
            Assert.Equal("hi", page[0].Text);
        }
        finally { TestDb.Drop(path); }
    }

    // Lookups are kept as history; the newest one is the current view. Translations sit beside
    // the message and never touch it.
    [Fact]
    public void ProfilesKeepHistoryAndTranslationsLeaveMessagesAlone()
    {
        var path = TestDb.Create();
        try
        {
            var store = new ChatStore(path);
            store.EnsureSchema();
            var t0 = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
            var profiles = new ProfileStore(path);
            profiles.EnsureSchema();
            PlayerProfile U(DateTimeOffset at, int home, int current) =>
                new() { Uid = "1234567890001958", TakenAt = at, HomeServer = home, CurrentServer = current, Source = "sweep" };
            Assert.Equal(1, profiles.Insert([U(t0, 1958, 1958)]));
            Assert.Equal(1, profiles.Insert([U(t0.AddHours(7), 1972, 1990)]));
            Assert.Equal(0, profiles.Insert([U(t0.AddHours(7), 1, 1)]));   // same moment again: kept, not replaced
            var now = profiles.Latest(["1234567890001958"])["1234567890001958"];
            Assert.Equal(1972, now.HomeServer);
            Assert.True(now.Visiting);                                      // based on 1990, from 1972
            Assert.Equal(1958, profiles.Latest(null, null, t0.AddHours(1))["1234567890001958"].HomeServer);
            Assert.Equal(2, profiles.History("1234567890001958").Count);

            store.InsertNew([M(5, "hola")]);
            store.SaveTranslation("country_1967", 5, "en", "hello", "es");
            Assert.Equal("hello", store.Translations("country_1967", "en", 1, 9)[5].Text);
            Assert.Equal("hola", store.Get("country_1967", 5)!.Text);
        }
        finally { TestDb.Drop(path); }
    }
}