namespace LastWar.Storage;

/// <summary>
/// The server leaderboards worth keeping (rank.get types), what each one ranks, and which field
/// holds the value. Established by probing and confirmed in game - see AGENTS.md "SERVER
/// LEADERBOARDS" and "BOARD IDENTITIES". 23 and 24 are duplicates of 3; 6-12, 16, 17, 21, 22 are
/// empty outside events. 18-20 publish no value at all - the rank IS the data.
/// </summary>
public static class Boards
{
    public sealed record Board(int Type, string Name, string ValueField, string[] Extra, bool Alliance, int Depth);

    public static readonly IReadOnlyList<Board> All =
    [
        new(1, "Alliance kills", "armyKill", ["nums", "fightpower"], true, 50),
        new(2, "Alliance power", "fightpower", ["nums", "armyKill", "leaderUid"], true, 50),
        new(3, "Kills", "armyKill", ["power"], false, 100),
        new(4, "Total power", "power", [], false, 200),
        new(5, "HQ level (first to max)", "buildingPower", ["level", "updateTime"], false, 100),
        new(13, "Hero power", "heroPower", [], false, 200),
        new(14, "PvE stage", "pveMaxStage", ["pveMaxStageTime"], false, 100),
        // The sort key is "power" (the hero's own); heroHighestPower is missing on some rows.
        new(15, "Strongest hero", "power", ["heroId", "heroHighestPower"], false, 100),
        new(18, "Tank Battlefield", "", [], false, 100),
        new(19, "Aircraft Battlefield", "", [], false, 100),
        new(20, "Missile Battlefield", "", [], false, 100),
    ];

    public static Board? Find(int type) => All.FirstOrDefault(b => b.Type == type);

    public static string DefaultTypes => string.Join(",", All.Select(b => b.Type));
}
