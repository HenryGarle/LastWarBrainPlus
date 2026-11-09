using LastWar.Storage;

namespace LastWar.Tests;

public class HandSeasonGroupTests
{
    // A hand-entered group shows next to the game's reading, and drops out per server once the game reports that config.
    [Fact]
    public void HandRowsShowUntilTheGameReportsThem()
    {
        var path = TestDb.Create();
        try
        {
            var reg = new ServerRegistry(path);
            reg.EnsureSchema();
            reg.NoteSeasonCfg(1967, 1322);
            reg.SetHandSeasonGroup(1500, [1967, 1969, 1969]);

            List<(long, long, string)> Rows() => [.. new Insights(path).SeasonGroups(1957, 2020)
                .Select(r => ((long)r["server"]!, (long)r["cfg"]!, (string)r["by"]!)).OrderBy(x => x)];
            Assert.Equal([(1967, 1322, "game"), (1967, 1500, "hand"), (1969, 1500, "hand")], Rows());

            reg.NoteSeasonCfg(1967, 1500);   // the game now says 1967 is on 1500
            Assert.Equal([(1967, 1500, "game"), (1969, 1500, "hand")], Rows());

            reg.SetHandSeasonGroup(1500, []);
            Assert.Equal([(1967, 1500, "game")], Rows());
        }
        finally { TestDb.Drop(path); }
    }
}
