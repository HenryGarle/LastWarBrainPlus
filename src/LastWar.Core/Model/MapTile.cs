namespace LastWar.Core.Model;

/// <summary>
/// One occupied map tile, as returned by world.get.block. Carries identity but NOT
/// power - the map has no power field, which is why an activity baseline needs a
/// second pass (see <see cref="PowerSample"/>).
/// </summary>
public sealed record MapTile
{
    public required ServerId Server { get; init; }
    public required int PointId { get; init; }
    public required int PointType { get; init; }
    public required PlayerUid OwnerUid { get; init; }
    public string Name { get; init; } = "";
    public string AllianceId { get; init; } = "";
    public string AllianceTag { get; init; } = "";
    public int HqLevel { get; init; }
    public int BuildId { get; init; }

    /// <summary>Tile coordinates. pointId is 1-based row-major: id = x + y*worldSize + 1.</summary>
    public (int X, int Y) Coordinates(int worldSize = 1000)
    {
        var t = PointId - 1;
        return (t % worldSize, t / worldSize);
    }

    public bool HasAlliance => !string.IsNullOrEmpty(AllianceId);
}
