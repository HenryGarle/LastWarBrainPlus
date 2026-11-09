using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// Who has been in which alliance, and when - DERIVED from every observation of a player's
/// alliance (map tiles, power rows, profile sweeps, leaderboard rows), rebuilt from scratch like
/// the other histories. Each unbroken run of sightings in one alliance is a stint:
///   first_seen / last_seen - the stint's first and last sighting
///   left_at   - the first sighting AFTER it in another alliance (or none); '' while still a member
///   joined_known - 0 when the stint starts at the player's first sighting ever: they were already
///                  a member when we started watching, so the join date is unknown.
/// An empty alliance id on a map tile, power row or profile means "in no alliance" and ends a
/// stint. On leaderboard rows and the earliest alliance-roster power rows it only means the field
/// was not recorded (a roster row is by definition IN an alliance), so those are used only when
/// they carry an id.
/// </summary>
public sealed class AllianceMembership(string path)
{
    NpgsqlConnection Open() => Db.Open(path);

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "AllianceMembership")) return;   // DDL once per process: it takes table locks
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS alliance_stint (
                uid          TEXT    NOT NULL,
                seq          BIGINT NOT NULL,
                alliance_id  TEXT    NOT NULL,
                tag          TEXT    NOT NULL DEFAULT '',
                first_seen   TEXT    NOT NULL,
                last_seen    TEXT    NOT NULL,
                left_at      TEXT    NOT NULL DEFAULT '',
                joined_known BIGINT NOT NULL DEFAULT 0,
                next_alliance_id TEXT NOT NULL DEFAULT '',
                next_tag     TEXT    NOT NULL DEFAULT '',
                PRIMARY KEY (uid, seq)
            );
            CREATE INDEX IF NOT EXISTS ix_stint_alliance ON alliance_stint(alliance_id, left_at);
            """;
        cmd.ExecuteNonQuery();
    }

    const string Evidence = """
        SELECT t.owner_uid, s.taken_at, t.alliance_id, t.alliance_tag FROM map_tile t JOIN map_scan s ON s.scan_id = t.scan_id WHERE t.owner_uid <> ''
        UNION ALL SELECT uid, taken_at, alliance_id, alliance_tag FROM power_sample WHERE NOT (source = 'AllianceRoster' AND alliance_id = '')
        UNION ALL SELECT uid, taken_at, alliance_id, alliance_tag FROM player_profile
        UNION ALL SELECT uid, taken_at, alliance_id, alliance_tag FROM board_sample WHERE board_type NOT IN (1, 2) AND alliance_id <> ''
        """;

    public int Rebuild()
    {
        EnsureSchema();
        using var c = Open();
        var stints = new List<(string Uid, string Al, string Tag, string First, string Last, string Left, bool Known, string NextAl, string NextTag)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandTimeout = 300;
            cmd.CommandText = $"SELECT * FROM ({Evidence}) ORDER BY 1, 2, 3, 4;";   // ties at one moment break the same way every time
            using var r = cmd.ExecuteReader();
            string? uid = null;
            var segs = new List<(string Al, string Tag, string First, string Last)>();
            void Flush()
            {
                if (uid is null) return;
                for (var i = 0; i < segs.Count; i++)
                {
                    var s = segs[i];
                    if (s.Al.Length == 0) continue;           // a spell in no alliance is not a stint
                    var next = i + 1 < segs.Count ? segs[i + 1] : default;
                    stints.Add((uid, s.Al, s.Tag, s.First, s.Last, next.First ?? "", i > 0, next.Al ?? "", next.Tag ?? ""));
                }
            }
            while (r.Read())
            {
                var u = r.GetString(0);
                if (u != uid) { Flush(); uid = u; segs.Clear(); }
                var (at, al, tag) = (r.GetString(1), r.GetString(2), r.GetString(3));
                if (al.Length == 0 && tag.Length > 0) continue;   // a tag with no id: the id just was not recorded
                if (segs.Count > 0 && segs[^1].Al == al)
                    segs[^1] = (al, tag.Length > 0 ? tag : segs[^1].Tag, segs[^1].First, at);
                else segs.Add((al, tag, at, at));
            }
            Flush();
        }

        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM alliance_stint;"; del.ExecuteNonQuery(); }
        string? last = null;
        var seq = 0;
        Db.Copy(c, "alliance_stint", ["uid", "seq", "alliance_id", "tag", "first_seen", "last_seen", "left_at", "joined_known", "next_alliance_id", "next_tag"],
            stints.Select(s =>
            {
                seq = s.Uid == last ? seq + 1 : 0;
                last = s.Uid;
                return new object?[] { s.Uid, seq, s.Al, s.Tag, s.First, s.Last, s.Left, s.Known, s.NextAl, s.NextTag };
            }));
        tx.Commit();
        return stints.Count;
    }
}
