using System.Text.RegularExpressions;

namespace LastWar.Storage;

/// <summary>
/// The alliance page's Activity tab: when its members are in the game, and how much estimated hero
/// power is on at each hour of the day (UTC).
///
/// Members are the newest al.rank roster (intel.sweep), else the newest map/profile sighting
/// (player_latest). Evidence per member, last 30 days, same rules as the player report:
/// profile last-seen moments (game-side mass touches left out, see PlayerActivity), donation
/// moments and chat messages - things a player has to do.
///
/// "On at hour h" = the member has at least one moment in hour h on any day in the window.
/// Strength at h = the sum of those members' hero power (profile estimate, else their hero
/// board value). Members with no timed evidence are shown separately, never guessed.
/// </summary>
public sealed partial class Insights
{
    static readonly Regex Digits = new("^[0-9]+$");

    /// <summary>
    /// Chance each uid is in the game at each UTC hour: the share of observed days with a moment
    /// (last seen outside a mass touch, a donation, a chat message) in that hour. A moment in the hour
    /// either side counts half - sessions cross hour boundaries. Observed days = days since our first
    /// profile reading (older last-seen moments are only the last login of whoever was away, not a day).
    /// ponytail: days with few readings dilute everyone evenly (fine for comparing two sides); weight
    /// by readings per day once the hourly pulse has weeks of history.
    /// </summary>
    Dictionary<string, double[]> OnByHour(Npgsql.NpgsqlConnection c, List<string> uids, int days, out int observed)
    {
        var result = new Dictionary<string, double[]>();
        observed = 0;
        var ids = uids.Where(u => Digits.IsMatch(u)).Distinct().ToList();   // digits only: safe to inline
        if (ids.Count == 0) return result;
        var inList = string.Join(",", ids.Select(u => $"'{u}'"));
        var first = T(Q(c, "SELECT MIN(taken_at) AS t FROM player_profile;")[0]["t"]) ?? DateTimeOffset.UtcNow;
        var since = new[] { DateTimeOffset.UtcNow.AddDays(-days), new DateTimeOffset(first.UtcDateTime.Date, TimeSpan.Zero) }.Max();
        var hits = new HashSet<(string Uid, DateTime Day, int Hour)>();
        foreach (var r in Q(c, $"""
            SELECT uid, seen_at AS at FROM player_seen
              WHERE uid IN ({inList}) AND seen_at >= @s AND NOT {PlayerActivity.NotALogin("seen_at")}
            UNION SELECT uid, donated_at FROM alliance_member_sample WHERE uid IN ({inList}) AND donated_at >= @s
            UNION SELECT sender_uid, sent_at FROM chat_message WHERE sender_uid IN ({inList}) AND sent_at >= @s;
            """, ("@s", since.ToString("O"))))
            if (T(r["at"]) is { } t) hits.Add(((string)r["uid"]!, t.UtcDateTime.Date, t.UtcDateTime.Hour));
        observed = (int)(DateTimeOffset.UtcNow.UtcDateTime.Date - since.UtcDateTime.Date).TotalDays + 1;
        foreach (var g in hits.GroupBy(x => x.Uid))
        {
            var p = new double[24];
            foreach (var d in g.GroupBy(x => x.Day))
            {
                var hs = d.Select(x => x.Hour).ToHashSet();
                for (var h = 0; h < 24; h++)
                    p[h] += hs.Contains(h) ? 1 : hs.Contains((h + 23) % 24) || hs.Contains((h + 1) % 24) ? 0.5 : 0;
            }
            for (var h = 0; h < 24; h++) p[h] /= observed;
            result[g.Key] = p;
        }
        return result;
    }

    public Dictionary<string, object?> AllianceActivity(string allianceId, int days = 30)
    {
        using var c = Open();
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-days);
        var rosterAt = Q(c, "SELECT MAX(taken_at) AS t FROM alliance_member_sample WHERE alliance_id = @a;", ("@a", allianceId))[0]["t"] as string;
        var members = rosterAt is not null
            ? Q(c, "SELECT uid, name, power, rank FROM alliance_member_sample WHERE alliance_id = @a AND taken_at = @t;", ("@a", allianceId), ("@t", rosterAt))
            : Q(c, "SELECT uid, name, NULL AS power, NULL AS rank FROM player_latest WHERE alliance_id = @a;", ("@a", allianceId));
        // uids are digits; anything else is dropped so the IN list below can be inlined safely.
        members = members.Where(m => m["uid"] is string u && Digits.IsMatch(u)).GroupBy(m => (string)m["uid"]!).Select(g => g.First()).ToList();
        if (members.Count == 0) return new() { ["allianceId"] = allianceId, ["members"] = members };
        var inList = string.Join(",", members.Select(m => $"'{m["uid"]}'"));
        var sinceIso = since.ToString("O");

        // Hero power per member: estimate from the newest profile, else newest hero-board value.
        var hero = new Dictionary<string, (long Value, bool Estimated)>();
        foreach (var r in Q(c, $"SELECT uid, hero FROM player_latest WHERE uid IN ({inList}) AND hero IS NOT NULL;"))
            hero[(string)r["uid"]!] = (L(r["hero"]), true);
        foreach (var r in Q(c, $"""
            SELECT uid, value FROM board_sample b WHERE board_type = 13 AND uid IN ({inList})
              AND taken_at = (SELECT MAX(taken_at) FROM board_sample x WHERE x.uid = b.uid AND x.board_type = 13);
            """))
            hero.TryAdd((string)r["uid"]!, (L(r["value"]), false));

        // Every timed moment per member.
        var moments = new List<(string Uid, DateTimeOffset At, string Kind)>();
        void Add(string sql, string kind)
        {
            foreach (var r in Q(c, sql, ("@s", sinceIso)))
                if (T(r["at"]) is { } t && t >= since) moments.Add(((string)r["uid"]!, t, kind));
        }
        Add($"SELECT uid, seen_at AS at FROM player_seen WHERE uid IN ({inList}) AND seen_at >= @s AND NOT {PlayerActivity.NotALogin("seen_at")};", "seen");
        Add($"SELECT DISTINCT uid, donated_at AS at FROM alliance_member_sample WHERE uid IN ({inList}) AND donated_at >= @s;", "donated");
        Add($"SELECT sender_uid AS uid, sent_at AS at FROM chat_message WHERE sender_uid IN ({inList}) AND sent_at >= @s;", "chat");
        // Power rises are not used: queued builds and research finish while a player is away.

        var kinds = new[] { "seen", "donated", "chat", "rise", "other" };
        var byKind = kinds.ToDictionary(x => x, _ => new int[24]);
        foreach (var m in moments) byKind[m.Kind][m.At.Hour]++;
        // Shown in "when in the day" only, never as "on": a power rise or troop/kill/HQ change is known
        // only to fall between two readings, so it counts (at the midpoint) when that gap is <= 3 h -
        // the same rule as the player report.
        void Between(string sql, string kind)
        {
            foreach (var r in Q(c, sql, ("@s", sinceIso)))
                if (T(r["at"]) is { } at && T(r["prev"]) is { } prev && at - prev <= TimeSpan.FromHours(3))
                    byKind[kind][(prev + (at - prev) / 2).Hour]++;
        }
        Between($"""
            SELECT at, prev FROM (SELECT taken_at AS at, LAG(taken_at) OVER w AS prev, power - LAG(power) OVER w AS d
              FROM power_sample WHERE uid IN ({inList}) AND taken_at >= @s WINDOW w AS (PARTITION BY uid ORDER BY taken_at)) x WHERE d > 0;
            """, "rise");
        Between($"""
            SELECT at, prev FROM (SELECT taken_at AS at, LAG(taken_at) OVER w AS prev,
                     army_power AS t, LAG(army_power) OVER w AS pt, army_kill AS k, LAG(army_kill) OVER w AS pk, level AS l, LAG(level) OVER w AS pl
              FROM player_profile WHERE uid IN ({inList}) AND taken_at >= @s WINDOW w AS (PARTITION BY uid ORDER BY taken_at)) x
            WHERE (pt > 0 AND t <> pt) OR (pk > 0 AND k > pk) OR (pl > 0 AND l > pl);
            """, "other");
        var hoursOf = moments.GroupBy(m => m.Uid).ToDictionary(g => g.Key, g => g.Select(m => m.At.Hour).Distinct().OrderBy(h => h).ToList());
        var lastOf = moments.GroupBy(m => m.Uid).ToDictionary(g => g.Key, g => g.Max(m => m.At));
        // The last 24 hours only - a rolling "today", so the column never empties at midnight.
        var hours24Of = moments.Where(m => m.At >= now.AddHours(-24)).GroupBy(m => m.Uid)
            .ToDictionary(g => g.Key, g => g.Select(m => m.At.Hour).Distinct().OrderBy(h => h).ToList());
        long HeroOf(string uid) => hero.TryGetValue(uid, out var h) ? h.Value : 0;

        // Active hours (24 h / 7 d / 30 d) and the per-country split - the same counting as the server pages.
        var uids = members.Select(m => (string)m["uid"]!).ToList();
        var window = Window(c, days);
        var online = OnlineHours(c, now.AddDays(-30), uids);
        var country = Countries(c, uids);

        var hours = Enumerable.Range(0, 24).Select(h =>
        {
            var on = members.Select(m => (string)m["uid"]!).Where(u => hoursOf.TryGetValue(u, out var hs) && hs.Contains(h)).ToList();
            return new Dictionary<string, object?>
            {
                ["hour"] = h, ["members"] = on.Count, ["hero"] = on.Sum(HeroOf),
                // The strongest on at this hour, for the "who" list when an hour is picked.
                ["top"] = on.OrderByDescending(HeroOf).Take(15).ToList(),
            };
        }).ToList();

        var totalHero = members.Sum(m => HeroOf((string)m["uid"]!));
        var timed = members.Count(m => hoursOf.ContainsKey((string)m["uid"]!));
        // Strength by how recently each member showed any sign (any kind, the full window).
        long Bucket(double lo, double hi) => members.Where(m => lastOf.TryGetValue((string)m["uid"]!, out var t) && (now - t).TotalHours > lo && (now - t).TotalHours <= hi).Sum(m => HeroOf((string)m["uid"]!));
        var recency = new List<Dictionary<string, object?>>
        {
            new() { ["label"] = "last 24 h", ["hero"] = Bucket(-1, 24) },
            new() { ["label"] = "1–3 days", ["hero"] = Bucket(24, 72) },
            new() { ["label"] = "3–7 days", ["hero"] = Bucket(72, 168) },
            new() { ["label"] = $"7–{days} days", ["hero"] = Bucket(168, days * 24) },
            new() { ["label"] = "no timed sign", ["hero"] = members.Where(m => !lastOf.ContainsKey((string)m["uid"]!)).Sum(m => HeroOf((string)m["uid"]!)) },
        };

        var daysList = Enumerable.Range(0, days).Select(i => now.UtcDateTime.Date.AddDays(-days + 1 + i)).Select(day =>
        {
            var on = moments.Where(m => m.At.UtcDateTime.Date == day).Select(m => m.Uid).Distinct().ToList();
            return new Dictionary<string, object?> { ["day"] = day.ToString("yyyy-MM-dd"), ["members"] = on.Count, ["hero"] = on.Sum(HeroOf) };
        }).ToList();

        // Hours we took readings in. Last-seen and donation moments pile up just before a reading
        // (anyone on then shows "a few minutes ago"), so those hours read high until readings are
        // spread through the day.
        var sampled = new int[24];
        foreach (var r in Q(c, """
            SELECT DISTINCT substr(taken_at, 1, 13) AS h FROM profile_reading WHERE taken_at >= @s
            UNION SELECT DISTINCT substr(taken_at, 1, 13) FROM alliance_member_sample WHERE alliance_id = @a AND taken_at >= @s;
            """, ("@s", sinceIso), ("@a", allianceId)))
            if (r["h"] is string s && s.Length == 13 && int.TryParse(s[11..], out var hr)) sampled[hr]++;

        return new()
        {
            ["allianceId"] = allianceId, ["rosterAt"] = rosterAt, ["days"] = days, ["sampled"] = sampled,
            ["memberCount"] = members.Count, ["timed"] = timed, ["totalHero"] = totalHero,
            ["heroKnown"] = members.Count(m => hero.ContainsKey((string)m["uid"]!)),
            ["moments"] = moments.Count,
            ["byKind"] = byKind, ["hours"] = hours, ["recency"] = recency, ["perDay"] = daysList,
            ["online"] = OnlineGroup(window, uids, online, country),
            ["members"] = members.Select(m =>
            {
                var u = (string)m["uid"]!;
                return new Dictionary<string, object?>
                {
                    ["uid"] = u, ["name"] = m["name"], ["rank"] = m["rank"], ["power"] = m["power"],
                    ["hero"] = hero.TryGetValue(u, out var h) ? h.Value : null, ["heroEstimated"] = hero.TryGetValue(u, out var h2) && h2.Estimated,
                    ["hours"] = hoursOf.GetValueOrDefault(u) ?? [],
                    ["hours24"] = hours24Of.GetValueOrDefault(u) ?? [],
                    ["moments"] = moments.Count(x => x.Uid == u),
                    ["last"] = lastOf.TryGetValue(u, out var t) ? t.ToString("O") : null,
                    ["country"] = country.GetValueOrDefault(u),
                    ["online"] = OnlineOf(online.GetValueOrDefault(u), window.NowHour),
                };
            }).OrderByDescending(m => m["hero"] as long? ?? 0).ToList(),
        };
    }
}
