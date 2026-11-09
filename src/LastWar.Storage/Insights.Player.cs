namespace LastWar.Storage;

public sealed partial class Insights
{
    /// <summary>
    /// Everything known about one uid: every map sighting (so every server and alliance it has
    /// been seen in, and every name it has used), its power over time, its leaderboard places,
    /// its chat, and any transfer application.
    /// </summary>
    public Dictionary<string, object?> Player(string uid)
    {
        using var c = Open();
        var sightings = Q(c, """
            SELECT s.taken_at AS at, t.server_id AS server, t.name, t.alliance_id AS "allianceId",
                   t.alliance_tag AS tag, t.hq_level AS hq, t.point_id AS point
            FROM map_tile t JOIN map_scan s ON s.scan_id = t.scan_id
            WHERE t.owner_uid = @u ORDER BY s.taken_at;
            """, ("@u", uid));
        var power = Q(c, """
            SELECT taken_at AS at, server_id AS server, power, kill_army AS kills, hq_level AS hq,
                   name, alliance_tag AS tag, alliance_id AS "allianceId", source
            FROM power_sample WHERE uid = @u ORDER BY taken_at;
            """, ("@u", uid));
        var boards = Q(c, """
            SELECT taken_at AS at, server_id AS server, board_type AS board, rank, value
            FROM board_sample WHERE uid = @u ORDER BY taken_at;
            """, ("@u", uid));
        var chat = Q(c, """
            SELECT room_id AS room, sent_at AS at, sender_name AS name, sender_al AS tag, msg
            FROM chat_message WHERE sender_uid = @u ORDER BY sent_at DESC LIMIT 60;
            """, ("@u", uid));
        var chatCount = Q(c, "SELECT COUNT(*) AS n FROM chat_message WHERE sender_uid = @u;", ("@u", uid));
        var applications = Q(c, """
            SELECT captured_at AS at, from_server AS "fromServer", to_server AS "toServer", power,
                   hero_power AS "heroPower", thp_rank AS "thpRank", apply_score AS score, message
            FROM migration_applicant WHERE uid = @u ORDER BY captured_at;
            """, ("@u", uid));

        // Home server over time, from profile sweeps and chat lookups. This - not the map - is the
        // authority on which server a player is FROM: a base on another server's map at a weekend
        // is a visit to fight, not a transfer.
        var profileRows = new ProfileStore(path).History(uid);
        var profiles = profileRows.Select(p => new Dictionary<string, object?>
        {
            ["at"] = p.TakenAt.ToString("O"), ["home"] = p.HomeServer, ["based"] = p.CurrentServer, ["seen"] = p.LastSeen?.ToString("O"),
            ["tag"] = p.AllianceTag, ["allianceId"] = p.AllianceId, ["power"] = p.Power,
            // armyPower = troop power; the rest (buildings, tech, heroes) gives the hero estimate (HeroModel).
            ["troops"] = p.ArmyPower, ["heroEst"] = p.Power > p.ArmyPower ? (long?)(HeroModel.DefaultK * (p.Power - p.ArmyPower)) : null,
            ["name"] = p.Name, ["source"] = p.Source,
        }).ToList();

        // Moves: changes of home server between profiles when there are any, otherwise changes of
        // map server between consecutive sightings (which cannot tell a visit from a transfer).
        var trail = profiles.Count > 0 ? profiles.Select(p => (At: p["at"], Server: p["home"])).ToList()
                                       : sightings.Select(s => (At: s["at"], Server: s["server"])).ToList();
        var moves = new List<Dictionary<string, object?>>();
        for (var i = 1; i < trail.Count; i++)
            if (!Equals(trail[i].Server, trail[i - 1].Server))
                moves.Add(new()
                {
                    ["after"] = trail[i - 1].At, ["before"] = trail[i].At,
                    ["from"] = trail[i - 1].Server, ["to"] = trail[i].Server,
                    ["by"] = profiles.Count > 0 ? "profile" : "map",
                });
        var lastProfile = profiles.LastOrDefault();
        var lastProfileRow = profileRows.LastOrDefault();
        var vipHistory = VipHistory(profileRows);
        var onlineHours = OnlineHours(c, DateTimeOffset.UtcNow.AddDays(-30), [uid]).GetValueOrDefault(uid);
        var nameChanges = new NameHistory(path).For("player", uid);

        // Current values come from the NEWEST reading of any kind - a map sighting, a power sample or
        // a profile - so a transfer shows the new server and alliance while the history keeps the old.
        var latest = new[]
            {
                sightings.LastOrDefault(),
                // TileDetail samples carry no alliance at all, so an empty one there means "unknown".
                power.LastOrDefault(p => (p["source"] as string) == "Profile" || (p["allianceId"] as string)?.Length > 0),
                lastProfile is null ? null : new Dictionary<string, object?>
                {
                    ["at"] = lastProfile["at"], ["server"] = lastProfile["based"] ?? lastProfile["home"], ["name"] = lastProfile["name"],
                    ["allianceId"] = lastProfile["allianceId"], ["tag"] = lastProfile["tag"],
                },
            }
            .OfType<Dictionary<string, object?>>()
            .MaxBy(r => DateTimeOffset.TryParse(r["at"]?.ToString(), out var t) ? t : DateTimeOffset.MinValue);
        long? P(int back) => power.Count > back ? Convert.ToInt64(power[^(back + 1)]["power"]) : null;
        var (now, prev) = (P(0), P(1));
        // Newest reading that rose against the reading before it (same rule as PlayerActivity).
        string? lastRise = null;
        for (var i = power.Count - 1; i > 0 && lastRise is null; i--)
            if (Convert.ToInt64(power[i]["power"]) > Convert.ToInt64(power[i - 1]["power"])) lastRise = power[i]["at"] as string;
        // Last seen (profile lastUpdateTime) is direct evidence of a login; a rise is indirect.
        // Last seen = newest REAL login (game-side mass touches filtered out, see PlayerActivity).
        var pa = Q(c, "SELECT NULLIF(last_seen_at, '') AS seen, NULLIF(last_active_at, '') AS active, NULLIF(last_updated_at, '') AS updated FROM player_activity WHERE uid = @u;", ("@u", uid)).FirstOrDefault();
        var lastSeen = T(pa?["seen"]);
        var lastActive = T(pa?["active"]);
        var active7 = lastActive >= DateTimeOffset.UtcNow.AddDays(-PlayerActivity.WindowDays);

        return new()
        {
            ["uid"] = uid,
            ["originServer"] = uid.Length >= 6 && int.TryParse(uid[^6..], out var o) ? o : null,
            // The newest name from ANY source - the last logged rename wins over an older map sighting.
            ["name"] = nameChanges.Count > 0 ? nameChanges[^1]["new"] : latest?["name"],
            ["server"] = latest?["server"],               // where the newest reading of any kind saw them
            ["seenAt"] = latest?["at"],
            // Where the newest map saw their base, for the "on the map" link.
            ["mapPoint"] = sightings.LastOrDefault() is { } ms && ms["point"] is { } pt
                ? new Dictionary<string, object?> { ["server"] = ms["server"], ["x"] = (Convert.ToInt32(pt) - 1) % MapSize, ["y"] = (Convert.ToInt32(pt) - 1) / MapSize, ["at"] = ms["at"] }
                : null,
            ["home"] = lastProfile?["home"],               // the server they are from, per their profile
            ["homeAt"] = lastProfile?["at"],
            ["based"] = lastProfile?["based"],
            ["profiles"] = profiles,
            ["nameChanges"] = nameChanges,
            ["serverHistory"] = new ServerHistory(path).For(uid),
            ["allianceId"] = latest?["allianceId"],
            ["tag"] = latest?["tag"],
            ["power"] = now,
            ["change"] = now is not null && prev is not null ? now - prev : null,
            // The rule: only a RISE proves a login - anywhere in the last 7 days, not just the last pair.
            ["status"] = active7 ? "active" : lastActive is null && now is null ? "unknown" : "quiet",
            ["lastActive"] = lastActive?.ToString("O"),
            ["lastRise"] = lastRise,
            ["lastSeen"] = lastSeen?.ToString("O"),
            ["lastUpdated"] = T(pa?["updated"])?.ToString("O"),   // any lastUpdateTime, attacks and moves included: interest only
            ["troopPower"] = lastProfileRow?.ArmyPower,
            ["profileAt"] = lastProfileRow?.TakenAt.ToString("O"),
            ["heroEst"] = lastProfileRow is { } lp && lp.Power > lp.ArmyPower
                ? (long?)(Q(c, $"SELECT {HeroModel.KSql("0")} AS k;")[0]["k"] is double k ? k * (lp.Power - lp.ArmyPower) : HeroModel.DefaultK * (lp.Power - lp.ArmyPower)) : null,
            ["svipLevel"] = lastProfileRow?.SvipLevel,
            ["vipUntil"] = lastProfileRow is { SvipEnd: > 0 } ve ? DateTimeOffset.FromUnixTimeSeconds(ve.SvipEnd).ToString("O") : null,
            ["vipHistory"] = vipHistory,
            ["online"] = OnlineOf(onlineHours, HourOf(DateTime.UtcNow)),
            ["country"] = profileRows.LastOrDefault(p => p.Country.Length > 0)?.Country,
            ["flag"] = profileRows.LastOrDefault(p => p.Flag.Length > 0)?.Flag,
            ["monthCard"] = lastProfileRow is { MonthCardEnd: > 0 } mc ? DateTimeOffset.FromUnixTimeSeconds(mc.MonthCardEnd).ToString("O") : null,
            ["thumbsUp"] = lastProfileRow?.ThumbsUp,
            ["names"] = sightings.Select(s => s["name"]).Concat(power.Select(p => p["name"]))
                .OfType<string>().Where(n => n.Length > 0).Distinct().ToList(),
            // Every alliance any source saw them in, oldest first - map, power samples and profiles.
            ["alliances"] = sightings.Concat(power).Concat(profiles)
                .Where(s => (s["tag"] as string)?.Length > 0 && (s["allianceId"] as string)?.Length > 0)
                .OrderBy(s => DateTimeOffset.TryParse(s["at"]?.ToString(), out var t) ? t : DateTimeOffset.MinValue)
                .GroupBy(s => s["allianceId"])
                .Select(g => new Dictionary<string, object?>
                {
                    ["allianceId"] = g.Key, ["tag"] = g.Last()["tag"],
                    ["first"] = g.First()["at"], ["last"] = g.Last()["at"],
                }).ToList(),
            ["sightings"] = sightings,
            ["powerHistory"] = power,
            ["boards"] = boards,
            ["moves"] = moves,
            ["chat"] = chat,
            ["chatCount"] = chatCount[0]["n"],
            ["applications"] = applications,
            // Newest alliance roster row (intel.sweep): rank, donations, when they joined.
            ["roster"] = Q(c, """
                SELECT m.taken_at AS at, m.alliance_id AS "allianceId", m.rank, m.donate_today AS "donateToday",
                       m.donate_week AS "donateWeek", NULLIF(m.donated_at, '') AS "donatedAt", NULLIF(m.joined_at, '') AS "joinedAt",
                       NULLIF(m.rank_since, '') AS "rankSince",
                       (SELECT o.type FROM alliance_official o WHERE o.taken_at = m.taken_at AND o.alliance_id = m.alliance_id AND o.uid = m.uid) AS official
                FROM alliance_member_sample m WHERE m.uid = @u ORDER BY m.taken_at DESC LIMIT 1;
                """, ("@u", uid)).FirstOrDefault(),
            ["detail"] = Q(c, """
                SELECT taken_at AS at, drone_level AS "droneLevel", drone_power AS "dronePower", chip_level AS "chipLevel",
                       career_type AS "careerType", career_level AS "careerLevel", gift_level AS "giftLevel", badge_id AS "badgeId",
                       alliance_rank AS "allianceRank"
                FROM player_detail_sample WHERE uid = @u ORDER BY taken_at DESC LIMIT 1;
                """, ("@u", uid)).FirstOrDefault(),
            ["presidencies"] = Q(c, "SELECT server, round, NULLIF(became_at, '') AS \"becameAt\", tag FROM president WHERE uid = @u ORDER BY round;", ("@u", uid)),
        };
    }

    /// <summary>
    /// One box that finds players, alliances and servers by any handle. The signed-in password's
    /// favourites (access_favourite) come first in every list, partial matches included; an empty q
    /// returns only the favourites, newest first - the quick picks a box shows when first clicked.
    /// </summary>
    public Dictionary<string, object?> Search(string q, int limit = 25, string? favouritesOf = null)
    {
        q = q.Trim();
        var quick = q.Length == 0;
        if (quick) limit = 10;
        using var c = Open();
        var like = "%" + q.Replace("%", "").Replace("_", "") + "%";
        var prefix = q.Replace("%", "").Replace("_", "") + "%";
        (string, object)[] Ps(params (string, object)[] more) =>
            [("@q", like), ("@exact", q), ("@prefix", prefix), ("@n", limit), ("@k", favouritesOf ?? ""), ("@quick", quick), .. more];
        string Fav(string kind) => $"(SELECT uid AS id, created_at AS favAt FROM access_favourite WHERE key_id = @k AND kind = '{kind}')";
        // Players and alliances come from the derived *_latest tables (see PlayerLatest): the newest
        // name and alliance from any source, and the server they are FROM, not where a base sits.
        // Favourites, then exact uid, then names that start with the text, then the most recently seen.
        var players = Q(c, $"""
            SELECT uid, name, server, tag, alliance_id AS "allianceId", seen, {Origin("uid")} AS origin, f.id IS NOT NULL AS fav
            FROM player_latest LEFT JOIN {Fav("player")} f ON f.id = uid
            WHERE CASE WHEN @quick THEN f.id IS NOT NULL ELSE name ILIKE @q OR uid = @exact END
            ORDER BY f.id IS NOT NULL DESC, uid = @exact DESC, name ILIKE @prefix DESC, f.favAt DESC NULLS LAST, seen DESC, name, uid LIMIT @n;
            """, Ps());
        var alliances = Q(c, $"""
            SELECT alliance_id AS "allianceId", tag, server, members AS "seenMembers", seen, f.id IS NOT NULL AS fav
            FROM alliance_latest LEFT JOIN {Fav("alliance")} f ON f.id = alliance_id
            WHERE CASE WHEN @quick THEN f.id IS NOT NULL ELSE tag ILIKE @q OR alliance_id = @exact END
            ORDER BY f.id IS NOT NULL DESC, alliance_id = @exact DESC, tag ILIKE @prefix DESC, f.favAt DESC NULLS LAST, members DESC, tag, alliance_id LIMIT @n;
            """, Ps());
        // Servers: favourites whose number contains the text, then the exact number if we map it.
        var servers = Q(c, """
            SELECT CAST(uid AS BIGINT) AS server, TRUE AS fav, created_at AS o FROM access_favourite
              WHERE key_id = @k AND kind = 'server' AND (@quick OR uid LIKE @q)
            UNION ALL SELECT DISTINCT server_id, FALSE, '' FROM map_scan WHERE server_id = @s AND NOT @quick
            ORDER BY fav DESC, o DESC LIMIT @n;
            """, Ps(("@s", int.TryParse(q, out var sid) ? sid : -1)))
            .GroupBy(r => Convert.ToInt64(r["server"])).Select(g => { var r = g.First(); r.Remove("o"); return r; }).ToList();
        // Old names still find people: anyone whose logged renames match, with what they are called now.
        var seen = players.Select(p => p["uid"] as string).ToHashSet();
        var renamed = q.Length >= 2
            ? new NameHistory(path).Find(q, limit).Where(r => (string?)r["kind"] == "player" && !seen.Contains(r["id"] as string))
                .GroupBy(r => r["id"]).Select(g => g.First()).ToList()
            : [];
        return new() { ["players"] = players, ["alliances"] = alliances, ["servers"] = servers, ["renamed"] = renamed, ["quick"] = quick };
    }

    /// <summary>svip_level / svip_end were first stored on 2026-09-27 15:12; older profile rows hold 0 for "not read".</summary>
    public static readonly DateTimeOffset VipSince = new(2026, 9, 27, 15, 12, 0, TimeSpan.Zero);

    /// <summary>
    /// VIP level and VIP-active-until over time, one row per change (profiles are already stored
    /// change-only). "prevAt" is the last reading before the change: it happened between the two.
    /// The game shows other players no VIP points, so there is no progress within a level.
    /// </summary>
    public static List<Dictionary<string, object?>> VipHistory(IReadOnlyList<LastWar.Core.Model.PlayerProfile> rows)
    {
        var outp = new List<Dictionary<string, object?>>();
        LastWar.Core.Model.PlayerProfile? prev = null;
        foreach (var r in rows.Where(r => r.TakenAt >= VipSince).OrderBy(r => r.TakenAt))
        {
            var first = prev is null;
            var level = !first && r.SvipLevel != prev!.SvipLevel;
            var until = !first && prev!.SvipEnd != 0 && r.SvipEnd != prev.SvipEnd;
            if (first || level || until)
                outp.Add(new()
                {
                    ["at"] = r.TakenAt.ToString("O"), ["prevAt"] = prev?.TakenAt.ToString("O"),
                    ["kind"] = first ? "first" : level ? "level" : "until",
                    ["from"] = prev?.SvipLevel, ["level"] = r.SvipLevel,
                    ["until"] = r.SvipEnd > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.SvipEnd).ToString("O") : null,
                });
            prev = r;
        }
        return outp;
    }
}
