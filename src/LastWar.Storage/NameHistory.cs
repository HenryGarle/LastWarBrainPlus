using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// Name changes for players (name) and alliances (tag and name), detected from everything we
/// have ever observed with a timestamp: map scans, power snapshots, leaderboards, profile sweeps,
/// chat messages and transfer applications.
///
/// The observations are the record and are never touched. name_change is DERIVED from them: each
/// rebuild walks every id's observations in time order and logs a change wherever the value
/// differs from the one before, with the last time the old value was seen and the first time
/// the new one was (the moment we detected it). Rebuilding from scratch keeps it right when older
/// data arrives late - a chat backfill can reveal a name from months ago.
/// </summary>
public sealed class NameHistory(string path)
{
    NpgsqlConnection Open() => Db.Open(path);

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "NameHistory")) return;   // DDL once per process: it takes table locks
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS name_change (
                kind           TEXT NOT NULL,   -- player | alliance
                id             TEXT NOT NULL,   -- uid or alliance id
                field          TEXT NOT NULL,   -- name | tag
                old_value      TEXT NOT NULL,
                new_value      TEXT NOT NULL,
                last_seen_old  TEXT NOT NULL,
                first_seen_new TEXT NOT NULL,   -- when we detected the change
                source         TEXT NOT NULL DEFAULT '',  -- where the new value was first seen
                PRIMARY KEY (kind, id, field, first_seen_new)
            );
            CREATE INDEX IF NOT EXISTS ix_name_change_time ON name_change(first_seen_new);
            CREATE INDEX IF NOT EXISTS ix_name_change_new ON name_change(new_value);
            CREATE INDEX IF NOT EXISTS ix_name_change_old ON name_change(old_value);
            """;
        cmd.ExecuteNonQuery();
    }

    // Every timed observation of a value, as (id, value, at, source). Board types 1 and 2 are the
    // alliance boards, whose uid IS the alliance id.
    const string PlayerNames = """
        SELECT t.owner_uid, t.name, s.taken_at, 'map' FROM map_tile t JOIN map_scan s ON s.scan_id = t.scan_id WHERE t.name <> '' AND t.owner_uid <> ''
        UNION ALL SELECT uid, name, taken_at, 'power' FROM power_sample WHERE name <> ''
        UNION ALL SELECT uid, name, taken_at, 'board' FROM board_sample WHERE board_type NOT IN (1, 2) AND name <> ''
        UNION ALL SELECT uid, name, taken_at, 'profile' FROM player_profile WHERE name <> ''
        UNION ALL SELECT sender_uid, sender_name, sent_at, 'chat' FROM chat_message WHERE sender_name <> '' AND sender_uid ~ '^[0-9]'
        UNION ALL SELECT uid, name, captured_at, 'applicant' FROM migration_applicant WHERE name <> ''
        """;
    const string AllianceTags = """
        SELECT t.alliance_id, t.alliance_tag, s.taken_at, 'map' FROM map_tile t JOIN map_scan s ON s.scan_id = t.scan_id WHERE t.alliance_id <> '' AND t.alliance_tag <> ''
        UNION ALL SELECT alliance_id, alliance_tag, taken_at, 'power' FROM power_sample WHERE alliance_id <> '' AND alliance_tag <> ''
        UNION ALL SELECT uid, alliance_tag, taken_at, 'board' FROM board_sample WHERE board_type IN (1, 2) AND alliance_tag <> ''
        UNION ALL SELECT alliance_id, alliance_tag, taken_at, 'board' FROM board_sample WHERE board_type NOT IN (1, 2) AND alliance_id <> '' AND alliance_tag <> ''
        UNION ALL SELECT alliance_id, alliance_tag, taken_at, 'profile' FROM player_profile WHERE alliance_id <> '' AND alliance_tag <> ''
        """;
    const string AllianceNames = """
        SELECT alliance_id, alliance_name, taken_at, 'power' FROM power_sample WHERE alliance_id <> '' AND alliance_name <> ''
        UNION ALL SELECT uid, name, taken_at, 'board' FROM board_sample WHERE board_type IN (1, 2) AND name <> ''
        UNION ALL SELECT alliance_id, alliance_name, taken_at, 'profile' FROM player_profile WHERE alliance_id <> '' AND alliance_name <> ''
        """;

    public sealed record Change(string Kind, string Id, string Field, string Old, string New,
        string LastSeenOld, string FirstSeenNew, string Source);

    /// <summary>Recomputes every change from the observations. Returns how many are logged.</summary>
    public int Rebuild()
    {
        EnsureSchema();
        var changes = new List<Change>();
        using var c = Open();
        foreach (var (kind, field, sql) in new[]
        {
            ("player", "name", PlayerNames), ("alliance", "tag", AllianceTags), ("alliance", "name", AllianceNames),
        })
            changes.AddRange(Detect(c, kind, field, sql));

        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM name_change;"; del.ExecuteNonQuery(); }
        using var ins = c.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO name_change (kind, id, field, old_value, new_value, last_seen_old, first_seen_new, source)
            VALUES (@k, @i, @f, @o, @n, @lo, @fn, @s) ON CONFLICT DO NOTHING;
            """;
        foreach (var p in new[] { "@k", "@i", "@f", "@o", "@n", "@lo", "@fn", "@s" }) ins.Parameters.Add(new NpgsqlParameter(p, null));
        foreach (var ch in changes)
        {
            ins.Parameters["@k"].Value = ch.Kind; ins.Parameters["@i"].Value = ch.Id; ins.Parameters["@f"].Value = ch.Field;
            ins.Parameters["@o"].Value = ch.Old; ins.Parameters["@n"].Value = ch.New;
            ins.Parameters["@lo"].Value = ch.LastSeenOld; ins.Parameters["@fn"].Value = ch.FirstSeenNew; ins.Parameters["@s"].Value = ch.Source;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return changes.Count;
    }

    static IEnumerable<Change> Detect(NpgsqlConnection c, string kind, string field, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 300;
        // Timestamps are all DateTimeOffset.ToString("O") in UTC, so text order is time order.
        cmd.CommandText = $"SELECT * FROM ({sql}) ORDER BY 1, 3, 4, 2;";   // source then value break ties the same way every time
        using var r = cmd.ExecuteReader();
        string? id = null, value = null, seen = null;
        while (r.Read())
        {
            var rid = r.GetString(0);
            var v = r.GetString(1).Trim();
            var at = r.GetString(2);
            if (rid != id) { id = rid; value = v; seen = at; continue; }
            if (v.Length == 0) continue;
            if (v != value)
            {
                yield return new Change(kind, id, field, value!, v, seen!, at, r.GetString(3));
                value = v;
            }
            seen = at;
        }
    }

    static List<Dictionary<string, object?>> Rows(NpgsqlCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    const string Select = """
        SELECT kind, id, field, old_value AS old, new_value AS new, last_seen_old AS "lastSeenOld",
               first_seen_new AS detected, source FROM name_change
        """;

    /// <summary>Every change logged for one player or alliance, oldest first.</summary>
    public List<Dictionary<string, object?>> For(string kind, string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"{Select} WHERE kind = @k AND id = @i ORDER BY first_seen_new;";
        cmd.Parameters.AddWithValue("@k", kind);
        cmd.Parameters.AddWithValue("@i", id);
        return Rows(cmd);
    }

    /// <summary>The newest changes across everything, optionally only players or alliances.</summary>
    public List<Dictionary<string, object?>> Recent(int limit, string? kind = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"{Select} WHERE (@k = '' OR kind = @k) ORDER BY first_seen_new DESC, kind, id, field LIMIT @n;";
        cmd.Parameters.AddWithValue("@k", kind ?? "");
        cmd.Parameters.AddWithValue("@n", limit);
        return Rows(cmd);
    }

    /// <summary>Ids that have EVER carried this name - so an old name still finds the player.</summary>
    public List<Dictionary<string, object?>> Find(string name, int limit = 25)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"{Select} WHERE old_value ILIKE @q OR new_value ILIKE @q ORDER BY first_seen_new DESC LIMIT @n;";
        cmd.Parameters.AddWithValue("@q", "%" + name.Replace("%", "").Replace("_", "") + "%");
        cmd.Parameters.AddWithValue("@n", limit);
        return Rows(cmd);
    }
}
