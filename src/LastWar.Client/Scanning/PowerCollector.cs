using LastWar.Core.Model;
using LastWar.Protocol.Sfs;

namespace LastWar.Client.Scanning;

public sealed record PowerScanProgress(int Done, int Total, int Accounts, string Stage);

/// <summary>
/// Collects a power reading for every account on a server, which the map itself cannot
/// provide.
///
/// Two routes, mixed deliberately. al.rank returns a whole alliance for one request, so
/// it covers members cheaply and also catches members whose base the map sweep missed.
/// world.get.detail.new costs one request per tile and is the ONLY route for players in
/// no alliance - on 1967 that is 69% of the server, so it cannot be skipped.
///
/// The two sources report the SAME power number; verified against overlapping accounts,
/// exact match. That is what makes mixing them in one snapshot safe.
/// </summary>
public sealed class PowerCollector(LastWarClient client)
{
    public TimeSpan Gap { get; init; } = TimeSpan.FromMilliseconds(60);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(6);

    public async Task<IReadOnlyCollection<PowerSample>> CollectAsync(
        ServerId server, IReadOnlyCollection<MapTile> tiles, DateTimeOffset takenAt,
        IProgress<PowerScanProgress>? progress = null, CancellationToken ct = default,
        IDictionary<PlayerUid, PowerSample>? into = null)
    {
        // "into" lets a caller keep what was collected when the connection drops part way, and
        // resume with only the players still missing instead of starting the server over.
        var samples = into ?? new Dictionary<PlayerUid, PowerSample>();
        var alliances = tiles.Where(t => t.HasAlliance).Select(t => t.AllianceId).Distinct().ToList();

        // al.rank answers with members but not with the alliance's own tag. The tiles already
        // carry it, so stamp it from them - otherwise every roster sample is stored untagged and
        // an activity check cannot say which alliance anyone is in.
        var tagOf = tiles.Where(t => t.HasAlliance)
            .GroupBy(t => t.AllianceId)
            .ToDictionary(g => g.Key, g => g.First().AllianceTag);
        var solo = tiles.Where(t => !t.HasAlliance).ToList();
        var total = alliances.Count + solo.Count;
        var done = 0;

        foreach (var allianceId in alliances)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var s in await RosterAsync(server, allianceId, takenAt, ct).ConfigureAwait(false))
                samples[s.Uid] = s with { AllianceTag = tagOf.GetValueOrDefault(allianceId, "") };
            progress?.Report(new PowerScanProgress(++done, total, samples.Count, "rosters"));
            await Task.Delay(LastWar.Client.Jitter.Of(Gap), ct).ConfigureAwait(false);
        }

        foreach (var tile in solo)
        {
            ct.ThrowIfCancellationRequested();
            if (await DetailAsync(server, tile, takenAt, ct).ConfigureAwait(false) is { } s)
                samples[s.Uid] = s;
            progress?.Report(new PowerScanProgress(++done, total, samples.Count, "tile detail"));
            await Task.Delay(LastWar.Client.Jitter.Of(Gap), ct).ConfigureAwait(false);
        }
        return [.. samples.Values];
    }

    async Task<IReadOnlyList<PowerSample>> RosterAsync(
        ServerId server, string allianceId, DateTimeOffset takenAt, CancellationToken ct)
    {
        var p = new SfsObject();
        p.PutUtfString("allianceId", allianceId);
        var reply = await client.TryCallAsync("al.rank", p, Timeout, ct).ConfigureAwait(false);
        if (reply is null || reply.GetString("errorCode").Length > 0) return [];

        var outp = new List<PowerSample>();
        foreach (var key in new[] { "list", "memberList", "members" })
        {
            if (!reply.TryGetArray(key, out var arr)) continue;
            foreach (var m in arr.Objects())
            {
                var uid = m.GetString("uid");
                if (uid.Length == 0) continue;
                outp.Add(new PowerSample
                {
                    TakenAt = takenAt,
                    Server = server,
                    Uid = new PlayerUid(uid),
                    Power = m.GetLong("power"),
                    Name = m.GetString("name"),
                    AllianceId = allianceId,
                    KillArmy = m.GetLong("armyKill"),
                    HqLevel = m.GetInt("mainCityLv"),
                    Source = PowerSource.AllianceRoster,
                });
            }
            if (outp.Count > 0) break;
        }
        return outp;
    }

    async Task<PowerSample?> DetailAsync(
        ServerId server, MapTile tile, DateTimeOffset takenAt, CancellationToken ct)
    {
        var p = new SfsObject();
        p.PutInt("point", tile.PointId);
        p.PutInt("serverId", server.Value);
        p.PutInt("worldId", 0);
        p.PutInt("previewAssistance", 0);
        p.PutInt("pointType", tile.PointType);
        p.PutUtfString("uid", "");      // optional: point + serverId + pointType is enough

        var reply = await client.TryCallAsync("world.get.detail.new", p, Timeout, ct).ConfigureAwait(false);
        if (reply is null || reply.GetString("errorCode").Length > 0) return null;

        var uid = reply.GetString("uid");
        if (uid.Length == 0) return null;

        return new PowerSample
        {
            TakenAt = takenAt,
            Server = server,
            Uid = new PlayerUid(uid),
            Power = reply.GetLong("power"),
            Name = reply.GetString("name"),
            AllianceId = reply.GetString("allianceId"),
            AllianceTag = reply.GetString("afn"),
            AllianceName = reply.GetString("alName"),
            KillArmy = reply.GetLong("killArmy"),
            CareerLevel = reply.GetInt("careerLv"),
            HqLevel = tile.HqLevel,
            PointId = tile.PointId,
            Source = PowerSource.TileDetail,
        };
    }
}
