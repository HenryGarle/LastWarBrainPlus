using LastWar.Storage;

namespace LastWar.Tests;

public class ServerHistoryTests
{
    static string T(int h) => DateTimeOffset.FromUnixTimeSeconds(1_790_000_000 + h * 3600L).ToString("O");

    // Created on 1958, already homed on 1967 when first seen (early move, estimated), a base seen
    // on 1970 while home stayed 1967 (visit, not a move), then a transfer to 1972 whose moment is
    // narrowed by a base sighting on 1972 before the next profile.
    [Fact]
    public void StoryTellsVisitsFromTransfers()
    {
        var obs = new List<(string, int, bool, string)>
        {
            (T(0), 1967, true, "profile"),
            (T(1), 1970, false, "map"),
            (T(2), 1967, true, "profile"),
            (T(5), 1972, false, "chat"),
            (T(9), 1972, true, "profile"),
        };
        var story = ServerHistory.Story("1234567890001958", obs).ToList();
        Assert.Equal(["created", "early", "visit", "transfer"], story.Select(e => e.Kind));
        Assert.True(story[1].Estimated);
        Assert.Equal((1967, 1970), (story[2].From, story[2].To));
        var move = story[3];
        Assert.Equal((1967, 1972), (move.From, move.To));
        Assert.Equal(T(2), move.FromAt);   // last seen at the old home
        Assert.Equal(T(5), move.ToAt);     // base seen on the new server before the profile said so
    }
}
