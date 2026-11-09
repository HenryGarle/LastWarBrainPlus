using LastWar.Storage;

namespace LastWar.Tests;

// The same cases as web/src/explorer/hive.check.mjs, with the numbers the JS gives - the two must agree.
public class HiveTests
{
    static List<Hive.Base> Grid(params (int, int)[] skip)
    {
        var o = new List<Hive.Base>();
        for (var i = 0; i < 8; i++) for (var j = 0; j < 8; j++)
            if (!skip.Contains((i, j))) o.Add(new(500 + i * 4, 300 + j * 4));
        return o;
    }

    [Fact]
    public void ScoresMatchTheMapPage()
    {
        var perfect = Hive.Rate(Grid())!;
        Assert.Equal((100, 4.0, 0), (perfect.Value, perfect.Pitch, perfect.Holes));
        var gapped = Hive.Rate(Grid((4, 4)))!;
        Assert.True(gapped.Gap); Assert.Equal((100, 0), (gapped.Value, gapped.Holes));
        var holed = Hive.Rate(Grid((4, 4), (1, 6)))!;
        Assert.Equal((97, 1), (holed.Value, holed.Holes));

        var seed = 7L; double Rnd() => (seed = seed * 16807 % 2147483647) / 2147483647.0;
        var scatter = Enumerable.Range(0, 60).Select(_ => new Hive.Base(500 + (int)Math.Round(Rnd() * 40), 300 + (int)Math.Round(Rnd() * 40))).ToList();
        Assert.Equal(12, Hive.Rate(Hive.HiveOf(scatter))!.Value);
        Assert.Equal(64, Hive.HiveOf([.. Grid(), new(900, 900)]).Count);
    }
}