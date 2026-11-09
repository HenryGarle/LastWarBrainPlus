using LastWar.Storage;

namespace LastWar.Tests;

public class ChatStatsTests
{
    // A flag is ONE emoji (two regional letters), a skin tone is not counted on its own, game
    // emojis are Private Use Area characters, and a dice sticker records its roll.
    [Fact]
    public void CountsFlagsGameEmojiAndDice()
    {
        var path = TestDb.Create();
        try
        {
            new PlayerLatest(path).EnsureSchema();
            new ChatStore(path).EnsureSchema();
            using (var c = Db.Open(path))
            {
                using var cmd = c.CreateCommand();
                var now = DateTimeOffset.UtcNow.ToString("O");
                cmd.CommandText = $"""
                    INSERT INTO chat_message (room_id, seq_id, sender_uid, sender_name, sender_al, msg, sent_at) VALUES
                        ('country_1967', 1, 'u1', 'Ann', 'SaDe', 'allez 🇫🇷👍🏻 {""}', '{now}'),
                        ('country_1967', 2, 'u1', 'Ann', 'SaDe', '<lwSticker:5:6:>', '{now}'),
                        ('country_1972', 3, 'system', '', '', 'gift', '{now}');
                    """;
                cmd.ExecuteNonQuery();
            }
            var s = new Insights(path).ChatStats(7);
            var t = (Dictionary<string, object?>)s["totals"]!;
            Assert.Equal((3, 2L, 2L, 1L), ((int)t["messages"]!, (long)t["unicodeEmoji"]!, (long)t["gameEmoji"]!, (long)t["stickers"]!));
            Assert.Equal(1, ((int[])s["dice"]!)[5]);
            var talkers = (List<Dictionary<string, object?>>)s["talkers"]!;
            Assert.Equal("u1", Assert.Single(talkers)["uid"]);   // "system" is volume, not a person
        }
        finally { TestDb.Drop(path); }
    }
}
