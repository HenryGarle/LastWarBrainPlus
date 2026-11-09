namespace LastWar.Core.Model;

/// <summary>
/// A map point that is not a player base but matters to alliances: a city, stronghold, trade
/// post, alliance building (Alliance Center, furnace, outpost...) or a Marshal's Guard trap.
/// PointType is the client's WorldPointType (apk/src/WorldPointType.cs).
/// </summary>
public sealed record MapFeature
{
    public required ServerId Server { get; init; }
    public required int PointId { get; init; }
    public required int PointType { get; init; }
    /// <summary>buildId for alliance buildings, cfgId for Marshal's Guard, city/stronghold/trade id otherwise.</summary>
    public int BuildId { get; init; }
    public int Level { get; init; }
    public string AllianceId { get; init; } = "";
    public string AllianceTag { get; init; } = "";
    public string AllianceName { get; init; } = "";
    /// <summary>City name, or the player holding a trade post / mini-game city.</summary>
    public string Label { get; init; } = "";
    public string HolderUid { get; init; } = "";
}
