using LastWar.Operations;

namespace LastWar.Tests;

public class AccountPolicyTests
{
    // A privileged account (a player's own main) runs only operations that name it: chores on the
    // account itself. Every scan, sweep and read must refuse it.
    [Fact]
    public void PrivilegedRunsOnlyWhatIsMarkedForIt()
    {
        var allowed = Operations.Operations.All.Where(o => o.NeedsSession && o.Refuses(AccountTier.Privileged) is null)
            .Select(o => o.Id).ToHashSet();
        Assert.All(allowed, id => Assert.Matches("^(base|alliance\\.(help|gifts|tech)|mail|vip|visitors|daily|chat\\.dm|chat\\.share\\.point|servers\\.own)\\.", id + "."));
        Assert.DoesNotContain("scan.map", allowed);
        Assert.DoesNotContain("board.snapshot", allowed);
        Assert.DoesNotContain("alliance.rank", allowed);
    }

    [Fact]
    public void GuestsCannotRunServiceOnlyWork()
    {
        var map = Operations.Operations.Find("scan.map")!;
        Assert.NotNull(map.Refuses(AccountTier.Guest));
        Assert.Null(map.Refuses(AccountTier.Service));
        Assert.Null(Operations.Operations.Find("board.snapshot")!.Refuses(AccountTier.Guest));
        // Operations that bring their own guests need no runner at all, so nothing is refused.
        Assert.Null(Operations.Operations.Find("kingdom.positions")!.Refuses(AccountTier.Privileged));
    }
}
