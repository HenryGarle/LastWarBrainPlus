namespace LastWar.Storage;

/// <summary>
/// The views built on intel.sweep (IntelStore) plus profile "last seen":
///
///   AllianceIntel - the alliance card over time, its roster with how recently each member was in
///                   the game and donated, officers, who joined/left, the cities it holds.
///   ServerIntel   - who holds the server's cities (and every change of hands seen), its presidents,
///                   its score and migration inflow over time.
///   Matchup       - two alliances side by side, and a transparent estimate of who wins a fight.
///
/// "Seen" is profile lastUpdateTime (PlayerActivity.last_seen_at): when the game last refreshed the
/// player. "Donated" is al.rank donateTime. Either within a window proves the player was in the game.
/// </summary>
public sealed partial class Insights
{
    /// <summary>Estimated hero power from the player's newest profile (HeroModel): k x (power - armyPower).</summary>
    static string HeroEst(string uid) =>
        $"(SELECT pl.hero FROM player_latest pl WHERE pl.uid = {uid})";   // PlayerLatest: newest profile, tapered estimate

    /// <summary>The hero-power model's own back-test (hero_fit), overall first.</summary>
    public List<Dictionary<string, object?>> HeroFit()
    {
        using var c = Open();
        return Q(c, """
            SELECT server, k, players, median_err AS "medianErr", p90_err AS "p90Err", worst_err AS "worstErr",
                   bottom20_err AS "bottom20Err", bias, fitted_at AS "fittedAt"
            FROM hero_fit ORDER BY server;
            """);
    }

    static readonly string SeenBuckets = $"""
        COUNT(*) FILTER (WHERE s.seen >= {Db.AgoIso("1 day")}) AS seen1d,
        COUNT(*) FILTER (WHERE s.seen >= {Db.AgoIso("3 days")}) AS seen3d,
        COUNT(*) FILTER (WHERE s.seen >= {Db.AgoIso("7 days")}) AS seen7d,
        COUNT(*) FILTER (WHERE s.seen <> '' AND s.seen < {Db.AgoIso("7 days")}) AS gone7d,
        COUNT(*) FILTER (WHERE s.seen = '') AS unknown
        """;

    public Dictionary<string, object?> AllianceIntel(string allianceId)
    {
        using var c = Open();
        var info = Q(c, """
            SELECT taken_at AS at, tag, name, server, create_server AS "createServer", fight_power AS "fightPower", members,
                   max_members AS "maxMembers", engagement, gift_level AS "giftLevel", created_at AS "createdAt", language, country,
                   leader_uid AS "leaderUid", leader_name AS "leaderName", join_approval AS "joinApproval", level_limit AS "levelLimit",
                   power_limit AS "powerLimit", announce
            FROM alliance_info_sample WHERE alliance_id = @a ORDER BY taken_at;
            """, ("@a", allianceId));
        var snaps = Q(c, "SELECT DISTINCT taken_at AS at FROM alliance_member_sample WHERE alliance_id = @a ORDER BY taken_at;", ("@a", allianceId));
        if (snaps.Count == 0) return new() { ["allianceId"] = allianceId, ["info"] = info.LastOrDefault(), ["history"] = info, ["roster"] = null };
        var at = (string)snaps[^1]["at"]!;
        var prev = snaps.Count > 1 ? (string)snaps[^2]["at"]! : null;

        var roster = Q(c, $"""
            SELECT m.uid, m.name, m.rank, m.power, m.kills, m.hq, m.donate_today AS "donateToday", m.donate_week AS "donateWeek",
                   NULLIF(m.donated_at, '') AS "donatedAt", NULLIF(m.joined_at, '') AS "joinedAt", NULLIF(m.rank_since, '') AS "rankSince",
                   m.month_card_end AS "monthCardEnd", m.server, m.current_server AS "currentServer",
                   NULLIF(pa.last_seen_at, '') AS "lastSeen", NULLIF(pa.last_rise_at, '') AS "lastRise",
                   {F(LatestHero, "m.uid")} AS "heroPower", {HeroEst("m.uid")} AS "heroEst",
                   (SELECT d.drone_power FROM player_detail_sample d WHERE d.uid = m.uid ORDER BY d.taken_at DESC LIMIT 1) AS "dronePower",
                   (SELECT d.drone_level FROM player_detail_sample d WHERE d.uid = m.uid ORDER BY d.taken_at DESC LIMIT 1) AS "droneLevel",
                   (SELECT o.type FROM alliance_official o WHERE o.taken_at = m.taken_at AND o.alliance_id = m.alliance_id AND o.uid = m.uid) AS official
            FROM alliance_member_sample m LEFT JOIN player_activity pa ON pa.uid = m.uid
            WHERE m.alliance_id = @a AND m.taken_at = @t ORDER BY m.power DESC;
            """, ("@a", allianceId), ("@t", at));

        // Joined and left between the two newest rosters.
        var left = prev is null ? [] : Q(c, """
            SELECT p.uid, p.name, p.power, p.rank,
                   (SELECT pl.tag FROM player_latest pl WHERE pl.uid = p.uid) AS "nowTag",
                   (SELECT pl.alliance_id FROM player_latest pl WHERE pl.uid = p.uid) AS "nowAllianceId"
            FROM alliance_member_sample p
            WHERE p.alliance_id = @a AND p.taken_at = @p
              AND p.uid NOT IN (SELECT uid FROM alliance_member_sample WHERE alliance_id = @a AND taken_at = @t);
            """, ("@a", allianceId), ("@p", prev), ("@t", at));
        var joined7 = roster.Count(m => m["joinedAt"] is string j && DateTimeOffset.TryParse(j, out var jd) && jd >= DateTimeOffset.UtcNow.AddDays(-7));

        // Activity per roster: the later of last seen and last donation, in buckets.
        var activity = Q(c, $"""
            WITH s AS (
                SELECT GREATEST(COALESCE(pa.last_seen_at, ''), m.donated_at) AS seen, m.donated_at AS donated, m.donate_today AS today,
                       m.power AS power, m.taken_at AS at
                FROM alliance_member_sample m LEFT JOIN player_activity pa ON pa.uid = m.uid
                WHERE m.alliance_id = @a)
            SELECT at, COUNT(*) AS members, SUM(power) AS power,
                   COUNT(*) FILTER (WHERE donated >= {Db.MinusIso("at", "1 day")}) AS donated1d,
                   COUNT(*) FILTER (WHERE today > 0) AS "donatedToday", SUM(today) AS "donationToday",
                   {SeenBuckets}
            FROM s GROUP BY at ORDER BY at;
            """, ("@a", allianceId));

        var cities = Q(c, """
            SELECT server, city_id AS "cityId", NULLIF(first_occupied_at, '') AS since, NULLIF(protected_until, '') AS "protectedUntil"
            FROM city_sample WHERE alliance_id = @a AND taken_at = (SELECT MAX(taken_at) FROM city_sample WHERE alliance_id = @a)
            ORDER BY city_id;
            """, ("@a", allianceId));

        // When the alliance is in the game (UTC hour): every distinct donation moment and every
        // distinct "last seen" moment of its current members. Its quiet hours are when a strike lands.
        var hours = new long[24];
        foreach (var r in Q(c, $"""
            SELECT {Db.HourOf("t")} AS h, COUNT(*) AS n FROM (
                SELECT DISTINCT uid, donated_at AS t FROM alliance_member_sample WHERE alliance_id = @a AND donated_at <> ''
                UNION
                SELECT p.uid, p.seen_at FROM player_seen p
                WHERE NOT {PlayerActivity.NotALogin("p.seen_at")} AND p.uid IN (SELECT uid FROM alliance_member_sample WHERE alliance_id = @a AND taken_at = @t)) moments
            GROUP BY h;
            """, ("@a", allianceId), ("@t", at)))
            if (r["h"] is long h && h is >= 0 and < 24) hours[h] = Convert.ToInt64(r["n"]);

        var strongholds = Q(c, """
            SELECT server, stronghold_id AS id, level FROM stronghold_sample
            WHERE alliance_id = @a AND taken_at = (SELECT MAX(taken_at) FROM stronghold_sample WHERE alliance_id = @a) ORDER BY id;
            """, ("@a", allianceId));

        return new()
        {
            ["allianceId"] = allianceId,
            ["strongholds"] = strongholds,
            ["hours"] = hours,
            ["info"] = info.LastOrDefault(),
            ["history"] = info,
            ["rosterAt"] = at,
            ["prevRosterAt"] = prev,
            ["roster"] = roster,
            ["left"] = left,
            ["joined7d"] = joined7,
            ["activity"] = activity,
            ["cities"] = cities,
        };
    }

    public Dictionary<string, object?> ServerIntel(int server)
    {
        using var c = Open();
        var latest = Q(c, "SELECT MAX(taken_at) AS at FROM city_sample WHERE server = @s;", ("@s", server))[0]["at"] as string;
        var holders = latest is null ? [] : Q(c, """
            SELECT alliance_id AS "allianceId", MAX(tag) AS tag, MAX(alliance_name) AS name, COUNT(*) AS cities,
                   MIN(NULLIF(first_occupied_at, '')) AS since
            FROM city_sample WHERE server = @s AND taken_at = @t AND alliance_id <> ''
            GROUP BY alliance_id ORDER BY cities DESC;
            """, ("@s", server), ("@t", latest));
        var totals = latest is null ? null : Q(c, "SELECT COUNT(*) AS total, COUNT(*) FILTER (WHERE alliance_id <> '') AS held FROM city_sample WHERE server = @s AND taken_at = @t;",
            ("@s", server), ("@t", latest))[0];
        // Every change of hands between consecutive samples of the same city.
        var changes = Q(c, """
            WITH x AS (
                SELECT city_id, taken_at, alliance_id, tag,
                       LAG(alliance_id) OVER (PARTITION BY city_id ORDER BY taken_at) AS prev_id,
                       LAG(tag) OVER (PARTITION BY city_id ORDER BY taken_at) AS prev_tag,
                       LAG(taken_at) OVER (PARTITION BY city_id ORDER BY taken_at) AS prev_at,
                       first_occupied_at
                FROM city_sample WHERE server = @s)
            SELECT city_id AS "cityId", prev_at AS after, taken_at AS before, prev_id AS "fromId", prev_tag AS "fromTag",
                   alliance_id AS "toId", tag AS "toTag", NULLIF(first_occupied_at, '') AS "occupiedAt"
            FROM x WHERE prev_id IS NOT NULL AND prev_id <> alliance_id ORDER BY taken_at DESC LIMIT 200;
            """, ("@s", server));
        var presidents = Q(c, """
            SELECT round, uid, name, tag, alliance_name AS "allianceName", NULLIF(became_at, '') AS "becameAt",
                   occupy_server AS "occupyServer", country
            FROM president WHERE server = @s ORDER BY round DESC;
            """, ("@s", server));
        var state = Q(c, """
            SELECT taken_at AS at, state, score, president_uid AS "presidentUid", president_name AS "presidentName",
                   president_tag AS "presidentTag", first_lady_uid AS "firstLadyUid", first_lady_name AS "firstLadyName",
                   apply_power AS "applyPower", in_super_low AS "inSuperLow", in_low AS "inLow", in_mid AS "inMid", in_high AS "inHigh", notice
            FROM server_state_sample WHERE server = @s ORDER BY taken_at;
            """, ("@s", server));
        // Last seen across the server's players (home server = this one).
        var seen = Q(c, $"""
            WITH s AS (SELECT COALESCE(pa.last_seen_at, '') AS seen FROM player_latest pl
                       LEFT JOIN player_activity pa ON pa.uid = pl.uid WHERE pl.server = @s)
            SELECT COUNT(*) AS players, {SeenBuckets} FROM s;
            """, ("@s", server)).FirstOrDefault();
        var card = Q(c, """
            SELECT NULLIF(created_at, '') AS "createdAt", king_uid AS "kingUid", king_name AS "kingName", king_tag AS "kingTag",
                   king_power AS "kingPower", NULLIF(king_since, '') AS "kingSince"
            FROM server_card_sample WHERE server = @s ORDER BY taken_at DESC LIMIT 1;
            """, ("@s", server)).FirstOrDefault();
        var strongholds = Q(c, """
            SELECT alliance_id AS "allianceId", MAX(tag) AS tag, COUNT(*) AS held FROM stronghold_sample
            WHERE server = @s AND alliance_id <> '' AND taken_at = (SELECT MAX(taken_at) FROM stronghold_sample WHERE server = @s)
            GROUP BY alliance_id ORDER BY held DESC;
            """, ("@s", server));

        return new()
        {
            ["card"] = card, ["strongholds"] = strongholds,
            ["server"] = server, ["citiesAt"] = latest, ["cityTotals"] = totals, ["holders"] = holders,
            ["cityChanges"] = changes, ["presidents"] = presidents, ["state"] = state.LastOrDefault(),
            ["stateHistory"] = state, ["seen"] = seen,
        };
    }

    /// <summary>
    /// Two alliances side by side from their newest rosters, with an explained estimate.
    ///
    /// Fights are won by squads, so each member counts by HERO POWER, not total power (which also
    /// counts buildings and tech). Hero power is read from the top-200 hero boards; a member below
    /// the board is estimated as power x the median hero/power share of the members who are on it,
    /// capped at their server's 200th place. Each member is then weighted by how recently they were
    /// in the game (last seen or donated): today 1, within 3 days 0.75, within a week 0.35, older 0.1.
    ///
    /// Attack: the attacker's 10 strongest (rallies) against the defender's 20 strongest at half
    /// weight (a deeper garrison). No home bonus: no source for one. Open fight: everyone, no home advantage.
    /// Chance = 1 / (1 + (defence / attack)^4): equal = 50%, 25% stronger = 71%, twice as strong = 94%.
    /// ponytail: still a heuristic - troop tiers, gear, buffs and tactics are not visible.
    /// </summary>
    public Dictionary<string, object?> Matchup(string a, string b, double steep = 2, int slots = 100)
    {
        steep = Math.Clamp(steep, 1, 6);
        using var c = Open();
        List<Dictionary<string, object?>> Members(string id, out string? at)
        {
            at = Q(c, "SELECT MAX(taken_at) AS at FROM alliance_member_sample WHERE alliance_id = @a;", ("@a", id))[0]["at"] as string;
            return at is null ? [] : Q(c, $"""
                SELECT m.uid, m.name, m.power, m.kills, m.hq, m.rank, m.server, m.donate_today AS today,
                       GREATEST(COALESCE(pa.last_seen_at, ''), m.donated_at) AS seen, {F(LatestHero, "m.uid")} AS "heroPower",
                       {HeroEst("m.uid")} AS "heroEst",
                       (SELECT d.drone_power FROM player_detail_sample d WHERE d.uid = m.uid ORDER BY d.taken_at DESC LIMIT 1) AS "dronePower"
                FROM alliance_member_sample m LEFT JOIN player_activity pa ON pa.uid = m.uid
                WHERE m.alliance_id = @a AND m.taken_at = @t ORDER BY m.power DESC;
                """, ("@a", id), ("@t", at));
        }
        var ma = Members(a, out var atA);
        var mb = Members(b, out var atB);

        static long L(object? v) => v is null ? 0 : Convert.ToInt64(v);
        static double Hours(Dictionary<string, object?> m) =>
            m["seen"] is string s && s.Length > 0 && DateTimeOffset.TryParse(s, out var t) ? (DateTimeOffset.UtcNow - t).TotalHours : double.MaxValue;
        static double Weight(double h) => h <= 24 ? 1 : h <= 72 ? 0.75 : h <= 168 ? 0.35 : 0.1;

        // Hero share of power among members on a hero board (both alliances), and each server's board floor.
        var shares = ma.Concat(mb).Where(m => m["heroPower"] is not null && L(m["power"]) > 0)
            .Select(m => (double)L(m["heroPower"]) / L(m["power"])).Order().ToList();
        var share = shares.Count > 0 ? shares[shares.Count / 2] : 0.55;
        var floor = new Dictionary<long, long>();
        foreach (var s in ma.Concat(mb).Select(m => L(m["server"])).Distinct())
            floor[s] = L(Q(c, """
                SELECT MIN(value) AS v FROM board_sample
                WHERE board_type = 13 AND server_id = @s AND taken_at = (SELECT MAX(taken_at) FROM board_sample WHERE board_type = 13 AND server_id = @s);
                """, ("@s", s))[0]["v"]);
        // Off the board means BELOW the 200th hero power - so an estimate never reaches it: at most
        // 85% of that floor (measured on 1967: off-board SaDe/SaFa members have 124-131M median power,
        // which at the board's share would put them above a floor they are known to be under).
        // Newest profile estimate first (HeroModel: ~1% from the board, and newer than it); the board
        // value when there is no profile; the power-share guess only when neither exists.
        double Hero(Dictionary<string, object?> m)
        {
            if (m["heroEst"] is { } e && L(e) > 0) return L(e);
            if (m["heroPower"] is { } h) return L(h);
            var est = L(m["power"]) * share;
            return floor.TryGetValue(L(m["server"]), out var f) && f > 0 ? Math.Min(est, 0.85 * f) : est * 0.85;
        }

        Dictionary<string, object?> Side(string id, string? at, List<Dictionary<string, object?>> members)
        {
            var info = Q(c, "SELECT tag, name, server, fight_power AS \"fightPower\", engagement, gift_level AS \"giftLevel\" FROM alliance_info_sample WHERE alliance_id = @a ORDER BY taken_at DESC LIMIT 1;", ("@a", id)).FirstOrDefault();
            var cities = Q(c, "SELECT COUNT(*) AS n FROM city_sample WHERE alliance_id = @a AND taken_at = (SELECT MAX(taken_at) FROM city_sample WHERE alliance_id = @a);", ("@a", id))[0]["n"];
            var combat = members.Select(m => (m, hero: Hero(m), fight: Hero(m) * Weight(Hours(m)))).OrderByDescending(x => x.fight).ToList();
            // Lanchester's square law: a squad's fighting value grows with the SQUARE of its strength
            // (a squad twice as strong beats about four equal ones), so chances use sums of squares.
            double Sq(IEnumerable<(Dictionary<string, object?> m, double hero, double fight)> xs) => xs.Sum(v => Math.Pow(v.fight / 1e8, steep));
            var drones = members.Where(m => m["dronePower"] is not null).ToList();
            return new()
            {
                ["allianceId"] = id, ["rosterAt"] = at, ["info"] = info, ["cities"] = cities,
                ["members"] = members.Count, ["seen1d"] = members.Count(m => Hours(m) <= 24), ["active3d"] = members.Count(m => Hours(m) <= 72),
                ["power"] = members.Sum(m => L(m["power"])),
                ["heroKnown"] = members.Sum(m => L(m["heroPower"])), ["onHeroBoard"] = members.Count(m => m["heroPower"] is not null),
                ["heroTotal"] = (long)combat.Sum(x => x.hero),
                ["combat"] = (long)combat.Sum(x => x.fight),
                ["top10"] = (long)combat.Take(10).Sum(x => x.fight), ["top20"] = (long)combat.Take(20).Sum(x => x.fight),
                ["sq10"] = Sq(combat.Take(10)), ["sq20"] = Sq(combat.Take(20)), ["sqAll"] = Sq(combat),
                ["kills"] = members.Sum(m => L(m["kills"])),
                ["hq30"] = members.Count(m => L(m["hq"]) >= 30),
                ["donatedToday"] = members.Count(m => L(m["today"]) > 0),
                ["withDrone"] = drones.Count,
                ["droneAvg"] = drones.Select(m => (double)L(m["dronePower"])).DefaultIfEmpty(0).Average(),
                ["heroFromProfile"] = members.Count(m => m["heroEst"] is { } e && L(e) > 0),
                ["strongest"] = combat.Take(10).Select(x => new Dictionary<string, object?>
                    { ["uid"] = x.m["uid"], ["name"] = x.m["name"], ["hero"] = (long)x.hero, ["estimated"] = x.m["heroEst"] is null && x.m["heroPower"] is null, ["weight"] = Weight(Hours(x.m)) }).ToList(),
            };
        }
        var x = Side(a, atA, ma); var y = Side(b, atB, mb);
        static double Chance(double attack, double defend) =>
            attack <= 0 || defend <= 0 ? (attack > defend ? 1 : attack < defend ? 0 : 0.5)
            : 1 / (1 + Math.Pow(defend / attack, 2));
        double D(Dictionary<string, object?> s, string k) => Convert.ToDouble(s[k] ?? 0);

        // War game by hour (UTC): the same fight, with each member weighted by the chance they are in
        // the game at that hour instead of by how recently they were seen.
        var on = OnByHour(c, ma.Concat(mb).Select(m => (string)m["uid"]!).ToList(), 14, out var observed);
        double P(Dictionary<string, object?> m, int h) => on.TryGetValue((string)m["uid"]!, out var p) ? p[h] : 0;
        double SqAt(List<Dictionary<string, object?>> ms, int h, int take) =>
            ms.Select(m => P(m, h) * Math.Pow(Hero(m) / 1e8, steep)).OrderDescending().Take(take).Sum();
        // Expected hero power of the strongest N on at an hour: 10 = the rally that attacks, 20 = the garrison that defends.
        long TopOn(List<Dictionary<string, object?>> ms, int h, int take) =>
            (long)ms.Select(m => P(m, h) * Hero(m)).OrderDescending().Take(take).Sum();
        var byHour = Enumerable.Range(0, 24).Select(h => new Dictionary<string, object?>
        {
            ["hour"] = h,
            ["aOn"] = Math.Round(ma.Sum(m => P(m, h)), 1), ["bOn"] = Math.Round(mb.Sum(m => P(m, h)), 1),
            ["aHero"] = (long)ma.Sum(m => P(m, h) * Hero(m)), ["bHero"] = (long)mb.Sum(m => P(m, h) * Hero(m)),
            ["aRally"] = TopOn(ma, h, 10), ["aGarrison"] = TopOn(ma, h, 20),
            ["bRally"] = TopOn(mb, h, 10), ["bGarrison"] = TopOn(mb, h, 20),
            ["aAttacks"] = Math.Round(Chance(SqAt(ma, h, 10), SqAt(mb, h, 20) / 2), 3),
            ["bAttacks"] = Math.Round(Chance(SqAt(mb, h, 10), SqAt(ma, h, 20) / 2), 3),
            ["open"] = Math.Round(Chance(SqAt(ma, h, int.MaxValue), SqAt(mb, h, int.MaxValue)), 3),
        }).ToList();

        // Siege (Siege.cs): each side attacking the other's war building, which the whole defending side
        // garrisons. Scheduled war: everyone who plays is expected (on = the recency weight). At an hour:
        // garrisons were set beforehand (recency), but attacking and re-garrisoning need them on THEN -
        // their own daily pattern (share of days on at that hour, over their best hour) x recency.
        slots = Math.Clamp(slots, 5, 100);
        double Shape(Dictionary<string, object?> m, int h) =>
            on.TryGetValue((string)m["uid"]!, out var p) && p.Max() > 0 ? p[h] / p.Max() : 0;
        List<Siege.Member> Army(List<Dictionary<string, object?>> ms, int? h) => ms.Select(m =>
        {
            var w = Weight(Hours(m));
            return new Siege.Member(Hero(m), w, h is int hr ? w * Shape(m, hr) : w);
        }).ToList();
        Dictionary<string, object?> R(Siege.Result r) => new()
        {
            ["breaks"] = Math.Round(r.Breaks, 3), ["left"] = Math.Round(r.GarrisonLeft, 1),
            ["attackers"] = Math.Round(r.AttackersOn, 1), ["garrison"] = Math.Round(r.DefendersIn, 1),
        };
        var siege = new Dictionary<string, object?>
        {
            ["slots"] = slots,
            ["aOnB"] = R(Siege.Run(Army(ma, null), Army(mb, null), slots)),
            ["bOnA"] = R(Siege.Run(Army(mb, null), Army(ma, null), slots)),
            ["byHour"] = Enumerable.Range(0, 24).Select(h => new Dictionary<string, object?>
            {
                ["hour"] = h,
                ["aOnB"] = R(Siege.Run(Army(ma, h), Army(mb, h), slots, 200)),
                ["bOnA"] = R(Siege.Run(Army(mb, h), Army(ma, h), slots, 200)),
            }).ToList(),
        };
        return new()
        {
            ["a"] = x, ["b"] = y, ["heroShare"] = Math.Round(share, 3), ["steep"] = steep,
            ["byHour"] = byHour, ["observedDays"] = observed,
            ["siege"] = siege,
            ["aAttacks"] = Math.Round(Chance(D(x, "sq10"), D(y, "sq20") / 2), 3),
            ["bAttacks"] = Math.Round(Chance(D(y, "sq10"), D(x, "sq20") / 2), 3),
            ["open"] = Math.Round(Chance(D(x, "sqAll"), D(y, "sqAll")), 3),
            ["strengthRatio"] = D(y, "sqAll") > 0 ? Math.Round(D(x, "sqAll") / D(y, "sqAll"), 2) : null,
            ["model"] = "Each member counts by hero power: 0.78 x (power - troop power) from their newest profile, which back-tests to "
                + "about 1% of the hero boards (the board value if no profile; a rough guess if neither), weighted by how "
                + "recently they were in the game: today 1, 3 days 0.75, a week 0.35, older 0.1. Each squad's strength is raised to "
                + $"the power {steep:0.#} before adding up (2 = Lanchester's square law: one squad twice as strong beats about four "
                + "equal ones), so strong players count for more than numbers. Attack: attacker's 10 strongest vs the defender's 20 "
                + "at half weight. Open fight: everyone. Chance = 1 / (1 + (defence / attack)^2) on those sums.",
        };
    }
}
