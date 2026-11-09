using LastWar.Core.Model;
using LastWar.Protocol.Sfs;

namespace LastWar.Client.Game;

/// <summary>
/// get.user.info.multi on the game socket: profiles for any uids on any server, from any account
/// (a guest will do). Measured: 2,000 uids answer in under 300 ms, so a whole server range is a
/// few dozen requests. The command id sits in Chat/WebMessage/Config/ChatMsgDefines
/// ("GetUserInfoMulti") but it is an SFS command, not a chat-gateway one.
///
///     get.user.info.multi { allservers:"1", uids:[...] } -> { uids:[{ uid, name, serverId,
///       currentServer, srcServer, crossFightSrcServerId?, allianceId, allianceAbbrName, power, ...}] }
///
/// Its power is the same number world.get.detail.new and al.rank report (500 checked: 357
/// identical, the rest within normal day-to-day drift).
/// </summary>
public static class ProfileLookup
{
    public const string Command = "get.user.info.multi";

    public static SfsObject Request(IEnumerable<string> uids)
    {
        var arr = new SfsArray();
        foreach (var u in uids) arr.Add(SfsValue.Of(u));
        var p = new SfsObject();
        p.PutUtfString("allservers", "1");
        p.PutArray("uids", arr);
        return p;
    }

    /// <summary>
    /// The server a player is FROM. On teleport day (2026-09-26) serverId and currentServer both
    /// follow the base to the server it teleported to, crossFightSrcServerId stays 0, and srcServer
    /// keeps the real home - girlsgirl: serverId 1959, srcServer 1967, still in 1967's BTW. A real
    /// transfer moves srcServer too (her 1979 -> 1967 move changed both). Before teleports opened,
    /// srcServer equalled serverId for 177,783 of 177,810 players; the other 27 were bases parked on
    /// another server, and their alliances belong to the srcServer. So: cross-fight source if set,
    /// else srcServer, else serverId.
    /// </summary>
    public static int HomeOf(int serverId, int srcServer, int crossFightSrc) =>
        crossFightSrc > 0 ? crossFightSrc : srcServer > 0 ? srcServer : serverId;

    public static List<PlayerProfile> Parse(SfsObject reply, DateTimeOffset takenAt, string source)
    {
        var outp = new List<PlayerProfile>();
        if (!reply.TryGetArray("uids", out var arr)) return outp;
        foreach (var o in arr.Objects())
        {
            var uid = o.GetString("uid");
            if (uid.Length == 0) continue;
            var serverId = Int(o, "serverId");
            var cross = Int(o, "crossFightSrcServerId");
            var src = Int(o, "srcServer");
            outp.Add(new PlayerProfile
            {
                Uid = uid,
                TakenAt = takenAt,
                Name = o.GetString("name"),
                HomeServer = HomeOf(serverId, src, cross),
                ServerId = serverId,
                CurrentServer = Int(o, "currentServer"),
                SrcServer = Int(o, "srcServer"),
                CrossFightSrcServer = cross,
                AllianceId = o.GetString("allianceId"),
                AllianceTag = o.GetString("allianceAbbrName"),
                AllianceName = o.GetString("allianceName"),
                Power = Long(o, "power"),
                ArmyPower = Long(o, "armyPower"),
                ArmyKill = Long(o, "armyKill"),
                Level = Int(o, "mainBuildingLevel") is > 0 and var hq ? hq : Int(o, "level"),
                Lang = o.GetString("lang"),
                Country = o.GetString("country"),
                LastSeen = Long(o, "lastUpdateTime") is > 0 and var seen ? DateTimeOffset.FromUnixTimeSeconds(seen) : null,
                SvipLevel = Int(o, "svipLevel"),
                SvipEnd = Long(o, "svipEndTime"),
                Flag = o.GetString("countryflag"),
                MonthCardEnd = Long(o, "monthCardEndTime"),
                ThumbsUp = Long(o, "thumbsUpCount"),
                Source = source,
            });
        }
        return outp;
    }

    public static async Task<List<PlayerProfile>> FetchAsync(
        LastWarClient client, IReadOnlyCollection<string> uids, DateTimeOffset takenAt, string source,
        TimeSpan timeout, CancellationToken ct = default)
    {
        if (uids.Count == 0) return [];
        var reply = await client.CallAsync(Command, Request(uids), timeout, ct).ConfigureAwait(false);
        return Parse(reply, takenAt, source);
    }

    // Numbers arrive as whatever width the server chose, sometimes as strings.
    static int Int(SfsObject o, string k) => int.TryParse(o.Scalar(k), out var v) ? v : 0;
    static long Long(SfsObject o, string k) => long.TryParse(o.Scalar(k), out var v) ? v : 0;
}
