using Google.Protobuf;
using LastWar.Protocol.Sfs;

namespace LastWar.Client.Scanning;

/// <summary>
/// A Secret Task (hero dispatch mission) on the world map: point type 17 (HERO_DISPATCH), detail in
/// WorldPointInfo field 10 HeroDispatchMissionPointInfo - owner, task config (lw_dispatch_tasks: color
/// 3 SR / 4 SSR / 5 UR, level = stars), when it completes, who has plundered (stealList, max
/// steal_maxtimes = 3) or assisted it. Times are as the server sends them (ms since epoch).
/// </summary>
public sealed record SecretTask(
    int Server, int PointId, int X, int Y, int CfgId, string OwnerUid, string AllianceId,
    long CompletionTime, long ActEndTime, int Steals, int Assists, int Heroes, bool Rewarded);

public static class SecretTasks
{
    /// <summary>The secret tasks in one world.get.block reply (the scanned server's own only).</summary>
    public static IEnumerable<SecretTask> Read(SfsObject reply, int server)
    {
        var blocks = reply.TryGetArray("serverPointArr", out var arr) ? arr.Objects().ToList() : [reply];
        foreach (var block in blocks)
        {
            if (!block.TryGetArray("points", out var list)) continue;
            foreach (var bytes in list.ByteArrays())
            {
                LastWar.Client.Proto.WorldPointInfo p;
                try { p = LastWar.Client.Proto.WorldPointInfo.Parser.ParseFrom(bytes); }
                catch (InvalidProtocolBufferException) { continue; }
                if (p.PointType != 17 || p.HeroDispatchMissionPointInfo is not { } m) continue;
                if (p.ServerId > 0 && p.ServerId != server) continue;
                yield return new(server, p.Id, (p.Id - 1) % 1000, (p.Id - 1) / 1000, m.CfgId, m.OwnerUid, m.AllianceId,
                    m.CompletionTime, m.ActEndTime, m.StealList.Count, m.AccList.Count, m.HeroList.Count, m.Rewarded != 0);
            }
        }
    }
}
