using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// The newest thing known about every player and alliance, DERIVED from every source and rebuilt
/// with the other histories. It exists so search is one lookup on a small table instead of a scan
/// of every map tile, and so what search shows is the latest, not what an old map saw.
///
///   player_latest   - per uid: name and alliance from the newest of a map sighting, an
///                     alliance-roster power sample or a profile (ties go to the profile; a
///                     profile's empty alliance means "none"). The server is the one they are
///                     FROM - the newest profile's home - never where a base happens to stand.
///                     Only a player with no profile falls back to the server a map saw them on.
///   alliance_latest - per alliance, from its members' rows: newest tag, the home server most of
///                     them share, how many, when last seen.
/// </summary>
public sealed class PlayerLatest(string path)
{
    NpgsqlConnection Open() => Db.Open(path);

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "PlayerLatest")) return;   // DDL once per process: it takes table locks
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS player_latest (
                uid         TEXT    NOT NULL PRIMARY KEY,
                name        TEXT    NOT NULL DEFAULT '',
                server      BIGINT NOT NULL DEFAULT 0,
                alliance_id TEXT    NOT NULL DEFAULT '',
                tag         TEXT    NOT NULL DEFAULT '',
                seen        TEXT    NOT NULL,
                source      TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_player_latest_alliance ON player_latest(alliance_id);
            -- From the newest profile: the site's default measure is estimated hero power (HeroModel).
            ALTER TABLE player_latest ADD COLUMN IF NOT EXISTS power BIGINT;
            ALTER TABLE player_latest ADD COLUMN IF NOT EXISTS troops BIGINT;
            ALTER TABLE player_latest ADD COLUMN IF NOT EXISTS kills BIGINT;
            ALTER TABLE player_latest ADD COLUMN IF NOT EXISTS hero BIGINT;
            ALTER TABLE player_latest ADD COLUMN IF NOT EXISTS profiled_at TEXT;
            -- 2026-11-02 (the Explore > Players list): HQ, where the account is from, the flag it shows, where its base is now, VIP.
            ALTER TABLE player_latest ADD COLUMN IF NOT EXISTS level BIGINT;
            ALTER TABLE player_latest ADD COLUMN IF NOT EXISTS country TEXT;
            ALTER TABLE player_latest ADD COLUMN IF NOT EXISTS flag TEXT;
            ALTER TABLE player_latest ADD COLUMN IF NOT EXISTS current_server BIGINT;
            ALTER TABLE player_latest ADD COLUMN IF NOT EXISTS svip BIGINT;
            CREATE INDEX IF NOT EXISTS ix_player_latest_server ON player_latest(server);
            CREATE TABLE IF NOT EXISTS alliance_latest (
                alliance_id TEXT    NOT NULL PRIMARY KEY,
                tag         TEXT    NOT NULL DEFAULT '',
                server      BIGINT NOT NULL DEFAULT 0,
                members     BIGINT NOT NULL DEFAULT 0,
                seen        TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public int Rebuild()
    {
        EnsureSchema();
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 300;
        var hasProfiles = Exists(c, tx, "player_profile");
        var profiles = hasProfiles
            ? "UNION ALL SELECT uid, name, home_server, alliance_id, alliance_tag, taken_at, 'profile', 3 FROM player_profile"
            : "";
        void Run(string sql) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

        Run($"""
            DELETE FROM player_latest;
            INSERT INTO player_latest (uid, name, server, alliance_id, tag, seen, source)
            SELECT uid, name, server, aid, tag, at, src FROM (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY uid ORDER BY at DESC, prio DESC) AS rn FROM (
                    SELECT t.owner_uid AS uid, t.name, t.server_id AS server, t.alliance_id AS aid, t.alliance_tag AS tag,
                           s.taken_at AS at, 'map' AS src, 1 AS prio
                    FROM map_tile t JOIN map_scan s ON s.scan_id = t.scan_id WHERE t.owner_uid <> ''
                    UNION ALL
                    SELECT uid, name, server_id, alliance_id, alliance_tag, taken_at, 'roster', 2
                    FROM power_sample WHERE source = 'AllianceRoster' AND alliance_id <> ''
                    {profiles}))
            WHERE rn = 1;
            """);
        if (hasProfiles)
            Run("""
                UPDATE player_latest SET server = (SELECT home_server FROM player_profile p
                                                   WHERE p.uid = player_latest.uid ORDER BY taken_at DESC LIMIT 1)
                WHERE source <> 'profile' AND EXISTS (SELECT 1 FROM player_profile p WHERE p.uid = player_latest.uid);
                """);
        if (hasProfiles)
        {
            // k per home server from the last fit (HeroModel), else overall, else the default.
            var k = Exists(c, tx, "hero_fit")
                ? $"COALESCE(hf.k, (SELECT k FROM hero_fit WHERE server = 0), {HeroModel.DefaultK.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
                : HeroModel.DefaultK.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var join = Exists(c, tx, "hero_fit") ? "LEFT JOIN hero_fit hf ON hf.server = p.home_server" : "";
            Run($"""
                UPDATE player_latest pl SET power = x.power, troops = x.army_power, kills = x.army_kill, hero = x.hero, profiled_at = x.taken_at,
                       level = x.level, country = NULLIF(x.country, ''), flag = NULLIF(x.flag, ''), current_server = NULLIF(x.current_server, 0), svip = x.svip_level
                FROM (SELECT p.uid, p.power, p.army_power, p.army_kill, p.taken_at, p.level, p.country, p.flag, p.current_server, p.svip_level,
                             {HeroModel.EstimateSql("p.power", "p.army_power", k)} AS hero
                      FROM (SELECT DISTINCT ON (uid) uid, home_server, power, army_power, army_kill, taken_at, level, country, flag, current_server, svip_level
                            FROM player_profile ORDER BY uid, taken_at DESC) p {join}) x
                WHERE pl.uid = x.uid;
                """);
        }
        Run("""
            DELETE FROM alliance_latest;
            INSERT INTO alliance_latest (alliance_id, tag, server, members, seen)
            SELECT alliance_id,
                   COALESCE((SELECT tag FROM player_latest p WHERE p.alliance_id = a.alliance_id AND tag <> '' ORDER BY seen DESC LIMIT 1), ''),
                   (SELECT server FROM player_latest p WHERE p.alliance_id = a.alliance_id
                    GROUP BY server ORDER BY COUNT(*) DESC, MAX(seen) DESC LIMIT 1),
                   members, seen
            FROM (SELECT alliance_id, COUNT(*) AS members, MAX(seen) AS seen
                  FROM player_latest WHERE alliance_id <> '' GROUP BY alliance_id) a;
            """);
        cmd.CommandText = "SELECT COUNT(*) FROM player_latest;";
        var n = Convert.ToInt32(cmd.ExecuteScalar());
        tx.Commit();
        return n;
    }

    static bool Exists(NpgsqlConnection c, NpgsqlTransaction tx, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 WHERE to_regclass(@t) IS NOT NULL;";
        cmd.Parameters.AddWithValue("@t", table);
        return cmd.ExecuteScalar() is not null;
    }
}
