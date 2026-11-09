using System.Text.Json;

namespace LastWar.Storage;

/// <summary>
/// Every server the game knows, not just the ones we scan in detail (Henry, 2026-09-30: "all-knowing",
/// with control over how much each server is tracked).
///
///   server_info         one row per server: name, type, open time, king, current badge (latest reading)
///   server_event        change log per server: king / badge / camp / season changes, with when
///   server_zone_sample  daily season-zone reading for "basic" servers (force rank, top alliances, camp)
///   server_season       a server's season timeline as its own login reports it (needs an account there)
///   server_track        how much each server is tracked: off (registry only) | basic | full, and
///                       whether the viewer pages show it by default
///   game_zone_war / game_event_group / game_season   the game's own tables (data/assets/tables):
///                       server-vs-server wars, cross-server event groups, season configs
///
/// Groups are read off game_event_group: its match ranges ("1893-1956|1957-2020") are the blocks the
/// game itself groups servers into, so nothing here is designed by hand.
/// Scheduled jobs name servers by level: "@full", "@basic" (basic or full), "@visible" (Resolve).
/// </summary>
public sealed class ServerRegistry(string path)
{
    public static readonly string[] Levels = ["off", "basic", "full"];
    /// <summary>Our own server: where the site's alliances live.</summary>
    public const int Home = 1967;

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "ServerRegistry")) return;
        Db.Exec(path, """
            CREATE TABLE IF NOT EXISTS server_info (
                server_id BIGINT PRIMARY KEY, name TEXT NOT NULL DEFAULT '', server_type BIGINT NOT NULL DEFAULT 0,
                open_time TEXT NOT NULL DEFAULT '', first_seen TEXT NOT NULL, last_seen TEXT NOT NULL,
                king_uid TEXT NOT NULL DEFAULT '', king_name TEXT NOT NULL DEFAULT '', king_tag TEXT NOT NULL DEFAULT '',
                king_alliance TEXT NOT NULL DEFAULT '', king_power BIGINT NOT NULL DEFAULT 0, king_since TEXT NOT NULL DEFAULT '',
                badge_cfg TEXT NOT NULL DEFAULT '', badge_at TEXT NOT NULL DEFAULT '', checked_at TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS server_event (
                server_id BIGINT NOT NULL, at TEXT NOT NULL, kind TEXT NOT NULL, value TEXT NOT NULL, detail TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (server_id, kind, at));
            CREATE TABLE IF NOT EXISTS server_zone_sample (
                taken_at TEXT NOT NULL, server_id BIGINT NOT NULL, force_rank BIGINT, zone_count BIGINT, camp_id BIGINT,
                cities BIGINT, destroyed BIGINT, top TEXT NOT NULL DEFAULT '', PRIMARY KEY (server_id, taken_at));
            CREATE TABLE IF NOT EXISTS server_season (
                server_id BIGINT NOT NULL, season_id BIGINT NOT NULL, cfg_id BIGINT NOT NULL, start_time TEXT NOT NULL, end_time TEXT NOT NULL,
                season_group BIGINT, seen_at TEXT NOT NULL, PRIMARY KEY (server_id, season_id));
            CREATE TABLE IF NOT EXISTS server_track (
                server_id BIGINT PRIMARY KEY, level TEXT NOT NULL DEFAULT 'off', visible BOOLEAN NOT NULL DEFAULT FALSE, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS game_zone_war (
                id BIGINT PRIMARY KEY, type BIGINT, open_time TEXT, end_time TEXT, season_tag TEXT, server_ids TEXT, medal_reward TEXT, raw TEXT);
            CREATE TABLE IF NOT EXISTS game_event_group (
                id BIGINT PRIMARY KEY, season BIGINT, open_time TEXT, close_time TEXT, group_list TEXT, match_type BIGINT, setting_group_id BIGINT, raw TEXT);
            CREATE TABLE IF NOT EXISTS game_season (
                id BIGINT PRIMARY KEY, season BIGINT, name TEXT, pre_start TEXT, start_time TEXT, settlement TEXT, end_time TEXT,
                zone_war_season_group TEXT, raw TEXT);
            -- The season config each server is on, read off the game's refusal to start a character there (servers.season).
            CREATE TABLE IF NOT EXISTS server_season_cfg (
                server_id BIGINT NOT NULL, cfg_id BIGINT NOT NULL, first_seen TEXT NOT NULL, last_seen TEXT NOT NULL, PRIMARY KEY (server_id, cfg_id));
            -- Season groups entered by hand (Henry, from the in-game display) before the game reports them.
            CREATE TABLE IF NOT EXISTS server_season_hand (
                cfg_id BIGINT NOT NULL, server_id BIGINT NOT NULL, entered_at TEXT NOT NULL, PRIMARY KEY (cfg_id, server_id));
            ALTER TABLE game_season ADD COLUMN IF NOT EXISTS seeds TEXT NOT NULL DEFAULT '';
            CREATE TABLE IF NOT EXISTS event_server (
                event TEXT NOT NULL, server_id BIGINT NOT NULL, added_at TEXT NOT NULL, PRIMARY KEY (event, server_id));
            """);
    }

    static string Now() => DateTimeOffset.UtcNow.ToString("O");

    /// <summary>Notes that a server is on season config <paramref name="cfg"/> (lw_season id) now.</summary>
    public void NoteSeasonCfg(int server, long cfg) =>
        Db.Exec(path, """
            INSERT INTO server_season_cfg (server_id, cfg_id, first_seen, last_seen) VALUES (@s, @c, @a, @a)
            ON CONFLICT (server_id, cfg_id) DO UPDATE SET last_seen = @a;
            """, ("@s", (long)server), ("@c", cfg), ("@a", Now()));

    /// <summary>Replaces the hand-entered server list for season config <paramref name="cfg"/> (empty list = remove it).</summary>
    public void SetHandSeasonGroup(long cfg, IReadOnlyCollection<int> servers)
    {
        EnsureSchema();
        using var c = Db.Open(path);
        using var tx = c.BeginTransaction();
        using (var del = Db.Command(c, "DELETE FROM server_season_hand WHERE cfg_id = @c;", ("@c", cfg))) { del.Transaction = tx; del.ExecuteNonQuery(); }
        var at = Now();
        foreach (var s in servers.Distinct())
        {
            using var ins = Db.Command(c, "INSERT INTO server_season_hand (cfg_id, server_id, entered_at) VALUES (@c, @s, @a);", ("@c", cfg), ("@s", (long)s), ("@a", at));
            ins.Transaction = tx;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ---- game tables

    /// <summary>Loads lw_zone_war, yuntie_battle_server_group_new and lw_season from the extracted tables. Upserts; returns row counts.</summary>
    public (int Wars, int Groups, int Seasons) IngestTables(string tablesDir)
    {
        EnsureSchema();
        JsonElement[] Read(string file) =>
            File.Exists(Path.Combine(tablesDir, file)) ? JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(Path.Combine(tablesDir, file))) ?? [] : [];
        static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText()) : "";
        static long? L(JsonElement e, string k) => long.TryParse(S(e, k), out var n) ? n : null;
        using var c = Db.Open(path);
        using var tx = c.BeginTransaction();
        int Up(string sql, IEnumerable<JsonElement> rows, Func<JsonElement, (string, object?)[]> ps)
        {
            var n = 0;
            foreach (var r in rows) { using var cmd = Db.Command(c, sql, ps(r)); cmd.Transaction = tx; n += cmd.ExecuteNonQuery(); }
            return n;
        }
        var wars = Up("""
            INSERT INTO game_zone_war (id, type, open_time, end_time, season_tag, server_ids, medal_reward, raw)
            VALUES (@id, @t, @o, @e, @tag, @s, @m, @raw)
            ON CONFLICT (id) DO UPDATE SET type = @t, open_time = @o, end_time = @e, season_tag = @tag, server_ids = @s, medal_reward = @m, raw = @raw;
            """, Read("lw_zone_war.json").Where(r => S(r, "server_ids").Length > 0), r =>
            [("@id", L(r, "id")), ("@t", L(r, "type")), ("@o", S(r, "open_time")), ("@e", S(r, "end_time")), ("@tag", S(r, "season_tag_for_alert")),
             ("@s", S(r, "server_ids")), ("@m", S(r, "medal_reward")), ("@raw", r.GetRawText())]);
        var groups = Up("""
            INSERT INTO game_event_group (id, season, open_time, close_time, group_list, match_type, setting_group_id, raw)
            VALUES (@id, @s, @o, @e, @g, @m, @set, @raw)
            ON CONFLICT (id) DO UPDATE SET season = @s, open_time = @o, close_time = @e, group_list = @g, match_type = @m, setting_group_id = @set, raw = @raw;
            """, Read("yuntie_battle_server_group_new.json"), r =>
            [("@id", L(r, "id")), ("@s", L(r, "season")), ("@o", S(r, "open_time")), ("@e", S(r, "close_time")), ("@g", S(r, "group_list")),
             ("@m", L(r, "match_type")), ("@set", L(r, "setting_group_id")), ("@raw", r.GetRawText())]);
        // ponytail: lw_season rows are huge; the dates and the two seeded servers are what we use, so raw stays empty.
        var seasons = Up("""
            INSERT INTO game_season (id, season, name, pre_start, start_time, settlement, end_time, zone_war_season_group, raw, seeds)
            VALUES (@id, @s, @n, @p, @st, @se, @e, @z, '', @seeds)
            ON CONFLICT (id) DO UPDATE SET season = @s, name = @n, pre_start = @p, start_time = @st, settlement = @se, end_time = @e, zone_war_season_group = @z, seeds = @seeds;
            """, Read("lw_season.json"), r =>
            [("@id", L(r, "id")), ("@s", L(r, "season")), ("@n", S(r, "name")), ("@p", S(r, "pre_start_time")), ("@st", S(r, "start_time")),
             ("@se", S(r, "settlement_time")), ("@e", S(r, "end_time")), ("@z", S(r, "zone_war_season_group")), ("@seeds", Seeds(S(r, "camp_sever_local")))]);
        tx.Commit();
        return (wars, groups, seasons);
    }

    /// <summary>
    /// lw_season.camp_sever_local "lw:&lt;9 hex bytes&gt;" -> "1974,1958": two servers of the season group (the seeds),
    /// each byte XOR a fixed per-position key (docs/season-groups.md). Anything else (other code formats) -> "".
    /// </summary>
    public static string Seeds(string code)
    {
        ReadOnlySpan<byte> key = [0x0e, 0x2a, 0x0d, 0x28, 0x38, 0x03, 0x23, 0x02, 0x0e];
        if (!code.StartsWith("lw:") || code.Length != 3 + 18) return "";
        Span<char> s = stackalloc char[9];
        for (var i = 0; i < 9; i++)
        {
            if (!byte.TryParse(code.AsSpan(3 + i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return "";
            s[i] = (char)(b ^ key[i]);
        }
        var t = new string(s);
        return System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{4}\|\d{4}$") ? t.Replace('|', ',') : "";
    }

    /// <summary>"1957;1958|1959-1962" -> sides of server ids.</summary>
    public static List<List<int>> Sides(string spec) =>
        [.. spec.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(side => side.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(p => p.Split('-') is [var a, var b] && int.TryParse(a, out var lo) && int.TryParse(b, out var hi) ? Enumerable.Range(lo, hi - lo + 1)
                : int.TryParse(p, out var one) ? [one] : Array.Empty<int>()).ToList())];

    public sealed record Block(string Id, int Lo, int Hi, List<string> PairedWith, string FirstSeen, string LastSeen, List<long> Seasons);

    /// <summary>
    /// The game's own server blocks: every "lo-hi" range in the cross-server event groups, newest
    /// first, with which blocks each was matched against. A server's group = the newest block holding it.
    /// </summary>
    public List<Block> Blocks()
    {
        EnsureSchema();
        using var c = Db.Open(path);
        var map = new Dictionary<string, Block>();
        foreach (var r in Db.Rows(c, "SELECT season, open_time, group_list FROM game_event_group WHERE group_list <> '' ORDER BY open_time;"))
        {
            var at = DateOf((string)r["open_time"]!);
            var ranges = ((string)r["group_list"]!).Split('|').Select(x => x.Trim()).Where(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"^\d+-\d+$")).ToList();
            foreach (var g in ranges)
            {
                var (lo, hi) = (int.Parse(g.Split('-')[0]), int.Parse(g.Split('-')[1]));
                if (!map.TryGetValue(g, out var b)) b = new Block(g, lo, hi, [], at, at, []);
                b = b with { LastSeen = string.CompareOrdinal(at, b.LastSeen) > 0 ? at : b.LastSeen };
                foreach (var other in ranges.Where(o => o != g && !b.PairedWith.Contains(o))) b.PairedWith.Add(other);
                if (r["season"] is long s && !b.Seasons.Contains(s)) b.Seasons.Add(s);
                map[g] = b;
            }
        }
        return [.. map.Values.OrderByDescending(b => b.LastSeen, StringComparer.Ordinal).ThenBy(b => b.Lo)];
    }

    public sealed record WarGroup(string Id, int Lo, int Hi, List<int> Servers, string FirstWar, string LastWar);

    /// <summary>
    /// The groups servers actually fight in: servers joined by any 4 v 4 war (game_zone_war type 8),
    /// transitively. For 1957-2020 this gives 1957-1988 and 1989-2020 - the two 32s that ran seasons
    /// and transfers apart (Henry, 2026-09-30), inside the 64-server event block. A group grows by
    /// itself once the game starts matching across it.
    /// </summary>
    public List<WarGroup> WarGroups()
    {
        EnsureSchema();
        using var c = Db.Open(path);
        var parent = new Dictionary<int, int>();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var first = new Dictionary<int, string>();
        var last = new Dictionary<int, string>();
        foreach (var r in Db.Rows(c, "SELECT server_ids, open_time FROM game_zone_war WHERE type = 8 ORDER BY open_time;"))
        {
            var ids = Sides((string)r["server_ids"]!).SelectMany(s => s).Where(s => s < 9000).Distinct().ToList();   // 9000+ = cross-server hosts
            var at = DateOf((string?)r["open_time"] ?? "");
            foreach (var i in ids) { parent.TryAdd(i, i); first.TryAdd(i, at); last[i] = at; }
            for (var k = 1; k < ids.Count; k++) { var (a, b) = (Find(ids[0]), Find(ids[k])); if (a != b) parent[a] = b; }
        }
        return [.. parent.Keys.GroupBy(Find).Select(g =>
        {
            var s = g.Order().ToList();
            return new WarGroup($"{s[0]}-{s[^1]}", s[0], s[^1], s, s.Select(x => first[x]).Min(StringComparer.Ordinal) ?? "", s.Select(x => last[x]).Max(StringComparer.Ordinal) ?? "");
        }).OrderBy(g => g.Lo)];
    }

    /// <summary>"2026/1/19" or "2026-01-19 00:00:00" -> "2026-01-19".</summary>
    public static string DateOf(string s) =>
        DateTime.TryParse(s.Replace('/', '-'), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd") : s;

    /// <summary>The newest block holding the server (null if the game never grouped it in these tables).</summary>
    public static Block? BlockOf(IEnumerable<Block> blocks, int server) =>
        blocks.Where(b => b.Lo <= server && server <= b.Hi).OrderByDescending(b => b.LastSeen, StringComparer.Ordinal).ThenBy(b => b.Hi - b.Lo).FirstOrDefault();

    // ---- tracking

    /// <summary>Once: 1957-1988 (what the jobs covered) full + shown; the rest of their block basic + shown; everything else registry only.</summary>
    public void SeedTracking()
    {
        EnsureSchema();
        using var c = Db.Open(path);
        if (Convert.ToInt64(Db.Rows(c, "SELECT COUNT(*) AS n FROM server_track;")[0]["n"]) > 0) return;
        var block = BlockOf(Blocks(), Home);
        var now = Now();
        foreach (var s in Enumerable.Range(1957, 32)) SetTrack(c, s, "full", true, now);
        if (block is not null)
            foreach (var s in Enumerable.Range(block.Lo, block.Hi - block.Lo + 1).Where(s => s is < 1957 or > 1988)) SetTrack(c, s, "basic", true, now);
    }

    static void SetTrack(Npgsql.NpgsqlConnection c, int server, string? level, bool? visible, string now)
    {
        using var cmd = Db.Command(c, """
            INSERT INTO server_track (server_id, level, visible, updated_at) VALUES (@s, COALESCE(@l, 'off'), COALESCE(@v, FALSE), @t)
            ON CONFLICT (server_id) DO UPDATE SET level = COALESCE(@l, server_track.level), visible = COALESCE(@v, server_track.visible), updated_at = @t;
            """, ("@s", (long)server), ("@l", (object?)level ?? DBNull.Value), ("@v", (object?)visible ?? DBNull.Value), ("@t", now));
        cmd.Parameters["@l"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
        cmd.Parameters["@v"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Boolean;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Bulk: set level and/or visibility for many servers at once (null = leave as is).</summary>
    public int SetTracking(IEnumerable<int> servers, string? level, bool? visible)
    {
        if (level is not null && !Levels.Contains(level)) throw new ArgumentException($"level must be one of {string.Join(", ", Levels)}");
        EnsureSchema();
        using var c = Db.Open(path);
        var now = Now();
        var n = 0;
        foreach (var s in servers.Distinct()) { SetTrack(c, s, level, visible, now); n++; }
        return n;
    }

    // ---- events: the servers an event page looks at (Meteorite: the ~4 warzones in this week's crater).
    // The game matches them week by week, so the list is set by hand (admin) and kept until changed.

    public List<int> EventServers(string evt)
    {
        EnsureSchema();
        using var c = Db.Open(path);
        return Db.Rows(c, "SELECT server_id FROM event_server WHERE event = @e ORDER BY server_id;", ("@e", evt))
            .Select(r => Convert.ToInt32(r["server_id"])).ToList();
    }

    public void SetEventServers(string evt, IEnumerable<int> servers)
    {
        EnsureSchema();
        using var c = Db.Open(path);
        using var tx = c.BeginTransaction();
        using (var del = Db.Command(c, "DELETE FROM event_server WHERE event = @e;", ("@e", evt))) { del.Transaction = tx; del.ExecuteNonQuery(); }
        foreach (var s in servers.Distinct())
        {
            using var cmd = Db.Command(c, "INSERT INTO event_server (event, server_id, added_at) VALUES (@e, @s, @at);", ("@e", evt), ("@s", (long)s), ("@at", Now()));
            cmd.Transaction = tx; cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public Dictionary<int, (string Level, bool Visible)> Tracking()
    {
        EnsureSchema();
        using var c = Db.Open(path);
        return Db.Rows(c, "SELECT server_id, level, visible FROM server_track;")
            .ToDictionary(r => Convert.ToInt32(r["server_id"]), r => ((string)r["level"]!, r["visible"] is true));
    }

    /// <summary>"@full" -> full servers; "@basic" -> basic or full; "@visible" -> shown ones. Anything else is returned as is.</summary>
    public string Resolve(string spec)
    {
        if (!spec.StartsWith('@')) return spec;
        var t = Tracking();
        var ids = spec switch
        {
            "@full" => t.Where(x => x.Value.Level == "full"),
            "@basic" => t.Where(x => x.Value.Level is "basic" or "full"),
            "@visible" => t.Where(x => x.Value.Visible),
            _ => throw new ArgumentException($"unknown server set {spec} (use @full, @basic or @visible)"),
        };
        var list = ids.Select(x => x.Key).Order().ToList();
        return list.Count == 0 ? throw new ArgumentException($"no servers are set to {spec[1..]} (Tracking page)") : Compact(list);
    }

    /// <summary>[1957,1958,1959,1970] -> "1957-1959,1970".</summary>
    public static string Compact(IReadOnlyList<int> sorted)
    {
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j == i ? $"{sorted[i]}" : $"{sorted[i]}-{sorted[j]}");
            i = j + 1;
        }
        return string.Join(",", parts);
    }

    // ---- readings

    /// <summary>Upserts the server list (cross.server.ls). Returns how many servers are new to us.</summary>
    public int UpsertList(IEnumerable<(int Id, string Name, int Type)> servers)
    {
        EnsureSchema();
        using var c = Db.Open(path);
        using var tx = c.BeginTransaction();
        var now = Now();
        var added = 0;
        foreach (var (id, name, type) in servers)
        {
            using var cmd = Db.Command(c, """
                INSERT INTO server_info (server_id, name, server_type, first_seen, last_seen) VALUES (@s, @n, @t, @now, @now)
                ON CONFLICT (server_id) DO UPDATE SET name = @n, server_type = @t, last_seen = @now
                RETURNING (xmax = 0) AS inserted;
                """, ("@s", (long)id), ("@n", name), ("@t", (long)type), ("@now", now));
            cmd.Transaction = tx;
            if (cmd.ExecuteScalar() is true) added++;
        }
        tx.Commit();
        return added;
    }

    public void SetOpenTime(int server, long openTimeMs) =>
        Db.Exec(path, "UPDATE server_info SET open_time = @o WHERE server_id = @s;",
            ("@s", (long)server), ("@o", DateTimeOffset.FromUnixTimeMilliseconds(openTimeMs).ToString("O")));

    /// <summary>Normal servers we have no open time for yet, newest ids first (open times never change).</summary>
    public List<int> MissingOpenTime(int limit)
    {
        EnsureSchema();
        using var c = Db.Open(path);
        return [.. Db.Rows(c, "SELECT server_id FROM server_info WHERE open_time = '' AND server_type = 0 ORDER BY server_id DESC LIMIT @n;", ("@n", limit))
            .Select(r => Convert.ToInt32(r["server_id"]))];
    }

    public List<int> AllServers(int type = 0)
    {
        EnsureSchema();
        using var c = Db.Open(path);
        return [.. Db.Rows(c, "SELECT server_id FROM server_info WHERE server_type = @t ORDER BY server_id;", ("@t", (long)type)).Select(r => Convert.ToInt32(r["server_id"]))];
    }

    public sealed record KingReading(int Server, string Uid, string Name, string Tag, string Alliance, long Power, string Since, string BadgeCfg, string BadgeAt);

    /// <summary>Stores king + badge readings; a change from the stored value is logged in server_event. Returns changes logged.</summary>
    public int UpsertKings(IEnumerable<KingReading> rows)
    {
        EnsureSchema();
        using var c = Db.Open(path);
        using var tx = c.BeginTransaction();
        var now = Now();
        var changes = 0;
        foreach (var k in rows)
        {
            var old = Db.Rows(c, "SELECT king_uid, king_since, badge_cfg, badge_at FROM server_info WHERE server_id = @s;", ("@s", (long)k.Server)).FirstOrDefault();
            void Log(string kind, string value, string detail, string at)
            {
                using var e = Db.Command(c, "INSERT INTO server_event (server_id, at, kind, value, detail) VALUES (@s, @a, @k, @v, @d) ON CONFLICT DO NOTHING;",
                    ("@s", (long)k.Server), ("@a", at), ("@k", kind), ("@v", value), ("@d", detail));
                e.Transaction = tx;
                changes += e.ExecuteNonQuery();
            }
            if (k.Uid.Length > 0 && (string?)old?["king_uid"] != k.Uid)
                Log("king", k.Uid, $"{(k.Tag.Length > 0 ? $"[{k.Tag}]" : "")}{k.Name}", KingChangeAt(k.Since, (string?)old?["king_since"], now));
            if (k.BadgeCfg.Length > 0 && ((string?)old?["badge_cfg"] != k.BadgeCfg || (string?)old?["badge_at"] != k.BadgeAt))
                Log("badge", k.BadgeCfg, "", k.BadgeAt.Length > 0 ? k.BadgeAt : now);
            using var up = Db.Command(c, """
                INSERT INTO server_info (server_id, first_seen, last_seen, king_uid, king_name, king_tag, king_alliance, king_power, king_since, badge_cfg, badge_at, checked_at)
                VALUES (@s, @now, @now, @u, @n, @t, @a, @p, @since, @b, @bat, @now)
                ON CONFLICT (server_id) DO UPDATE SET king_uid = @u, king_name = @n, king_tag = @t, king_alliance = @a, king_power = @p,
                    king_since = @since, badge_cfg = @b, badge_at = @bat, checked_at = @now;
                """, ("@s", (long)k.Server), ("@now", now), ("@u", k.Uid), ("@n", k.Name), ("@t", k.Tag), ("@a", k.Alliance), ("@p", k.Power),
                ("@since", k.Since), ("@b", k.BadgeCfg), ("@bat", k.BadgeAt));
            up.Transaction = tx;
            up.ExecuteNonQuery();
        }
        tx.Commit();
        return changes;
    }

    /// <summary>
    /// When a president change happened. A new term has its own start (since); a handover INSIDE a term
    /// (a temporary president, 2026-09-30) keeps the term's start, so it is dated when we saw it - else the
    /// change and the change back share one date and the second is lost.
    /// </summary>
    public static string KingChangeAt(string since, string? oldSince, string now) =>
        since.Length > 0 && since != oldSince ? since : now;

    /// <summary>
    /// A president reading from any other scan (the server card of refresh.stats / intel): newest reading
    /// wins, so a temporary president the daily registry happened to see does not stick for a day.
    /// Only the king fields change; a change is logged like UpsertKings does.
    /// </summary>
    public void SeenKing(int server, string uid, string name, string tag, long power, string since)
    {
        if (uid.Length == 0) return;
        EnsureSchema();
        using var c = Db.Open(path);
        var now = Now();
        var old = Db.Rows(c, "SELECT king_uid, king_since FROM server_info WHERE server_id = @s;", ("@s", (long)server)).FirstOrDefault();
        if ((string?)old?["king_uid"] == uid) return;
        using (var e = Db.Command(c, "INSERT INTO server_event (server_id, at, kind, value, detail) VALUES (@s, @a, 'king', @v, @d) ON CONFLICT DO NOTHING;",
            ("@s", (long)server), ("@a", KingChangeAt(since, (string?)old?["king_since"], now)), ("@v", uid), ("@d", $"{(tag.Length > 0 ? $"[{tag}]" : "")}{name}")))
            e.ExecuteNonQuery();
        using var up = Db.Command(c, """
            INSERT INTO server_info (server_id, first_seen, last_seen, king_uid, king_name, king_tag, king_power, king_since, checked_at)
            VALUES (@s, @now, @now, @u, @n, @t, @p, @since, @now)
            ON CONFLICT (server_id) DO UPDATE SET king_uid = @u, king_name = @n, king_tag = @t, king_power = @p, king_since = @since, checked_at = @now;
            """, ("@s", (long)server), ("@now", now), ("@u", uid), ("@n", name), ("@t", tag), ("@p", power), ("@since", since));
        up.ExecuteNonQuery();
    }

    public void AddZoneSample(int server, long? forceRank, long? zones, long? camp, long? cities, long? destroyed, string top)
    {
        EnsureSchema();
        var at = Now();
        Db.Exec(path, """
            INSERT INTO server_zone_sample (taken_at, server_id, force_rank, zone_count, camp_id, cities, destroyed, top)
            VALUES (@t, @s, @f, @z, @c, @ci, @d, @top) ON CONFLICT DO NOTHING;
            """, ("@t", at), ("@s", (long)server), ("@f", forceRank), ("@z", zones), ("@c", camp), ("@ci", cities), ("@d", destroyed), ("@top", top));
        // A change of side in a cross-server war is a line in the server's story.
        using var c = Db.Open(path);
        var last = Db.Rows(c, "SELECT value FROM server_event WHERE server_id = @s AND kind = 'camp' ORDER BY at DESC LIMIT 1;", ("@s", (long)server)).FirstOrDefault();
        var now = (camp ?? 0).ToString();
        if ((string?)last?["value"] != now && (last is not null || camp is > 0)) LogEvent(server, "camp", now);
    }

    public int AddSeasons(int server, IEnumerable<(long SeasonId, long CfgId, long StartMs, long EndMs)> seasons, long? seasonGroup)
    {
        EnsureSchema();
        var now = Now();
        var n = 0;
        foreach (var s in seasons)
            n += Db.Exec(path, """
                INSERT INTO server_season (server_id, season_id, cfg_id, start_time, end_time, season_group, seen_at) VALUES (@s, @id, @cfg, @st, @e, @g, @now)
                ON CONFLICT (server_id, season_id) DO UPDATE SET cfg_id = @cfg, start_time = @st, end_time = @e, season_group = COALESCE(@g, server_season.season_group), seen_at = @now;
                """, ("@s", (long)server), ("@id", s.SeasonId), ("@cfg", s.CfgId), ("@st", DateTimeOffset.FromUnixTimeMilliseconds(s.StartMs).ToString("O")),
                ("@e", DateTimeOffset.FromUnixTimeMilliseconds(s.EndMs).ToString("O")), ("@g", seasonGroup), ("@now", now));
        return n;
    }

    public void LogEvent(int server, string kind, string value, string detail = "") =>
        Db.Exec(path, "INSERT INTO server_event (server_id, at, kind, value, detail) VALUES (@s, @a, @k, @v, @d) ON CONFLICT DO NOTHING;",
            ("@s", (long)server), ("@a", Now()), ("@k", kind), ("@v", value), ("@d", detail));
}
