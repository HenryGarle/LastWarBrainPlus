using LastWar.Client;
using LastWar.Client.Auth;

namespace LastWar.Tests;

public class BlockedAccountTests
{
    // Ghost (Henry's personal account) and the banned Sadmin are refused before anything touches the network.
    // Waits and schedules never land on a fixed beat: spread within +-spread, and not all the same.
    [Fact]
    public void JitterSpreadsWithinBounds()
    {
        var t = TimeSpan.FromMinutes(60);
        var got = Enumerable.Range(0, 200).Select(_ => Jitter.Of(t, 0.2).TotalMinutes).ToList();
        Assert.All(got, m => Assert.InRange(m, 48, 72));
        Assert.True(got.Distinct().Count() > 150);
        Assert.Equal(TimeSpan.Zero, Jitter.Of(TimeSpan.Zero));
    }

    [Theory]
    [InlineData("1001690072001967")]
    [InlineData("2359053554001967")]
    public async Task NeverLogsInAsBlockedAccounts(string uid)
    {
        var config = new SessionConfig { GameUid = uid, AccessToken = "x", Ip = "127.0.0.1", Port = 1, Zone = "APS1967" };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LastWarClient.ConnectAsync(config, GameIdentity.LoadOrCreate(Path.GetTempPath())));
        Assert.Contains("refusing to log in", ex.Message);
        Assert.Null(BlockedAccounts.Why("2093679368000647"));   // lastwar_1 is not blocked
    }
}
