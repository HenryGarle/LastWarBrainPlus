using LastWar.Storage;

namespace LastWar.Tests;

public class CompareTests
{
    // "Risen since last board" is the activity measure on the top 200: only a rise counts, and
    // only for players on both snapshots. Totals and the entry bar come off the same board.
    [Fact]
    public void ServerBoardsGiveTotalsEntryBarAndRisers()
    {
        var path = TestDb.Create();
        try
        {
            new LastWarDb(path).EnsureSchema();
            var wave = new WaveStore(path);
            wave.EnsureSchema();
            new ProfileStore(path).EnsureSchema();
            var t0 = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
            wave.InsertBoard(t0, 1967, 4, [new BoardRow(1, "a", "A", "", 300), new BoardRow(2, "b", "B", "", 200), new BoardRow(3, "c", "C", "", 100)]);
            // a rose, b fell, c left the board, d is new.
            wave.InsertBoard(t0.AddDays(1), 1967, 4, [new BoardRow(1, "a", "A", "", 350), new BoardRow(2, "b", "B", "", 190), new BoardRow(3, "d", "D", "", 150)]);

            var r = new Insights(path).CompareServers([1967]);
            var metrics = (List<Dictionary<string, object?>>)r["metrics"]!;
            double Last(string key) =>
                (double)((Dictionary<string, List<Dictionary<string, object?>>>)metrics.First(m => (string)m["key"]! == key)["series"]!)["1967"][^1]["v"]!;
            Assert.Equal(690, Last("b4_sum"));
            Assert.Equal(350, Last("b4_1"));
            Assert.Equal(150, Last("b4_last"));
            Assert.Equal(1, Last("b4_rose"));
        }
        finally { TestDb.Drop(path); }
    }
}
