using LastWar.Storage;

namespace LastWar.Tests;

public class ServerRegistryTests
{
    // The game's war and group tables write sides as "a;b|c-d": both forms expand, and the blocks
    // a server belongs to come back newest first; @-sets print back as compact ranges.
    // Servers joined by any 4 v 4 war, transitively, are one war group; duels (type 4) and 9000+ hosts do not join groups.
    [Fact]
    public void WarGroupsComeFromTheWars()
    {
        var path = TestDb.Create();
        try
        {
            var reg = new ServerRegistry(path);
            reg.EnsureSchema();
            void War(long id, long type, string servers) =>
                Db.Exec(path, "INSERT INTO game_zone_war (id, type, open_time, server_ids) VALUES (@i, @t, '2026-06-15 00:00:00', @s);", ("@i", id), ("@t", type), ("@s", servers));
            War(1, 8, "1957;1958|1959;1960");
            War(2, 8, "1960;1961|1962;9001");
            War(3, 8, "1989;1990|1991;1992");
            War(4, 4, "1962;1963|1989;1990");   // a duel across the two: not a group link
            var groups = reg.WarGroups();
            Assert.Equal(["1957-1962", "1989-1992"], groups.Select(g => g.Id));
            Assert.DoesNotContain(9001, groups[0].Servers);
        }
        finally { TestDb.Drop(path); }
    }

    [Fact]
    public void SidesBlocksAndRanges()
    {
        var sides = ServerRegistry.Sides("1955;1956|1957-1959");
        Assert.Equal([1955, 1956], sides[0]);
        Assert.Equal([1957, 1958, 1959], sides[1]);
        Assert.Equal("1957-1959,1970", ServerRegistry.Compact([1957, 1958, 1959, 1970]));
        Assert.Equal("2026-01-19", ServerRegistry.DateOf("2026/1/19"));

        var blocks = new List<ServerRegistry.Block>
        {
            new("1983-1998", 1983, 1998, [], "2026-01-26", "2026-01-26", [0]),
            new("1957-2020", 1957, 2020, ["1893-1956"], "2026-06-08", "2026-09-21", [1, 2]),
        };
        Assert.Equal("1957-2020", ServerRegistry.BlockOf(blocks, 1990)!.Id);   // newest wins over the old 16-block
        Assert.Null(ServerRegistry.BlockOf(blocks, 2100));
    }
}
