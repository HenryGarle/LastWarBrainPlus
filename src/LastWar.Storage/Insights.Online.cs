using System.Globalization;
using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// "Online" = whole UTC hours with a sign a player was in the game: a profile last-seen that is a
/// real login (not a mass touch or a first look, PlayerActivity.NotALogin), a donation, or a chat
/// message - the evidence the alliance Activity tab uses. Active hours = how many distinct hours
/// showed a sign. A floor: we only see the moments a reading happens to catch.
///
/// Groups (an alliance, a server, one country on a server) are summed in OnlineAcc. The page
/// groups countries into regions/zones itself (web explorer/Online.jsx), so the mapping lives in
/// one place. "Country" is the profile's country (where the game places them), not the chosen flag.
/// </summary>
public sealed partial class Insights
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), (DateTime At, OnlineAll Value)> OnlineCache = new();

    static int HourOf(DateTime utc) => (int)((utc - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerHour);

    /// <summary>uid -> the UTC hours (since 1970) with a sign of life since <paramref name="since"/>. uids null = everyone.</summary>
    Dictionary<string, HashSet<int>> OnlineHours(NpgsqlConnection c, DateTimeOffset since, IEnumerable<string>? uids, DateTimeOffset? until = null)
    {
        string U(string col) => until is null ? "" : $" AND {col} < @u";
        var outp = new Dictionary<string, HashSet<int>>();
        string f = "", fc = " AND sender_uid ~ '^[0-9]'";
        if (uids is not null)
        {
            var ids = uids.Where(u => Digits.IsMatch(u)).Distinct().ToList();   // digits only: safe to inline
            if (ids.Count == 0) return outp;
            var inList = string.Join(",", ids.Select(u => $"'{u}'"));
            (f, fc) = ($" AND uid IN ({inList})", $" AND sender_uid IN ({inList})");
        }
        using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 180;
        cmd.CommandText = $"""
            SELECT uid, substr(seen_at, 1, 13) FROM player_seen WHERE seen_at >= @s{U("seen_at")}{f} AND NOT {PlayerActivity.NotALogin("seen_at")}
            UNION SELECT uid, substr(donated_at, 1, 13) FROM alliance_member_sample WHERE donated_at >= @s{U("donated_at")}{f}
            UNION SELECT sender_uid, substr(sent_at, 1, 13) FROM chat_message WHERE sent_at >= @s{U("sent_at")}{fc};
            """;
        cmd.Parameters.AddWithValue("@s", since.ToString("O"));
        if (until is not null) cmd.Parameters.AddWithValue("@u", until.Value.ToString("O"));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateTime.TryParseExact(r.GetString(1), "yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            {
                var uid = r.GetString(0);
                if (!outp.TryGetValue(uid, out var set)) outp[uid] = set = [];
                set.Add(HourOf(t));
            }
        return outp;
    }

    /// <summary>uid -> newest non-empty profile country. uids null = everyone.</summary>
    Dictionary<string, string> Countries(NpgsqlConnection c, IEnumerable<string>? uids)
    {
        var f = "";
        if (uids is not null)
        {
            var ids = uids.Where(u => Digits.IsMatch(u)).Distinct().ToList();
            if (ids.Count == 0) return [];
            f = $" AND uid IN ({string.Join(",", ids.Select(u => $"'{u}'"))})";
        }
        using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 180;
        cmd.CommandText = $"SELECT DISTINCT ON (uid) uid, country FROM player_profile WHERE country <> ''{f} ORDER BY uid, taken_at DESC;";
        var outp = new Dictionary<string, string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) outp[r.GetString(0)] = r.GetString(1).ToUpperInvariant();
        return outp;
    }

    /// <summary>The window every group is counted over: the last <paramref name="days"/> days, but no earlier than our first profile reading.</summary>
    public sealed record OnlineWindow(int NowHour, int FirstDay, int Days)
    {
        public static OnlineWindow Of(DateTimeOffset now, int days, DateTimeOffset firstReading)
        {
            var today = HourOf(now.UtcDateTime) / 24;
            var first = Math.Max(today - days + 1, HourOf(firstReading.UtcDateTime) / 24);
            return new(HourOf(now.UtcDateTime), first, today - first + 1);
        }
    }

    OnlineWindow Window(NpgsqlConnection c, int days) =>
        OnlineWindow.Of(DateTimeOffset.UtcNow, days, T(Q(c, "SELECT MIN(taken_at) AS t FROM player_profile;")[0]["t"]) ?? DateTimeOffset.UtcNow);

    /// <summary>Totals for a group of players. Typical online at hour h = HourDays[h] / observed days.</summary>
    public sealed class OnlineAcc(OnlineWindow w)
    {
        public int Players, Active24, Active7, Active30;
        public long Hours24, Hours7, Hours30;
        public readonly int[] HourDays = new int[24];   // sum over players of the days with a sign in that UTC hour
        public readonly int[] PerDay = new int[w.Days];  // players with a sign that day, oldest first

        public void Add(HashSet<int>? hours)
        {
            Players++;
            if (hours is null) return;
            int h24 = 0, h7 = 0, h30 = 0;
            var days = new HashSet<int>();
            foreach (var h in hours)
            {
                var ago = w.NowHour - h;
                if (ago < 24) h24++;
                if (ago < 168) h7++;
                if (ago < 720) h30++;
                if (h / 24 >= w.FirstDay && h / 24 < w.FirstDay + w.Days) { HourDays[h % 24]++; days.Add(h / 24 - w.FirstDay); }
            }
            foreach (var d in days) PerDay[d]++;
            (Hours24, Hours7, Hours30) = (Hours24 + h24, Hours7 + h7, Hours30 + h30);
            if (h24 > 0) Active24++;
            if (h7 > 0) Active7++;
            if (h30 > 0) Active30++;
        }

        public Dictionary<string, object?> Json() => new()
        {
            ["players"] = Players, ["active24"] = Active24, ["active7"] = Active7, ["active30"] = Active30,
            ["hours24"] = Hours24, ["hours7"] = Hours7, ["hours30"] = Hours30,
            ["hourDays"] = HourDays, ["perDay"] = PerDay,
        };
    }

    /// <summary>One player's active hours: 24 h / 7 d / 30 d, and the days with any sign.</summary>
    public static Dictionary<string, object?> OnlineOf(HashSet<int>? hours, int nowHour)
    {
        hours ??= [];
        int In(int within) => hours.Count(h => nowHour - h < within);
        int Days(int within) => hours.Where(h => nowHour - h < within).Select(h => h / 24).Distinct().Count();
        return new()
        {
            ["hours24"] = In(24), ["hours7"] = In(168), ["hours30"] = In(720),
            ["days7"] = Days(168), ["days30"] = Days(720),
            ["last24"] = hours.Where(h => nowHour - h < 24).Select(h => h % 24).Distinct().Order().ToList(),
        };
    }

    /// <summary>A group's totals and its per-country split, as the page draws them.</summary>
    Dictionary<string, object?> OnlineGroup(OnlineWindow w, IEnumerable<string> uids, Dictionary<string, HashSet<int>> hours, Dictionary<string, string> country)
    {
        var total = new OnlineAcc(w);
        var by = new Dictionary<string, OnlineAcc>();
        foreach (var u in uids.Distinct())
        {
            var h = hours.GetValueOrDefault(u);
            total.Add(h);
            var k = country.GetValueOrDefault(u) ?? "";
            if (!by.TryGetValue(k, out var acc)) by[k] = acc = new OnlineAcc(w);
            acc.Add(h);
        }
        return new()
        {
            ["days"] = w.Days, ["firstDay"] = DateTime.UnixEpoch.AddDays(w.FirstDay).ToString("yyyy-MM-dd"),
            ["total"] = total.Json(), ["countries"] = by.ToDictionary(x => x.Key, x => x.Value.Json()),
        };
    }

    public sealed record OnlineAll(OnlineWindow Window, Dictionary<int, List<string>> Servers, Dictionary<string, HashSet<int>> Hours, Dictionary<string, string> Country);

    /// <summary>Everyone's online hours and country, grouped by the server their base is on (player_latest). Cached 15 min.</summary>
    OnlineAll AllOnline(int days)
    {
        days = days <= 7 ? 7 : days <= 14 ? 14 : 30;
        if (OnlineCache.TryGetValue((path, days), out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(15)) return hit.Value;
        using var c = Open();
        var w = Window(c, days);
        // Servers we map only: player_latest also holds chat senders and visitors from anywhere.
        var servers = Q(c, "SELECT uid, server FROM player_latest WHERE server IN (SELECT DISTINCT server_id FROM map_scan);")
            .GroupBy(r => Convert.ToInt32(r["server"])).ToDictionary(g => g.Key, g => g.Select(r => (string)r["uid"]!).ToList());
        var value = new OnlineAll(w, servers, OnlineHours(c, DateTimeOffset.UtcNow.AddDays(-30), null), Countries(c, null));
        OnlineCache[(path, days)] = (DateTime.UtcNow, value);
        return value;
    }

    /// <summary>One server: totals and the per-country split (the page groups into regions).</summary>
    public Dictionary<string, object?> ServerOnline(int server, int days = 30)
    {
        var a = AllOnline(days);
        var g = OnlineGroup(a.Window, a.Servers.GetValueOrDefault(server) ?? [], a.Hours, a.Country);
        g["server"] = server;
        return g;
    }

    /// <summary>Every server side by side: totals, plus players / active 7 d / hours 7 d per country.</summary>
    public Dictionary<string, object?> OnlineComparison(int days = 7)
    {
        var a = AllOnline(days);
        var rows = a.Servers.OrderBy(s => s.Key).Select(s =>
        {
            var g = OnlineGroup(a.Window, s.Value, a.Hours, a.Country);
            var countries = ((Dictionary<string, Dictionary<string, object?>>)g["countries"]!)
                .ToDictionary(x => x.Key, x => new[] { Convert.ToInt64(x.Value["players"]), Convert.ToInt64(x.Value["active7"]), Convert.ToInt64(x.Value["hours7"]) });
            return new Dictionary<string, object?> { ["server"] = s.Key, ["total"] = g["total"], ["countries"] = countries };
        }).ToList();
        return new() { ["days"] = a.Window.Days, ["firstDay"] = DateTime.UnixEpoch.AddDays(a.Window.FirstDay).ToString("yyyy-MM-dd"), ["servers"] = rows };
    }
}
