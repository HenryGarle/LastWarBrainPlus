namespace LastWar.Core.Model;

/// <summary>Where a power reading came from. Both report the SAME number - verified
/// against four overlapping accounts, exact match - so they mix freely in a snapshot.</summary>
public enum PowerSource
{
    /// <summary>al.rank: one request per alliance, covers every member.</summary>
    AllianceRoster,
    /// <summary>world.get.detail.new: one request per tile, the only route for players in no alliance.</summary>
    TileDetail,
    /// <summary>get.user.info.multi: up to thousands of players per request, any server, with the
    /// player's HOME server rather than the server whose map they were seen on.</summary>
    Profile,
}

/// <summary>One account's power at one moment.</summary>
public sealed record PowerSample
{
    public required DateTimeOffset TakenAt { get; init; }
    public required ServerId Server { get; init; }
    public required PlayerUid Uid { get; init; }
    public required long Power { get; init; }
    public string Name { get; init; } = "";
    public string AllianceId { get; init; } = "";
    public string AllianceTag { get; init; } = "";
    public string AllianceName { get; init; } = "";
    public long KillArmy { get; init; }
    public int CareerLevel { get; init; }
    public int HqLevel { get; init; }
    public int? PointId { get; init; }
    public PowerSource Source { get; init; }
}
