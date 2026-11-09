using LastWar.Core.Model;
using Npgsql;

namespace LastWar.Storage;

/// <summary>Chat persistence. Separate from LastWarDb's scan tables but the same database.</summary>
public sealed class ChatStore(string path)
{
    public string Path { get; } = path;

    NpgsqlConnection Open() => Db.Open(Path);

    public void EnsureSchema()
    {
        if (!Db.FirstTime(Path, "ChatStore")) return;   // DDL once per process: it takes table locks
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS chat_message (
                room_id     TEXT    NOT NULL,
                seq_id      BIGINT NOT NULL,
                grp         TEXT    NOT NULL DEFAULT '',
                sender_uid  TEXT    NOT NULL DEFAULT '',
                sender_name TEXT    NOT NULL DEFAULT '',
                sender_al   TEXT    NOT NULL DEFAULT '',
                msg         TEXT    NOT NULL DEFAULT '',
                sent_at     TEXT    NOT NULL,
                msg_type    BIGINT NOT NULL DEFAULT 0,
                raw         TEXT    NOT NULL DEFAULT '',
                PRIMARY KEY (room_id, seq_id)
            );
            CREATE INDEX IF NOT EXISTS ix_chat_sender ON chat_message(sender_uid);
            CREATE INDEX IF NOT EXISTS ix_chat_time   ON chat_message(sent_at);
            -- Translations sit beside the messages, never in them: the original row is never
            -- rewritten, and one message can hold a translation per language.
            CREATE TABLE IF NOT EXISTS chat_translation (
                room_id     TEXT    NOT NULL,
                seq_id      BIGINT NOT NULL,
                lang        TEXT    NOT NULL,
                text        TEXT    NOT NULL,
                source_lang TEXT    NOT NULL DEFAULT '',
                at          TEXT    NOT NULL,
                PRIMARY KEY (room_id, seq_id, lang)
            );
            -- A room taken out of use: archived (messages kept, left out of chat stats) or deleted
            -- (messages gone; the row stays so the room is not auto-followed back in).
            CREATE TABLE IF NOT EXISTS chat_room_state (
                room_id TEXT PRIMARY KEY,
                state   TEXT NOT NULL,
                at      TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Stores what is new; returns how many rows were.</summary>
    public int Insert(IReadOnlyCollection<ChatMessage> messages) => InsertNew(messages).Count;

    /// <summary>
    /// Stores messages and hands back only the ones that were not already held. Existing rows
    /// are never touched (ON CONFLICT DO NOTHING), so a live feed, a poll and a backfill can all write
    /// the same message and the first copy wins.
    /// </summary>
    public List<ChatMessage> InsertNew(IReadOnlyCollection<ChatMessage> messages)
    {
        var fresh = new List<ChatMessage>();
        if (messages.Count == 0) return fresh;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var ins = c.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO chat_message
              (room_id, seq_id, grp, sender_uid, sender_name, sender_al, msg, sent_at, msg_type, raw)
            SELECT @room, @seq, @grp, @uid, @name, @al, @msg, @at, @type, @raw
            WHERE NOT EXISTS (SELECT 1 FROM chat_room_state s WHERE s.room_id = @room AND s.state = 'deleted')   -- a deleted room stays empty
            ON CONFLICT DO NOTHING;
            """;
        foreach (var n in new[] { "@room", "@seq", "@grp", "@uid", "@name", "@al", "@msg", "@at", "@type", "@raw" })
            ins.Parameters.Add(new NpgsqlParameter(n, null));

        foreach (var m in messages)
        {
            ins.Parameters["@room"].Value = m.RoomId;
            ins.Parameters["@seq"].Value = m.SeqId;
            ins.Parameters["@grp"].Value = m.Group;
            ins.Parameters["@uid"].Value = m.SenderUid;
            ins.Parameters["@name"].Value = m.SenderName;
            ins.Parameters["@al"].Value = m.SenderAlliance;
            ins.Parameters["@msg"].Value = m.Text;
            ins.Parameters["@at"].Value = m.SentAt.ToString("O");
            ins.Parameters["@type"].Value = m.Type;
            ins.Parameters["@raw"].Value = m.Raw;
            if (ins.ExecuteNonQuery() == 1) fresh.Add(m);
        }
        tx.Commit();
        return fresh;
    }

    public void SaveTranslation(string roomId, long seqId, string lang, string text, string sourceLang)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_translation (room_id, seq_id, lang, text, source_lang, at)
            VALUES (@r, @s, @l, @t, @src, @at)
            ON CONFLICT (room_id, seq_id, lang) DO UPDATE SET text = EXCLUDED.text, source_lang = EXCLUDED.source_lang, at = EXCLUDED.at;
            """;
        cmd.Parameters.AddWithValue("@r", roomId);
        cmd.Parameters.AddWithValue("@s", seqId);
        cmd.Parameters.AddWithValue("@l", lang);
        cmd.Parameters.AddWithValue("@t", text);
        cmd.Parameters.AddWithValue("@src", sourceLang);
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Stored translations for a seq range of one room, keyed by seq.</summary>
    public Dictionary<long, (string Text, string SourceLang)> Translations(string roomId, string lang, long lo, long hi)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT seq_id, text, source_lang FROM chat_translation
            WHERE room_id=@r AND lang=@l AND seq_id BETWEEN @lo AND @hi;
            """;
        cmd.Parameters.AddWithValue("@r", roomId);
        cmd.Parameters.AddWithValue("@l", lang);
        cmd.Parameters.AddWithValue("@lo", lo);
        cmd.Parameters.AddWithValue("@hi", hi);
        using var r = cmd.ExecuteReader();
        var outp = new Dictionary<long, (string, string)>();
        while (r.Read()) outp[r.GetInt64(0)] = (r.GetString(1), r.GetString(2));
        return outp;
    }

    public ChatMessage? Get(string roomId, long seqId) =>
        Page(roomId, seqId + 1, 1) is [var m] && m.SeqId == seqId ? m : null;

    public long? NewestSeqId(string roomId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(seq_id) FROM chat_message WHERE room_id=@r;";
        cmd.Parameters.AddWithValue("@r", roomId);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v);
    }

    /// <summary>Newest-first page of a room, below <paramref name="beforeSeq"/> when given.</summary>
    public List<ChatMessage> Page(string roomId, long? beforeSeq, int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT seq_id, grp, sender_uid, sender_name, sender_al, msg, sent_at, msg_type, raw
            FROM chat_message WHERE room_id=@r AND seq_id < @b ORDER BY seq_id DESC LIMIT @n;
            """;
        cmd.Parameters.AddWithValue("@r", roomId);
        cmd.Parameters.AddWithValue("@b", beforeSeq ?? long.MaxValue);
        cmd.Parameters.AddWithValue("@n", Math.Clamp(limit, 1, 1000));
        using var r = cmd.ExecuteReader();
        var outp = new List<ChatMessage>();
        while (r.Read())
            outp.Add(new ChatMessage
            {
                RoomId = roomId,
                SeqId = r.GetInt64(0),
                Group = r.GetString(1),
                SenderUid = r.GetString(2),
                SenderName = r.GetString(3),
                SenderAlliance = r.GetString(4),
                Text = r.GetString(5),
                SentAt = DateTimeOffset.Parse(r.GetString(6)),
                Type = (int)r.GetInt64(7),
                Raw = r.GetString(8),
            });
        return outp;
    }

    /// <summary>
    /// Searches every stored message (not just what a page has loaded), newest first. Every word
    /// of the query must appear in the text, the sender's name or a stored translation; a query
    /// that is exactly a tag or a uid also matches the sender's alliance tag or uid. Pages back
    /// with beforeAt (the oldest sent_at of the previous page). room "" = every room.
    /// </summary>
    public (List<ChatMessage> Messages, bool More) Search(string query, string room, string? beforeAt, int limit)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Replace("%", "").Replace("_", "")).Where(w => w.Length > 0).Take(6).ToList();
        if (words.Count == 0) return ([], false);
        using var c = Open();
        using var cmd = c.CreateCommand();
        var clauses = words.Select((_, i) =>
            $"(m.msg ILIKE @w{i} OR m.sender_name ILIKE @w{i} OR EXISTS (SELECT 1 FROM chat_translation t " +
            $"WHERE t.room_id = m.room_id AND t.seq_id = m.seq_id AND t.text ILIKE @w{i}))");
        cmd.CommandText = $"""
            SELECT m.room_id, m.seq_id, m.grp, m.sender_uid, m.sender_name, m.sender_al, m.msg, m.sent_at, m.msg_type, m.raw
            FROM chat_message m
            WHERE (@room = '' OR m.room_id = @room) AND m.sent_at < @before
              AND (({string.Join(" AND ", clauses)}) OR lower(m.sender_al) = lower(@exact) OR m.sender_uid = @exact)
            ORDER BY m.sent_at DESC, m.seq_id DESC LIMIT @n;
            """;
        for (var i = 0; i < words.Count; i++) cmd.Parameters.AddWithValue($"@w{i}", "%" + words[i] + "%");
        cmd.Parameters.AddWithValue("@exact", query.Trim());
        cmd.Parameters.AddWithValue("@room", room);
        cmd.Parameters.AddWithValue("@before", string.IsNullOrEmpty(beforeAt) ? "9999" : beforeAt);
        cmd.Parameters.AddWithValue("@n", Math.Clamp(limit, 1, 500) + 1);
        using var r = cmd.ExecuteReader();
        var outp = new List<ChatMessage>();
        while (r.Read())
            outp.Add(new ChatMessage
            {
                RoomId = r.GetString(0), SeqId = r.GetInt64(1), Group = r.GetString(2), SenderUid = r.GetString(3),
                SenderName = r.GetString(4), SenderAlliance = r.GetString(5), Text = r.GetString(6),
                SentAt = DateTimeOffset.Parse(r.GetString(7)), Type = (int)r.GetInt64(8), Raw = r.GetString(9),
            });
        var more = outp.Count > Math.Clamp(limit, 1, 500);
        if (more) outp.RemoveAt(outp.Count - 1);
        return (outp, more);
    }

    /// <summary>When the oldest stored message of a room was sent (null if none).</summary>
    public string? OldestAt(string roomId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sent_at FROM chat_message WHERE room_id = @r ORDER BY seq_id LIMIT 1;";
        cmd.Parameters.AddWithValue("@r", roomId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>SQL for "not an archived/deleted room"; append to a WHERE on chat_message.</summary>
    public const string InUse = "room_id NOT IN (SELECT room_id FROM chat_room_state)";

    /// <summary>
    /// A private room: a DM (custom_<uid>_<uid>_v2, name PRIVATE_a_to_b) or a player-made group. Public
    /// "custom" rooms have their own prefixes (season rooms, star war teams) and stay public.
    /// </summary>
    public static bool IsPrivate(string room) =>
        room.StartsWith("custom_") && !room.StartsWith("custom_season_sustain_") && !room.StartsWith("custom_starwar_");

    /// <summary>Rooms taken out of use: room id -> (state, when).</summary>
    public Dictionary<string, (string State, string At)> States()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT room_id, state, at FROM chat_room_state;";
        using var r = cmd.ExecuteReader();
        var outp = new Dictionary<string, (string, string)>();
        while (r.Read()) outp[r.GetString(0)] = (r.GetString(1), r.GetString(2));
        return outp;
    }

    /// <summary>Archive (true) or restore (false). Messages are untouched either way.</summary>
    public void Archive(string roomId, bool on)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = on
            ? "INSERT INTO chat_room_state (room_id, state, at) VALUES (@r, 'archived', @t) ON CONFLICT (room_id) DO UPDATE SET state = 'archived', at = @t;"
            : "DELETE FROM chat_room_state WHERE room_id = @r;";
        cmd.Parameters.AddWithValue("@r", roomId);
        cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes every stored message and translation of one room, for good. Returns messages removed.</summary>
    public int DeleteRoom(string roomId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM chat_translation WHERE room_id = @r;
            INSERT INTO chat_room_state (room_id, state, at) VALUES (@r, 'deleted', @t)
              ON CONFLICT (room_id) DO UPDATE SET state = 'deleted', at = @t;
            """;
        cmd.Parameters.AddWithValue("@r", roomId);
        cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        using var del = c.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM chat_message WHERE room_id = @r;";
        del.Parameters.AddWithValue("@r", roomId);
        var n = del.ExecuteNonQuery();
        tx.Commit();
        return n;
    }

    /// <summary>Every room held, with how much of it and when it last spoke.</summary>
    public List<(string Room, long Messages, long Newest, string LastAt, string FirstAt)> Rooms()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT room_id, COUNT(*), MAX(seq_id),
                   (SELECT sent_at FROM chat_message m2 WHERE m2.room_id=m.room_id ORDER BY seq_id DESC LIMIT 1),
                   (SELECT sent_at FROM chat_message m3 WHERE m3.room_id=m.room_id ORDER BY seq_id LIMIT 1)
            FROM chat_message m GROUP BY room_id ORDER BY 2 DESC;
            """;
        using var r = cmd.ExecuteReader();
        var outp = new List<(string, long, long, string, string)>();
        while (r.Read()) outp.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4)));
        return outp;
    }

    /// <summary>
    /// Lowest seqId stored for a room, i.e. how far back the backfill has reached.
    /// Null when the room has nothing yet. This is what makes a paced pull resumable
    /// across process restarts - which matters because each restart also refreshes the
    /// chat token.
    /// </summary>
    public long? OldestSeqId(string roomId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MIN(seq_id) FROM chat_message WHERE room_id=@r;";
        cmd.Parameters.AddWithValue("@r", roomId);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v);
    }

    public (long Messages, long Oldest, long Newest) RoomStats(string roomId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(MIN(seq_id),0), COALESCE(MAX(seq_id),0) FROM chat_message WHERE room_id=@r;";
        cmd.Parameters.AddWithValue("@r", roomId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)) : (0, 0, 0);
    }

    public (long Messages, long Rooms) Stats()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COUNT(DISTINCT room_id) FROM chat_message;";
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : (0, 0);
    }
}
