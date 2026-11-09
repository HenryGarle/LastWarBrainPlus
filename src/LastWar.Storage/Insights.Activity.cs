using System.Globalization;

namespace LastWar.Storage;

/// <summary>
/// The player page's Activity and Drone tabs: every trace a player leaves that proves they were in
/// the game, and WHEN - built only from data already stored.
///
///   seen      profile lastUpdateTime (scan.profiles) - one moment per distinct value
///   donated   al.rank donateTime (intel.sweep rosters) - one moment per distinct value
///   chat      every message they sent
///   rise      power went up between two readings (only a narrow gap says what hour)
///   troops    troop power changed between profiles (training up, or losses)
///   kills     kill count went up (they fought)
///   hq        HQ level went up
///   moved     base moved on the map (teleport), alliance or home server changed
///
/// Each moment is exact except a rise, which is only known to fall between two readings; a rise
/// counts towards an hour only when that gap is 3 hours or less.
/// </summary>
public sealed partial class Insights
{
    static DateTimeOffset? T(object? v) =>
        v is string s && s.Length > 0 && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    static long L(object? v) => v is null ? 0 : Convert.ToInt64(v);

    public Dictionary<string, object?> PlayerActivityReport(string uid)
    {
        using var c = Open();
        var now = DateTimeOffset.UtcNow;
        var events = new List<(DateTimeOffset At, string Kind, string Detail, DateTimeOffset? From)>();

        // Login moments (player_seen): each with the troops, kills and HQ the player had then.
        var seen = Q(c, $"""
            SELECT seen_at AS at, {PlayerActivity.NotALogin("seen_at")} AS touch, army_power AS troops, army_kill AS kills, level
            FROM player_seen WHERE uid = @u ORDER BY seen_at;
            """, ("@u", uid));
        // A last-seen inside a game-side mass touch (PlayerActivity.mass_touch) is listed, never counted.
        foreach (var s in seen)
            events.Add(s["touch"] is true
                ? (T(s["at"])!.Value, "touch", "last update moved with thousands of other accounts at once - the game, not a login", null)
                : (T(s["at"])!.Value, "seen", "in the game (profile last update)", null));
        // Full profiles (the daily sweep): troops, kills and HQ too, plus alliance and home changes.
        var profiles = Q(c, """
            SELECT taken_at AS at, army_power AS troops, army_kill AS kills, level,
                   home_server AS home, alliance_id AS "allianceId", alliance_tag AS tag, svip_level AS svip, month_card_end AS card
            FROM player_profile WHERE uid = @u ORDER BY taken_at;
            """, ("@u", uid));
        var readings = profiles.Concat(seen).OrderBy(r => T(r["at"])).ToList();
        for (var i = 1; i < readings.Count; i++)
        {
            var (a, b) = (readings[i - 1], readings[i]);
            var (from, at) = (T(a["at"]), T(b["at"])!.Value);
            var dt = L(b["troops"]) - L(a["troops"]);
            if (dt != 0 && L(a["troops"]) > 0 && L(b["troops"]) > 0) events.Add((at, "troops", dt > 0 ? $"troop power +{dt:N0}" : $"troop power {dt:N0} (losses or dismissed)", from));
            var dk = L(b["kills"]) - L(a["kills"]);
            if (dk > 0 && L(a["kills"]) > 0) events.Add((at, "kills", $"+{dk:N0} kills", from));
            if (L(b["level"]) > L(a["level"]) && L(a["level"]) > 0) events.Add((at, "hq", $"HQ {a["level"]} → {b["level"]}", from));
        }
        for (var i = 1; i < profiles.Count; i++)
        {
            var (a, b) = (profiles[i - 1], profiles[i]);
            var (from, at) = (T(a["at"]), T(b["at"])!.Value);
            if (!Equals(a["allianceId"], b["allianceId"])) events.Add((at, "moved", $"alliance {(a["tag"] as string is { Length: > 0 } t0 ? t0 : "none")} → {(b["tag"] as string is { Length: > 0 } t1 ? t1 : "none")}", from));
            if (!Equals(a["home"], b["home"]) && L(a["home"]) > 0) events.Add((at, "moved", $"home server #{a["home"]} → #{b["home"]}", from));
        }
        // Power: every reading of any kind (map detail, roster, profile), rises only.
        var power = Q(c, "SELECT taken_at AS at, power FROM power_sample WHERE uid = @u ORDER BY taken_at;", ("@u", uid));
        for (var i = 1; i < power.Count; i++)
        {
            var d = L(power[i]["power"]) - L(power[i - 1]["power"]);
            if (d > 0) events.Add((T(power[i]["at"])!.Value, "rise", $"power +{d:N0}", T(power[i - 1]["at"])));
        }

        // Donations: each distinct last-donation moment from every roster read.
        var roster = Q(c, """
            SELECT taken_at AS at, alliance_id AS "allianceId", donate_today AS today, donate_week AS week, NULLIF(donated_at, '') AS "donatedAt"
            FROM alliance_member_sample WHERE uid = @u ORDER BY taken_at;
            """, ("@u", uid));
        foreach (var d in roster.Select(r => T(r["donatedAt"])).OfType<DateTimeOffset>().Distinct())
            events.Add((d, "donated", "donated to the alliance", null));

        // Base moves on the map (a teleport is a login).
        var tiles = Q(c, """
            SELECT s.taken_at AS at, t.server_id AS server, t.point_id AS point FROM map_tile t JOIN map_scan s ON s.scan_id = t.scan_id
            WHERE t.owner_uid = @u ORDER BY s.taken_at;
            """, ("@u", uid));
        for (var i = 1; i < tiles.Count; i++)
            if (!Equals(tiles[i]["point"], tiles[i - 1]["point"]) || !Equals(tiles[i]["server"], tiles[i - 1]["server"]))
                events.Add((T(tiles[i]["at"])!.Value, "moved", $"base moved on the map (#{tiles[i]["server"]})", T(tiles[i - 1]["at"])));

        var chat = Q(c, "SELECT sent_at AS at FROM chat_message WHERE sender_uid = @u;", ("@u", uid))
            .Select(r => T(r["at"])).OfType<DateTimeOffset>().ToList();

        // When in the day (UTC). Exact moments count as they are; a bracketed one only when narrow.
        var kinds = new[] { "seen", "donated", "chat", "rise", "other" };
        var hours = kinds.ToDictionary(k => k, _ => new int[24]);
        foreach (var t in chat) hours["chat"][t.Hour]++;
        foreach (var e in events)
        {
            if (e.Kind == "touch") continue;
            var key = e.Kind is "seen" or "donated" or "rise" ? e.Kind : "other";
            if (e.From is { } f)
            {
                if (e.At - f > TimeSpan.FromHours(3)) continue;
                hours[key][(f + (e.At - f) / 2).Hour]++;
            }
            else hours[key][e.At.Hour]++;
        }

        // Per day, last 30 days: which days show any sign of life.
        var days = Enumerable.Range(0, 30).Select(i => now.UtcDateTime.Date.AddDays(-29 + i)).Select(day => new Dictionary<string, object?>
        {
            ["day"] = day.ToString("yyyy-MM-dd"),
            ["chat"] = chat.Count(t => t.UtcDateTime.Date == day),
            ["seen"] = events.Count(e => e.Kind == "seen" && e.At.UtcDateTime.Date == day),
            ["donated"] = events.Count(e => e.Kind == "donated" && e.At.UtcDateTime.Date == day),
            ["rise"] = events.Count(e => e.Kind == "rise" && e.At.UtcDateTime.Date == day),
            ["other"] = events.Count(e => e.Kind is not ("seen" or "donated" or "rise" or "touch") && e.At.UtcDateTime.Date == day),
        }).ToList();

        // Donations against the rest of the alliance in the same roster read.
        var r0 = roster.LastOrDefault();
        Dictionary<string, object?>? donation = null;
        if (r0 is not null)
        {
            var mates = Q(c, "SELECT donate_week AS week, donate_today AS today FROM alliance_member_sample WHERE alliance_id = @a AND taken_at = @t;",
                ("@a", r0["allianceId"]!), ("@t", r0["at"]!));
            var weeks = mates.Select(m => L(m["week"])).OrderBy(x => x).ToList();
            donation = new()
            {
                ["at"] = r0["at"], ["today"] = r0["today"], ["week"] = r0["week"], ["last"] = r0["donatedAt"],
                ["place"] = weeks.Count(w => w > L(r0["week"])) + 1, ["members"] = weeks.Count,
                ["median"] = weeks.Count > 0 ? weeks[weeks.Count / 2] : 0,
                ["gaveToday"] = mates.Count(m => L(m["today"]) > 0),
                ["history"] = roster.Select(r => new Dictionary<string, object?> { ["at"] = r["at"], ["today"] = r["today"], ["week"] = r["week"], ["last"] = r["donatedAt"] }).ToList(),
            };
        }

        DateTimeOffset? Last(string kind) => events.Where(e => e.Kind == kind).Select(e => (DateTimeOffset?)e.At).Max();
        var lastChat = chat.Count > 0 ? chat.Max() : (DateTimeOffset?)null;
        // Only what a player has to DO: power, troops, kills and HQ also move while they are away.
        var lastActive = new[] { Last("seen"), Last("donated"), lastChat }.Max();
        long Gain(int d)
        {
            var since = now.AddDays(-d);
            var old = power.FirstOrDefault(p => T(p["at"]) >= since);
            return old is null || power.Count == 0 ? 0 : L(power[^1]["power"]) - L(old["power"]);
        }
        var last = profiles.LastOrDefault();
        var card = last is null ? 0 : L(last["card"]);

        // A simple, visible score (0-100). Each part is shown with its reason on the page.
        var parts = new List<Dictionary<string, object?>>();
        void Part(string name, int got, int of, string why) => parts.Add(new() { ["name"] = name, ["score"] = got, ["of"] = of, ["why"] = why });
        var ago = lastActive is { } la ? (now - la).TotalHours : double.MaxValue;
        Part("Recently in the game", ago <= 6 ? 40 : ago <= 24 ? 34 : ago <= 72 ? 22 : ago <= 168 ? 10 : 0, 40,
            lastActive is null ? "no sign of them at all" : $"last sign {Ago(now - lastActive.Value)} ago");
        if (donation is not null)
        {
            var (w, med) = (L(donation["week"]), Math.Max(1, L(donation["median"])));
            Part("Donating", w <= 0 ? 0 : w >= 2 * med ? 20 : w >= med ? 15 : 8, 20, $"{w:N0} this week, alliance median {L(donation["median"]):N0} (#{donation["place"]} of {donation["members"]})");
        }
        else Part("Donating", 0, 20, "no alliance roster read yet");
        var daysActive = days.Skip(23).Count(d => (int)d["seen"]! + (int)d["donated"]! + (int)d["chat"]! > 0);
        Part("Days with a sign of life (last 7)", (int)Math.Round(daysActive / 7.0 * 20), 20, $"{daysActive} of the last 7 days");
        var gain7 = Gain(7);
        var pow = power.Count > 0 ? L(power[^1]["power"]) : 0;
        Part("Growing", gain7 <= 0 || pow == 0 ? 0 : gain7 * 100.0 / pow >= 3 ? 20 : gain7 * 100.0 / pow >= 1 ? 14 : 8, 20,
            pow == 0 ? "no power readings" : $"{(gain7 >= 0 ? "+" : "")}{gain7:N0} power in 7 days ({gain7 * 100.0 / Math.Max(1, pow):0.0}%)");
        // The monthly card is a purchase (it stacks, up to months ahead): it says they spend, not that
        // they play, so it is shown on the page (monthCard) but not scored.

        return new()
        {
            ["uid"] = uid,
            ["score"] = parts.Sum(p => (int)p["score"]!),
            ["parts"] = parts,
            ["lastActive"] = lastActive?.ToString("O"),
            ["last"] = new Dictionary<string, object?>
            {
                ["seen"] = Last("seen")?.ToString("O"), ["donated"] = Last("donated")?.ToString("O"), ["rise"] = Last("rise")?.ToString("O"),
                ["chat"] = lastChat?.ToString("O"), ["troops"] = Last("troops")?.ToString("O"), ["kills"] = Last("kills")?.ToString("O"),
                ["hq"] = Last("hq")?.ToString("O"), ["moved"] = Last("moved")?.ToString("O"),
            },
            ["chat7"] = chat.Count(t => t >= now.AddDays(-7)), ["chat30"] = chat.Count(t => t >= now.AddDays(-30)), ["chatAll"] = chat.Count,
            ["gain1"] = Gain(1), ["gain7"] = gain7, ["gain30"] = Gain(30), ["power"] = pow,
            ["readings"] = new Dictionary<string, object?> { ["power"] = power.Count, ["profiles"] = profiles.Count, ["rosters"] = roster.Count, ["map"] = tiles.Count, ["seenMoments"] = events.Count(e => e.Kind == "seen") },
            ["svip"] = last?["svip"], ["monthCard"] = card > 0 ? DateTimeOffset.FromUnixTimeSeconds(card).ToString("O") : null,
            ["donation"] = donation,
            ["hours"] = hours,
            ["days"] = days,
            ["events"] = events.OrderByDescending(e => e.At).Take(300).Select(e => new Dictionary<string, object?>
            {
                ["at"] = e.At.ToString("O"), ["from"] = e.From?.ToString("O"), ["kind"] = e.Kind, ["detail"] = e.Detail,
            }).ToList(),
        };
    }

    static string Ago(TimeSpan t) => t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min" : t.TotalHours < 48 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalDays} days";

    /// <summary>
    /// Drone and chip readings over time (intel.players), and where they stand: against their home
    /// server and against players of similar power (within 10%) anywhere.
    /// </summary>
    public Dictionary<string, object?> PlayerDrone(string uid)
    {
        using var c = Open();
        var history = Q(c, """
            SELECT taken_at AS at, drone_level AS level, drone_power AS power, chip_level AS chip, drone_props AS props,
                   career_type AS "careerType", career_level AS "careerLevel", gift_level AS "giftLevel"
            FROM player_detail_sample WHERE uid = @u ORDER BY taken_at;
            """, ("@u", uid));
        if (history.Count == 0) return new() { ["uid"] = uid, ["history"] = history };
        var me = history[^1];
        // Everyone's newest drone read with their newest profile power and home server.
        var all = Q(c, """
            WITH d AS (SELECT uid, drone_level AS level, drone_power AS dpower, chip_level AS chip FROM player_detail_sample x
                       WHERE taken_at = (SELECT MAX(taken_at) FROM player_detail_sample y WHERE y.uid = x.uid))
            SELECT d.uid, d.level, d.dpower, d.chip, pl.server,
                   (SELECT p.power FROM player_profile p WHERE p.uid = d.uid ORDER BY p.taken_at DESC LIMIT 1) AS power
            FROM d LEFT JOIN player_latest pl ON pl.uid = d.uid;
            """);
        var mine = all.FirstOrDefault(a => (string?)a["uid"] == uid);
        var (myPower, myServer) = (L(mine?["power"]), mine?["server"]);

        Dictionary<string, object?> Compare(string label, List<Dictionary<string, object?>> peers)
        {
            double Pct(string col, long v) => peers.Count == 0 ? 0 : peers.Count(p => L(p[col]) < v) * 100.0 / peers.Count;
            long Med(string col) => peers.Count == 0 ? 0 : peers.Select(p => L(p[col])).OrderBy(x => x).ElementAt(peers.Count / 2);
            return new()
            {
                ["label"] = label, ["players"] = peers.Count,
                ["levelPct"] = Pct("level", L(me["level"])), ["chipPct"] = Pct("chip", L(me["chip"])), ["powerPct"] = Pct("dpower", L(me["power"])),
                ["levelMedian"] = Med("level"), ["chipMedian"] = Med("chip"), ["powerMedian"] = Med("dpower"),
            };
        }
        var compare = new List<Dictionary<string, object?>>
        {
            Compare("Everyone with a drone read", all),
        };
        if (myServer is not null) compare.Add(Compare($"Server #{myServer}", all.Where(a => Equals(a["server"], myServer)).ToList()));
        if (myPower > 0) compare.Add(Compare("Similar power (±10%)", all.Where(a => L(a["power"]) > myPower * 0.9 && L(a["power"]) < myPower * 1.1).ToList()));
        return new()
        {
            ["uid"] = uid, ["history"] = history, ["latest"] = me, ["compare"] = compare,
            ["share"] = myPower > 0 ? L(me["power"]) * 100.0 / myPower : null,
            ["coverage"] = "drone reads cover the top 100 of each tracked server (intel.players)",
        };
    }
}
