namespace LastWar.Storage;

/// <summary>The Servers pages: every server the game knows, grouped by the game's own blocks (ServerRegistry).</summary>
public sealed partial class Insights
{
    /// <summary>
    /// Every server with its registry row, tracking and block, plus the blocks. "current" = the block
    /// of the first fully tracked server (our own group), which the pages open on.
    /// </summary>
    public Dictionary<string, object?> Servers()
    {
        var reg = new ServerRegistry(path);
        reg.EnsureSchema();
        var blocks = reg.Blocks();
        var wars = reg.WarGroups();
        var warOf = wars.SelectMany(g => g.Servers.Select(s => (s, g.Id))).ToDictionary(x => x.s, x => x.Id);
        var track = reg.Tracking();
        using var c = Open();
        var mapped = Q(c, """
            SELECT server_id AS s, MAX(taken_at) AS at, (ARRAY_AGG(tile_count ORDER BY taken_at DESC))[1] AS players FROM map_scan GROUP BY server_id;
            """).ToDictionary(r => Convert.ToInt32(r["s"]), r => r);
        var seasons = Q(c, "SELECT DISTINCT ON (server_id) server_id AS s, season_id AS season, cfg_id AS cfg, start_time AS start, end_time AS \"end\" FROM server_season ORDER BY server_id, season_id DESC;")
            .ToDictionary(r => Convert.ToInt32(r["s"]), r => r);
        var rows = Q(c, """
            SELECT server_id AS id, name, server_type AS type, NULLIF(open_time, '') AS open, king_uid AS "kingUid", king_name AS "kingName", king_tag AS "kingTag",
                   king_power AS "kingPower", NULLIF(king_since, '') AS "kingSince", badge_cfg AS badge, NULLIF(badge_at, '') AS "badgeAt", first_seen AS "firstSeen"
            FROM server_info ORDER BY server_id;
            """);
        foreach (var r in rows)
        {
            var id = Convert.ToInt32(r["id"]);
            var (level, visible) = track.TryGetValue(id, out var t) ? t : ("off", false);
            r["level"] = level; r["visible"] = visible;
            r["block"] = ServerRegistry.BlockOf(blocks, id)?.Id;
            r["warGroup"] = warOf.GetValueOrDefault(id);
            if (mapped.TryGetValue(id, out var m)) { r["mappedAt"] = m["at"]; r["mapPlayers"] = m["players"]; }
            if (seasons.TryGetValue(id, out var se)) r["season"] = se;
        }
        // Only a server's own players can read its season; the rest of its game group shares it, so
        // those servers show it too, marked "from" the server it was read on.
        var byBlock = rows.Where(r => r.ContainsKey("season") && r["block"] is string).GroupBy(r => (string)r["block"]!).ToDictionary(g => g.Key, g => g.First());
        foreach (var r in rows.Where(r => !r.ContainsKey("season") && r["block"] is string b && byBlock.ContainsKey(b)))
        {
            var src = byBlock[(string)r["block"]!];
            r["season"] = new Dictionary<string, object?>((Dictionary<string, object?>)src["season"]!) { ["from"] = src["id"] };
        }
        var firstFull = track.Where(x => x.Value.Level == "full").Select(x => x.Key).Order().FirstOrDefault();
        return new()
        {
            ["servers"] = rows,
            ["blocks"] = blocks.Select(b => new Dictionary<string, object?>
            {
                ["id"] = b.Id, ["lo"] = b.Lo, ["hi"] = b.Hi, ["size"] = b.Hi - b.Lo + 1, ["pairedWith"] = b.PairedWith,
                ["firstSeen"] = b.FirstSeen, ["lastSeen"] = b.LastSeen, ["seasons"] = b.Seasons,
            }).ToList(),
            // The pages open on the war group (the 32 that fight and transfer together), not the 64-server event block.
            ["current"] = firstFull > 0 ? warOf.GetValueOrDefault(firstFull) ?? ServerRegistry.BlockOf(blocks, firstFull)?.Id : null,
            ["currentBlock"] = firstFull > 0 ? ServerRegistry.BlockOf(blocks, firstFull)?.Id : null,
            ["warGroups"] = wars.Select(g => new Dictionary<string, object?>
            {
                ["id"] = g.Id, ["lo"] = g.Lo, ["hi"] = g.Hi, ["size"] = g.Servers.Count, ["servers"] = g.Servers,
                ["contiguous"] = g.Servers.Count == g.Hi - g.Lo + 1, ["firstWar"] = g.FirstWar, ["lastWar"] = g.LastWar,
            }).ToList(),
        };
    }

    /// <summary>
    /// Season groups for servers lo..hi: the season config each server is on now (servers.season, newest reading per
    /// server), with that config's season and dates from lw_season. Servers on the same config share a season map.
    /// </summary>
    public List<Dictionary<string, object?>> SeasonGroups(int lo, int hi)
    {
        new ServerRegistry(path).EnsureSchema();
        using var c = Open();
        return Q(c, """
            SELECT DISTINCT ON (s.server_id) s.server_id AS server, s.cfg_id AS cfg, s.first_seen AS since, s.last_seen AS seen,
                   g.season, g.pre_start AS "preStart", g.start_time AS start, g.end_time AS "end"
            FROM server_season_cfg s LEFT JOIN game_season g ON g.id = s.cfg_id
            WHERE s.server_id BETWEEN @lo AND @hi ORDER BY s.server_id, s.last_seen DESC, s.first_seen DESC;
            """, ("@lo", (long)lo), ("@hi", (long)hi));
    }

    /// <summary>Server-vs-server wars (game_zone_war) with any server in lo..hi, newest first, sides expanded.</summary>
    public List<Dictionary<string, object?>> ZoneWars(int lo, int hi, int limit = 300)
    {
        new ServerRegistry(path).EnsureSchema();
        using var c = Open();
        return Q(c, "SELECT id, type, open_time AS open, end_time AS \"end\", season_tag AS tag, server_ids AS servers, medal_reward AS medal FROM game_zone_war ORDER BY open_time DESC;")
            .Select(r => { r["sides"] = ServerRegistry.Sides((string)r["servers"]!); return r; })
            .Where(r => ((List<List<int>>)r["sides"]!).Any(side => side.Any(s => s >= lo && s <= hi)))
            .Take(limit).ToList();
    }

    /// <summary>Cross-server event groupings (game_event_group) that include lo..hi, newest first.</summary>
    public List<Dictionary<string, object?>> EventGroups(int lo, int hi)
    {
        new ServerRegistry(path).EnsureSchema();
        using var c = Open();
        return Q(c, "SELECT id, season, open_time AS open, close_time AS close, group_list AS groups, match_type AS match FROM game_event_group ORDER BY open_time DESC;")
            .Where(r => ServerRegistry.Sides(((string)r["groups"]!).Replace(';', '|')).Any(g => g.Any(s => s >= lo && s <= hi))).ToList();
    }

    /// <summary>One server's story: registry row, change log, season zone readings, seasons, wars and groupings.</summary>
    public Dictionary<string, object?> ServerStory(int server)
    {
        var reg = new ServerRegistry(path);
        reg.EnsureSchema();
        using var c = Open();
        var block = ServerRegistry.BlockOf(reg.Blocks(), server);
        var war = reg.WarGroups().FirstOrDefault(g => g.Servers.Contains(server));
        return new()
        {
            ["server"] = server,
            ["info"] = Q(c, "SELECT * FROM server_info WHERE server_id = @s;", ("@s", (long)server)).FirstOrDefault(),
            ["track"] = reg.Tracking().TryGetValue(server, out var t) ? new Dictionary<string, object?> { ["level"] = t.Level, ["visible"] = t.Visible } : null,
            ["block"] = block is null ? null : new Dictionary<string, object?> { ["id"] = block.Id, ["pairedWith"] = block.PairedWith, ["seasons"] = block.Seasons, ["lastSeen"] = block.LastSeen },
            ["warGroup"] = war is null ? null : new Dictionary<string, object?> { ["id"] = war.Id, ["size"] = war.Servers.Count, ["lastWar"] = war.LastWar },
            ["events"] = Q(c, "SELECT at, kind, value, detail FROM server_event WHERE server_id = @s ORDER BY at DESC LIMIT 200;", ("@s", (long)server)),
            ["zones"] = Q(c, "SELECT taken_at AS at, force_rank AS \"forceRank\", zone_count AS zones, camp_id AS camp, cities, destroyed, top FROM server_zone_sample WHERE server_id = @s ORDER BY taken_at DESC LIMIT 60;", ("@s", (long)server)),
            ["seasons"] = Q(c, """
                SELECT s.season_id AS season, s.cfg_id AS cfg, s.start_time AS start, s.end_time AS "end", s.season_group AS "group",
                       g.pre_start AS "preStart", g.settlement, g.name
                FROM server_season s LEFT JOIN game_season g ON g.id = s.cfg_id WHERE s.server_id = @s ORDER BY s.season_id;
                """, ("@s", (long)server)),
            ["wars"] = ZoneWars(server, server, 60),
            ["groups"] = EventGroups(server, server),
        };
    }
}
