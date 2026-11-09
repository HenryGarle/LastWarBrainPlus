using LastWar.Core.Model;
using LastWar.Protocol.Sfs;

namespace LastWar.Client.Game;

/// <summary>
/// Guest-safe reads about any alliance or server (all cross-server; probed 2026-09-27):
///
///   al.rank {allianceId}                      members: rank, power, kills, HQ, donations, joined
///   get.al.info {allianceId}                  the alliance card: fight power, engagement, leader
///   city.war.get.info {serverId}              who holds each of the server's cities, and since when
///   season.migrate.server.info {serverId}     server score/state, president, first lady, inflow
///   get.history.king.record {serverId, page}  every past president, 10 a page
/// </summary>
/// <summary>Just enough protobuf to read the game's base64 lists: fields of varints and length-delimited bytes.</summary>
public static class Proto
{
    public readonly record struct Field(int No, long Value, byte[]? Bytes);

    public static IEnumerable<Field> Fields(byte[] b)
    {
        var i = 0;
        long Varint()
        {
            long v = 0; var shift = 0;
            while (i < b.Length) { var x = b[i++]; v |= (long)(x & 0x7f) << shift; if (x < 0x80) break; shift += 7; }
            return v;
        }
        while (i < b.Length)
        {
            var key = Varint();
            var no = (int)(key >> 3);
            switch (key & 7)
            {
                case 0: yield return new Field(no, Varint(), null); break;
                case 2:
                    var len = (int)Varint();
                    if (len < 0 || i + len > b.Length) yield break;
                    yield return new Field(no, 0, b[i..(i + len)]);
                    i += len;
                    break;
                case 1: i += 8; break;
                case 5: i += 4; break;
                default: yield break;   // groups: not used by these lists
            }
        }
    }
}

public sealed class Intel(LastWarClient client)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(8);

    async Task<SfsObject?> Ask(string cmd, SfsObject p, CancellationToken ct)
    {
        var reply = await client.TryCallAsync(cmd, p, Timeout, ct).ConfigureAwait(false);
        return reply is null || reply.Scalar("errorCode") is { Length: > 0 } e && e != "0" ? null : reply;
    }

    static SfsObject P(string k, string v) { var o = new SfsObject(); o.PutUtfString(k, v); return o; }
    static SfsObject P(string k, int v) { var o = new SfsObject(); o.PutInt(k, v); return o; }
    static int I(SfsObject o, string k) => int.TryParse(o.Scalar(k), out var v) ? v : 0;
    static long L(SfsObject o, string k) => long.TryParse(o.Scalar(k), out var v) ? v : 0;
    static DateTimeOffset? Ms(SfsObject o, string k) => L(o, k) is > 0 and var v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;

    public async Task<AllianceRoster?> RosterAsync(string allianceId, DateTimeOffset at, CancellationToken ct = default)
    {
        if (await Ask("al.rank", P("allianceId", allianceId), ct).ConfigureAwait(false) is not { } r) return null;
        var members = new List<RosterMember>();
        if (r.TryGetArray("list", out var arr))
            foreach (var m in arr.Objects())
                if (m.GetString("uid") is { Length: > 0 } uid)
                    members.Add(new RosterMember
                    {
                        Uid = uid, Name = m.GetString("name"), Rank = I(m, "rank"), Power = L(m, "power"),
                        Kills = L(m, "armyKill"), Hq = I(m, "mainCityLv"),
                        DonateToday = L(m, "todayProgress"), DonateWeek = L(m, "weeklyProgress"),
                        DonatedAt = Ms(m, "donateTime"), WeekDonatedAt = Ms(m, "weeklyDonateTime"),
                        JoinedAt = Ms(m, "joinTime"), RankSince = Ms(m, "rankTime"), MonthCardEnd = L(m, "monthCardEndTime"),
                        Server = I(m, "serverId"), CurrentServer = I(m, "curServerId"),
                    });
        var officials = new List<(string, int)>();
        if (r.TryGetArray("allianceOfficialArr", out var offs))
            foreach (var o in offs.Objects()) officials.Add((o.GetString("uid"), I(o, "type")));
        var names = r.TryGetArray("groupDescription", out var g) ? g.Items.Select(i => i.Value?.ToString() ?? "").ToList() : [];
        return new AllianceRoster(allianceId, at, members, officials, names);
    }

    public async Task<AllianceInfo?> AllianceInfoAsync(string allianceId, DateTimeOffset at, CancellationToken ct = default)
    {
        if (await Ask("get.al.info", P("allianceId", allianceId), ct).ConfigureAwait(false) is not { } r) return null;
        var created = I(r, "createServer");
        return new AllianceInfo
        {
            AllianceId = allianceId, TakenAt = at, Tag = r.GetString("abbr"), Name = r.GetString("name"),
            CreateServer = created, Server = I(r, "crossFightSrcServerId") is > 0 and var s ? s : created,
            FightPower = L(r, "fightPower"), Members = I(r, "curMember"), MaxMembers = I(r, "maxMember"),
            Engagement = L(r, "allianceEngagementPoint"), GiftLevel = I(r, "giftLevel"), CreatedAt = Ms(r, "createTime"),
            Language = r.GetString("language"), Country = r.GetString("country"),
            LeaderUid = r.GetString("leaderUid"), LeaderName = r.GetString("leaderName"),
            JoinApproval = I(r, "joinApproval") != 0, LevelLimit = I(r, "levelLimit"), PowerLimit = L(r, "powerLimit"),
            Announce = r.GetString("announce"),
        };
    }

    public async Task<List<CityHold>> CitiesAsync(int server, DateTimeOffset at, CancellationToken ct = default)
    {
        var outp = new List<CityHold>();
        if (await Ask("city.war.get.info", P("serverId", server), ct).ConfigureAwait(false) is not { } r) return outp;
        if (r.TryGetArray("cityInfoList", out var arr))
            foreach (var c in arr.Objects())
                outp.Add(new CityHold(server, at, I(c, "cityId"), c.GetString("alId"), c.GetString("alAbbr"), c.GetString("alName"),
                    Ms(c, "firstOccupyTime"), Ms(c, "protectTime"), Ms(c, "giveUpTime")));
        return outp;
    }

    public async Task<ServerState?> ServerStateAsync(int server, DateTimeOffset at, CancellationToken ct = default)
    {
        if (await Ask("season.migrate.server.info", P("serverId", server), ct).ConfigureAwait(false) is not { } r) return null;
        r.TryGetObject("presidentInfo", out var pres);
        r.TryGetObject("firstLadyInfo", out var lady);
        r.TryGetObject("migratePeopleInfo", out var mig);
        return new ServerState
        {
            Server = server, TakenAt = at, State = I(r, "serverState"), Score = L(r, "serverScore"),
            PresidentUid = pres?.GetString("uid") ?? "", PresidentName = pres?.GetString("name") ?? "", PresidentTag = pres?.GetString("abbr") ?? "",
            FirstLadyUid = lady?.GetString("uid") ?? "", FirstLadyName = lady?.GetString("name") ?? "",
            ApplyPower = L(r, "applyPower"),
            InSuperLow = mig is null ? 0 : I(mig, "superLowPlayerIn"), InLow = mig is null ? 0 : I(mig, "lowPlayerIn"),
            InMid = mig is null ? 0 : I(mig, "playerIn"), InHigh = mig is null ? 0 : I(mig, "highPlayerIn"),
            Notice = r.GetString("notice"),
        };
    }

    public async Task<List<Stronghold>> StrongholdsAsync(int server, DateTimeOffset at, CancellationToken ct = default)
    {
        var outp = new List<Stronghold>();
        if (await Ask("world.get.city.stronghold.info", P("serverId", server), ct).ConfigureAwait(false) is not { } r) return outp;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(r.GetString("content")); } catch (FormatException) { return outp; }
        // Repeated field 1 = one stronghold: 1 id, 2 tag, 3 allianceId, 4 level, 6 alliance name, 7 server.
        foreach (var m in Proto.Fields(bytes).Where(f => f.No == 1 && f.Bytes is not null))
        {
            var f = Proto.Fields(m.Bytes!).ToLookup(x => x.No);
            string Str(int n) => f[n].FirstOrDefault().Bytes is { } b ? System.Text.Encoding.UTF8.GetString(b) : "";
            long Num(int n) => f[n].FirstOrDefault().Value;
            outp.Add(new Stronghold(Num(7) is > 0 and var s ? (int)s : server, at, (int)Num(1), Str(3), Str(2), Str(6), (int)Num(4)));
        }
        return outp;
    }

    public async Task<List<ServerCard>> ServerCardsAsync(IEnumerable<int> servers, DateTimeOffset at, CancellationToken ct = default)
    {
        var outp = new List<ServerCard>();
        foreach (var chunk in servers.Chunk(20))
        {
            if (await Ask("get.cross.server.king.info", P("serverIds", string.Join(",", chunk)), ct).ConfigureAwait(false) is not { } r) continue;
            r.TryGetObject("serverInfo", out var info);
            r.TryGetObject("serverKing", out var kings);
            foreach (var s in chunk)
            {
                SfsObject? i = null, k = null;
                info?.TryGetObject(s.ToString(), out i);
                kings?.TryGetObject(s.ToString(), out k);
                if (i is null && k is null) continue;
                outp.Add(new ServerCard(s, at, i is null ? null : Ms(i, "createTime"), k?.GetString("uid") ?? "", k?.GetString("name") ?? "",
                    k?.GetString("allianceAbbr") ?? "", k is null ? 0 : L(k, "power"), k is null ? null : Ms(k, "beKingTime")));
            }
        }
        return outp;
    }

    public async Task<PlayerDetail?> PlayerDetailAsync(string uid, int server, DateTimeOffset at, bool withCard = true, CancellationToken ct = default)
    {
        var w = new SfsObject(); w.PutUtfString("uid", uid); w.PutInt("serverId", server);
        var drone = await Ask("weapon.info.view", w, ct).ConfigureAwait(false);
        var c = new SfsObject();
        foreach (var k in new[] { "uid", "type", "boolean", "profile_hint" }) c.PutUtfString(k, k == "uid" ? uid : "");
        var card = withCard ? await Ask("get.new.user.info", c, ct).ConfigureAwait(false) : null;
        if (drone is null && card is null) return null;
        string props = "";
        if (drone is not null && drone.TryGetObject("props", out var p)) props = SfsJson.Write(p).ToJsonString();
        return new PlayerDetail
        {
            Uid = uid, TakenAt = at,
            DroneLevel = drone is null ? 0 : I(drone, "lv"), DronePower = drone is null ? 0 : L(drone, "power"),
            ChipLevel = drone is null ? 0 : I(drone, "chipLv"), DroneProps = props,
            CareerType = card is null ? 0 : I(card, "careerType"), CareerLevel = card is null ? 0 : I(card, "careerLv"),
            GiftLevel = card is null ? 0 : I(card, "giftLevel"), BadgeId = card?.GetString("badgeId") ?? "",
            AllianceRank = card is null ? 0 : I(card, "allianceRank"),
            BattleWin = card is null ? 0 : L(card, "battleWin"), BattleLose = card is null ? 0 : L(card, "battleLose"),
            ArmyDead = card is null ? 0 : L(card, "armyDead"),
        };
    }

    public async Task<List<President>> PresidentsAsync(int server, int maxPages = 10, CancellationToken ct = default)
    {
        var outp = new List<President>();
        for (var page = 1; page <= maxPages; page++)
        {
            var p = new SfsObject(); p.PutInt("serverId", server); p.PutInt("page", page);
            if (await Ask("get.history.king.record", p, ct).ConfigureAwait(false) is not { } r || !r.TryGetArray("historyKings", out var arr)) break;
            var before = outp.Count;
            foreach (var k in arr.Objects())
                outp.Add(new President(server, I(k, "round"), k.GetString("uid"), k.GetString("name"), k.GetString("allianceAbbr"),
                    k.GetString("allianceName"), Ms(k, "beKingTime"), I(k, "occupyServerId"), k.GetString("country")));
            if (outp.Count - before < 10) break;
        }
        return outp;
    }
}
