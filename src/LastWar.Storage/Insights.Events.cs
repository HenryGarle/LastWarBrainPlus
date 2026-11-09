namespace LastWar.Storage;

/// <summary>
/// Event pages. Meteorite Iron War (internal name yuntieBattle): ~4 warzones fight in one warzone's
/// crater on Saturday, two 30-minute rounds at 00:10 and 12:30 server time (UTC). The game matches the
/// warzones week by week inside the pools of yuntie_battle_server_group_new (game_event_group), so the
/// servers are picked by hand (event_server) and everything here is read off data we already hold:
/// hero power boards (exact, top 200 per server), profile estimates (player_latest.hero), HQ from the
/// newest map, activity and online hours.
/// </summary>
public sealed partial class Insights
{
    /// <summary>UTC hours the two contest rounds fall in (00:10-00:40 and 12:30-13:00).</summary>
    public static readonly int[] MeteoriteRoundHours = [0, 12];


    /// <summary>Within an hour of each other on the 24-hour clock (23 and 0 are neighbours).</summary>
    public static bool Near(int a, int b) { var d = ((a - b) % 24 + 24) % 24; return Math.Min(d, 24 - d) <= 1; }

    public Dictionary<string, object?> Meteorite(IReadOnlyList<int>? pick)
    {
        var reg = new ServerRegistry(path);
        var saved = reg.EventServers("meteorite");
        var ids = (pick is { Count: > 0 } ? pick : saved).Distinct().Order().ToList();
        if (ids.Count == 0) ids = [ServerRegistry.Home];   // nothing saved yet: open on our own server
        var outp = new Dictionary<string, object?> { ["saved"] = saved, ["servers"] = ids, ["pools"] = MeteoritePools(ids), ["roundHours"] = MeteoriteRoundHours };
        if (ids.Count == 0) return outp;
        var arr = ids.Select(i => (long)i).ToArray();
        using var c = Open();
        var win = PlayerActivity.WindowStart(Db.NowIso);
        // Everyone we know on these servers: the latest hero board (exact), everyone whose newest profile
        // says they are from here, and every base on the newest map. Server = board, then home, then map.
        var rows = Q(c, $"""
            WITH lb AS (SELECT server_id, MAX(taken_at) AS at FROM board_sample WHERE board_type = 13 AND server_id = ANY(@s) GROUP BY server_id),
                 b AS (SELECT DISTINCT ON (bs.uid) bs.uid, bs.value, bs.server_id FROM board_sample bs
                       JOIN lb ON lb.server_id = bs.server_id AND lb.at = bs.taken_at WHERE bs.board_type = 13 ORDER BY bs.uid, bs.value DESC),
                 ls AS (SELECT DISTINCT ON (server_id) server_id, scan_id FROM map_scan WHERE server_id = ANY(@s) ORDER BY server_id, taken_at DESC),
                 t AS (SELECT DISTINCT ON (mt.owner_uid) mt.owner_uid, mt.hq_level, mt.server_id FROM map_tile mt
                       JOIN ls ON ls.scan_id = mt.scan_id WHERE mt.owner_uid <> '' ORDER BY mt.owner_uid),
                 u AS (SELECT uid FROM player_latest WHERE server = ANY(@s) UNION SELECT uid FROM b UNION SELECT owner_uid FROM t)
            SELECT u.uid, pl.name, pl.tag, pl.alliance_id AS "allianceId", COALESCE(b.server_id, pl.server, t.server_id) AS server,
                   b.value AS board, pl.hero AS est, t.hq_level AS hq, pl.power,
                   (pa.last_active_at >= {win}) AS active7, NULLIF(pa.last_seen_at, '') AS "lastSeen"
            FROM u LEFT JOIN player_latest pl ON pl.uid = u.uid LEFT JOIN b ON b.uid = u.uid
            LEFT JOIN t ON t.owner_uid = u.uid LEFT JOIN player_activity pa ON pa.uid = u.uid;
            """, ("@s", arr));
        var boards = Q(c, "SELECT server_id AS s, MAX(taken_at) AS at FROM board_sample WHERE board_type = 13 AND server_id = ANY(@s) GROUP BY server_id;", ("@s", arr))
            .ToDictionary(r => Convert.ToInt32(r["s"]), r => r["at"]);
        var maps = Q(c, "SELECT server_id AS s, MAX(taken_at) AS at FROM map_scan WHERE server_id = ANY(@s) GROUP BY server_id;", ("@s", arr))
            .ToDictionary(r => Convert.ToInt32(r["s"]), r => r["at"]);
        var info = Q(c, "SELECT server_id AS s, name, king_name AS president, king_tag AS \"presidentTag\" FROM server_info WHERE server_id = ANY(@s);", ("@s", arr))
            .ToDictionary(r => Convert.ToInt32(r["s"]), r => r);
        var track = reg.Tracking();
        var on = AllOnline(30);
        var nowHour = on.Window.NowHour;
        // Days with a sign of life around the round: its UTC hour or the hour either side (00:10 catches late-night 23:xx players too).
        int RoundDays(string uid, int hour) =>
            on.Hours.TryGetValue(uid, out var hs) ? hs.Where(h => Near(h % 24, hour) && nowHour - h < 720).Select(h => h / 24).Distinct().Count() : 0;
        // "Likely on" a round: seen in a past round of that number, or around its hour on a quarter of the days we have readings for (at least 2).
        var LikelyDays = Math.Max(2, (int)Math.Ceiling(on.Window.Days / 4.0));

        var players = new List<Dictionary<string, object?>>();
        foreach (var r in rows)
        {
            var server = r["server"] is null ? 0 : Convert.ToInt32(r["server"]);
            if (!ids.Contains(server)) continue;
            var hero = r["board"] ?? r["est"];
            r["hero"] = hero is null ? null : Convert.ToInt64(hero);
            r["exact"] = r["board"] is not null;
            r["server"] = server;
            r.Remove("board"); r.Remove("est");
            players.Add(r);
        }
        long H(Dictionary<string, object?> r) => r["hero"] is long h ? h : 0;
        players.Sort((a, b) => H(b).CompareTo(H(a)));

        var top = players.Where(p => p["hero"] is not null).Take(200).Select((p, i) =>
        {
            var uid = (string)p["uid"]!;
            var o = OnlineOf(on.Hours.GetValueOrDefault(uid), nowHour);
            return new Dictionary<string, object?>(p)
            {
                ["rank"] = i + 1, ["hours7"] = o["hours7"], ["days30"] = o["days30"],
                ["round1Days"] = RoundDays(uid, MeteoriteRoundHours[0]), ["round2Days"] = RoundDays(uid, MeteoriteRoundHours[1]),
            };
        }).ToList();

        // Likely to turn up: seen in a past Meteorite round of the same number, or often on in that UTC hour.
        var hist = MeteoriteHistory(c, top.Select(t => (string)t["uid"]!).ToList(), out var r1Of, out var r2Of);
        foreach (var t in top)
        {
            var (a, b) = hist.GetValueOrDefault((string)t["uid"]!);
            t["meteorR1"] = a; t["meteorR2"] = b;
            t["likely1"] = a > 0 || (int)t["round1Days"]! >= LikelyDays;
            t["likely2"] = b > 0 || (int)t["round2Days"]! >= LikelyDays;
        }
        outp["pastRounds"] = new Dictionary<string, object?> { ["r1"] = r1Of, ["r2"] = r2Of };
        outp["likelyDays"] = LikelyDays;

        double AtHour(Dictionary<string, object?> total, int h) =>
            total["hourDays"] is int[] hd && on.Window.Days > 0 ? Math.Round(hd[h] / (double)on.Window.Days, 1) : 0;
        var servers = ids.Select(id =>
        {
            var list = players.Where(p => (int)p["server"]! == id).ToList();
            var heroes = list.Select(H).Where(h => h > 0).ToList();   // already sorted, strongest first
            long Sum(int n) => heroes.Take(n).Sum();
            var uids = list.Select(p => (string)p["uid"]!).ToList();
            var all = (Dictionary<string, object?>)OnlineGroup(on.Window, uids, on.Hours, on.Country)["total"]!;
            var top100 = (Dictionary<string, object?>)OnlineGroup(on.Window, uids.Take(100), on.Hours, on.Country)["total"]!;
            int Band(Func<long, bool> f, bool activeOnly = false) =>
                list.Count(p => p["hq"] is not null && f(Convert.ToInt64(p["hq"])) && (!activeOnly || p["active7"] is true));
            var i = info.GetValueOrDefault(id);
            return new Dictionary<string, object?>
            {
                ["server"] = id, ["name"] = i?["name"], ["president"] = i?["president"], ["presidentTag"] = i?["presidentTag"],
                ["level"] = track.TryGetValue(id, out var t) ? t.Level : "off",
                ["boardAt"] = boards.GetValueOrDefault(id), ["mappedAt"] = maps.GetValueOrDefault(id),
                ["players"] = list.Count, ["measured"] = heroes.Count, ["exact"] = list.Count(p => p["exact"] is true),
                ["active7"] = list.Count(p => p["active7"] is true),
                ["top10"] = Sum(10), ["top20"] = Sum(20), ["top50"] = Sum(50), ["top100"] = Sum(100),
                ["hero1"] = heroes.Count > 0 ? heroes[0] : null, ["hero50"] = heroes.Count >= 50 ? heroes[49] : null, ["hero100"] = heroes.Count >= 100 ? heroes[99] : null,
                ["hq30"] = Band(h => h >= 30), ["hq30Active"] = Band(h => h >= 30, true),
                ["hq22"] = Band(h => h is >= 22 and < 30), ["hq21"] = Band(h => h is > 0 and <= 21), ["hqKnown"] = list.Count(p => p["hq"] is not null),
                ["onlineRound1"] = AtHour(all, MeteoriteRoundHours[0]), ["onlineRound2"] = AtHour(all, MeteoriteRoundHours[1]),
                ["topOnlineRound1"] = AtHour(top100, MeteoriteRoundHours[0]), ["topOnlineRound2"] = AtHour(top100, MeteoriteRoundHours[1]),
                ["hourDays"] = all["hourDays"], ["topHourDays"] = top100["hourDays"],
                ["likely1"] = top.Count(t => (int)t["server"]! == id && t["likely1"] is true),
                ["likely2"] = top.Count(t => (int)t["server"]! == id && t["likely2"] is true),
                ["inTop200"] = top.Count(t => (int)t["server"]! == id),
            };
        }).ToList();

        var alliances = players.Where(p => p["allianceId"] is string a && a.Length > 0 && p["hero"] is not null)
            .GroupBy(p => (string)p["allianceId"]!)
            .Select(g =>
            {
                var hs = g.Select(H).ToList();   // strongest first (players is sorted)
                var first = g.First();
                return new Dictionary<string, object?>
                {
                    ["allianceId"] = g.Key, ["tag"] = g.Select(p => p["tag"] as string).FirstOrDefault(s => !string.IsNullOrEmpty(s)),
                    ["server"] = g.GroupBy(p => p["server"]).OrderByDescending(x => x.Count()).First().Key,
                    ["members"] = g.Count(), ["hero"] = hs.Sum(), ["top20"] = hs.Take(20).Sum(), ["top"] = first["name"], ["topHero"] = hs[0],
                    ["inTop50"] = top.Take(50).Count(t => (string?)t["allianceId"] == g.Key),
                    ["hq30"] = g.Count(p => p["hq"] is not null && Convert.ToInt64(p["hq"]) >= 30),
                    ["active7"] = g.Count(p => p["active7"] is true),
                };
            })
            .OrderByDescending(a => (long)a["top20"]!).Take(25).ToList();

        outp["top"] = top;
        outp["serverStats"] = servers;
        outp["alliances"] = alliances;
        outp["days"] = on.Window.Days;
        outp["online"] = OnlineGroup(on.Window, players.Select(p => (string)p["uid"]!), on.Hours, on.Country);
        outp["onlineTop"] = OnlineGroup(on.Window, top.Take(50).Select(p => (string)p["uid"]!), on.Hours, on.Country);
        return outp;
    }

    /// <summary>The game's weekly matchmaking pools that hold any of these servers (newest first).</summary>
    List<Dictionary<string, object?>> MeteoritePools(List<int> ids)
    {
        if (ids.Count == 0) return [];
        using var c = Open();
        var today = DateTime.UtcNow.Date;
        static bool Holds(string list, int s) => ServerRegistry.Sides(list).Any(side => side.Contains(s));
        static DateTime? D(object? v) => DateTime.TryParse(v as string, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        return Q(c, "SELECT id, season, open_time AS open, close_time AS close, group_list AS groups FROM game_event_group ORDER BY id DESC;")
            .Where(r => r["groups"] is string g && ids.Any(s => Holds(g, s)))
            .Take(6)
            .Select(r =>
            {
                r["current"] = D(r["open"]) <= today && today < D(r["close"]);
                r["holds"] = ids.Where(s => Holds((string)r["groups"]!, s)).ToList();
                return r;
            }).ToList();
    }
}
