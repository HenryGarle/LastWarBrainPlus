using LastWar.Storage;

namespace LastWar.Tests;

public class SeasonSeedsTests
{
    // lw_season.camp_sever_local codes from table 40210 (docs/season-groups.md).
    [Theory]
    [InlineData("lw:3f133a1c44321a3736", "1974,1958")]
    [InlineData("lw:3f133b1144321a353b", "1969,1975")]
    [InlineData("lw:3c1a3c1f443113333d", "2017,2013")]
    [InlineData("lw:3d1d71190a3b", "")]   // a shorter code (other format)
    [InlineData("", "")]
    public void DecodesTheTwoSeededServers(string code, string seeds) => Assert.Equal(seeds, ServerRegistry.Seeds(code));
}
