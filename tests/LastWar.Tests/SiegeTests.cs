using LastWar.Storage;
using Xunit;

public class SiegeTests
{
    static List<Siege.Member> Side(int n, double hero, double on = 1) =>
        Enumerable.Range(0, n).Select(_ => new Siege.Member(hero, on, on)).ToList();

    [Fact]
    public void Strength_numbers_and_turnout_decide_the_siege()
    {
        // Nobody home: the building is open.
        Assert.Equal(1, Siege.Run(Side(10, 100e6), Side(100, 100e6, on: 0), 100).Breaks);
        // Twice as strong, everyone on: breaks through; the reverse never does.
        Assert.True(Siege.Run(Side(100, 200e6), Side(100, 100e6), 100).Breaks > 0.9);
        Assert.True(Siege.Run(Side(100, 100e6), Side(100, 200e6), 100).Breaks < 0.1);
        // Same alliance defending: fewer defenders on to re-garrison makes it easier.
        var full = Siege.Run(Side(100, 100e6), Side(100, 100e6), 100).Breaks;
        var thin = Siege.Run(Side(100, 100e6), Side(100, 100e6, on: 0.3), 100).Breaks;
        Assert.True(thin > full);
    }
}
