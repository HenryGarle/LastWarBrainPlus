using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// Where each player has belonged and been, over time - DERIVED from the observations, rebuilt
/// from scratch like <see cref="NameHistory"/>, never touching the observations themselves.
///
/// Two kinds of evidence, kept apart because conflating them is the mistake this exists to avoid:
///   HOME  - the server a player is FROM: profile home_server, a leaderboard row's src_server,
///           a transfer application's from_server. Changes only on a transfer.
///   BASED - where their base was physically seen: a map tile, a map-driven power row, a chat
///           message in a world room. Changes on every teleport to fight.
///
/// Events written per player, in order:
///   created  - the account's creation server (uid suffix). No date is known.
///   early    - first known home differs from creation: moved before we were watching. The game
///              rebalanced early players; dated to November 2025 and marked estimated.
///   transfer - home A -> home B, bracketed by the last evidence of A and the first of B. BASED
///              evidence in the gap tightens the bracket (a base seen on B means already moved).
///   visit    - base on X while home stayed H on both sides: a trip to fight, first..last seen.
/// </summary>
public sealed class ServerHistory(string path)
{
    /// <summary>When an early (pre-tracking) move is assumed to have happened.</summary>
    public const string EarlyMoveEstimate = "2025-11-01T00:00:00.0000000+00:00";

    NpgsqlConnection Open() => Db.Open(path);

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "ServerHistory")) return;   // DDL once per process: it takes table locks
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS server_history (
                uid         TEXT    NOT NULL,
                seq         BIGINT NOT NULL,          -- order within the player's story
                kind        TEXT    NOT NULL,          -- created | early | transfer | visit
                from_server BIGINT NOT NULL DEFAULT 0,
                to_server   BIGINT NOT NULL DEFAULT 0,
                from_at     TEXT    NOT NULL DEFAULT '', -- transfer: last seen at the old home; visit: first seen there
                to_at       TEXT    NOT NULL DEFAULT '', -- transfer: first seen at the new home; visit: last seen there
                estimated   BIGINT NOT NULL DEFAULT 0,
                evidence    TEXT    NOT NULL DEFAULT '',
                PRIMARY KEY (uid, seq)
            );
            CREATE INDEX IF NOT EXISTS ix_server_history_kind ON server_history(kind, to_at);
            """;
        cmd.ExecuteNonQuery();
    }

    // (uid, at, server, isHome, source). Profile-sourced power rows are filed under the HOME
    // server, so they are home evidence already covered by player_profile and left out here.
    const string Evidence = """
        SELECT uid, taken_at, home_server, 1, 'profile' FROM player_profile WHERE home_server > 0
        UNION ALL SELECT uid, taken_at, src_server, 1, 'board' FROM board_sample WHERE src_server > 0 AND board_type NOT IN (1, 2)
        UNION ALL SELECT uid, captured_at, from_server, 1, 'application' FROM migration_applicant WHERE from_server > 0
        UNION ALL SELECT t.owner_uid, s.taken_at, t.server_id, 0, 'map' FROM map_tile t JOIN map_scan s ON s.scan_id = t.scan_id WHERE t.owner_uid <> ''
        UNION ALL SELECT uid, taken_at, server_id, 0, 'power' FROM power_sample WHERE source <> 'Profile'
        UNION ALL SELECT sender_uid, sent_at, CAST(substr(room_id, 9) AS BIGINT), 0, 'chat' FROM chat_message
            WHERE room_id ~ '^country_[0-9][^_]*$' AND sender_uid ~ '^[0-9]'
        """;

    public sealed record Event(string Uid, string Kind, int From, int To, string FromAt, string ToAt, bool Estimated, string Evidence);

    public int Rebuild()
    {
        EnsureSchema();
        using var c = Open();
        var events = new List<Event>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandTimeout = 300;
            cmd.CommandText = $"SELECT * FROM ({Evidence}) ORDER BY 1, 2, 3, 4, 5;";   // timestamps are ISO-8601 UTC: text order = time order
            using var r = cmd.ExecuteReader();
            string? uid = null;
            var obs = new List<(string At, int Server, bool Home, string Source)>();
            while (r.Read())
            {
                var u = r.GetString(0);
                if (u != uid)
                {
                    if (uid is not null) events.AddRange(Story(uid, obs));
                    uid = u;
                    obs.Clear();
                }
                obs.Add((r.GetString(1), (int)r.GetInt64(2), (int)r.GetInt64(3) == 1, r.GetString(4)));
            }
            if (uid is not null) events.AddRange(Story(uid, obs));
        }

        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM server_history;"; del.ExecuteNonQuery(); }
        string? last = null;
        var seq = 0;
        Db.Copy(c, "server_history", ["uid", "seq", "kind", "from_server", "to_server", "from_at", "to_at", "estimated", "evidence"],
            events.Select(e =>
            {
                seq = e.Uid == last ? seq + 1 : 0;
                last = e.Uid;
                return new object?[] { e.Uid, seq, e.Kind, e.From, e.To, e.FromAt, e.ToAt, e.Estimated, e.Evidence };
            }));
        tx.Commit();
        return events.Count;
    }

    /// <summary>One player's story from their observations, which arrive sorted by time.</summary>
    public static IEnumerable<Event> Story(string uid, IReadOnlyList<(string At, int Server, bool Home, string Source)> obs)
    {
        var origin = uid.Length >= 6 && uid.All(char.IsAsciiDigit) && int.TryParse(uid[^6..], out var o) ? o : 0;
        if (origin == 0) yield break;   // not a player uid
        yield return new Event(uid, "created", 0, origin, "", "", false, "uid");

        var homes = obs.Where(x => x.Home).ToList();
        if (homes.Count == 0) yield break;   // nothing says where they are FROM - a map sighting alone can't

        if (homes[0].Server != origin)
            yield return new Event(uid, "early", origin, homes[0].Server, EarlyMoveEstimate, homes[0].At, true,
                $"first known home {homes[0].Source}");

        // Home segments: consecutive home evidence on the same server.
        var segs = new List<(int Server, string First, string Last)>();
        foreach (var h in homes)
            if (segs.Count > 0 && segs[^1].Server == h.Server) segs[^1] = (h.Server, segs[^1].First, h.At);
            else segs.Add((h.Server, h.At, h.At));

        // What home was on either side of a moment: the segment ending at or before it and the
        // one starting at or after it.
        int HomeBefore(string at) { var s = segs.LastOrDefault(x => string.CompareOrdinal(x.First, at) <= 0); return s.Server; }
        int HomeAfter(string at) { var s = segs.FirstOrDefault(x => string.CompareOrdinal(x.Last, at) >= 0); return s.Server; }

        var based = obs.Where(x => !x.Home && x.Server > 0).ToList();
        var events = new List<Event>();
        for (var i = 1; i < segs.Count; i++)
        {
            var (a, b) = (segs[i - 1], segs[i]);
            // Base sightings inside the gap narrow it: still on A means not moved yet; on B, moved.
            var gap = based.Where(x => string.CompareOrdinal(x.At, a.Last) > 0 && string.CompareOrdinal(x.At, b.First) < 0).ToList();
            var lastA = gap.Where(x => x.Server == a.Server).Select(x => x.At).LastOrDefault() ?? a.Last;
            var firstB = gap.Where(x => x.Server == b.Server && string.CompareOrdinal(x.At, lastA) > 0).Select(x => x.At).FirstOrDefault() ?? b.First;
            events.Add(new Event(uid, "transfer", a.Server, b.Server, lastA, firstB, false,
                firstB == b.First ? "home evidence" : "home evidence, base seen on the new server"));
        }

        // Visits: a base on another server while home is the same on both sides of the sighting.
        (int Server, int Home, string First, string Last)? open = null;
        foreach (var x in based)
        {
            var hb = HomeBefore(x.At);
            var ha = HomeAfter(x.At);
            var isVisit = hb != 0 && hb == ha && x.Server != hb;
            if (isVisit && open is { } v && v.Server == x.Server && v.Home == hb) { open = (v.Server, v.Home, v.First, x.At); continue; }
            if (open is { } done) events.Add(new Event(uid, "visit", done.Home, done.Server, done.First, done.Last, false, "base seen away from home"));
            open = isVisit ? (x.Server, hb, x.At, x.At) : null;
        }
        if (open is { } end) events.Add(new Event(uid, "visit", end.Home, end.Server, end.First, end.Last, false, "base seen away from home"));

        foreach (var e in events.OrderBy(e => e.FromAt, StringComparer.Ordinal)) yield return e;
    }

    public List<Dictionary<string, object?>> For(string uid)
    {
        EnsureSchema();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT kind, from_server AS "from", to_server AS "to", from_at AS "fromAt", to_at AS "toAt", estimated, evidence
            FROM server_history WHERE uid = @u ORDER BY seq;
            """;
        cmd.Parameters.AddWithValue("@u", uid);
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

    public List<(string Kind, long Players)> Counts()
    {
        EnsureSchema();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT kind, COUNT(DISTINCT uid) FROM server_history GROUP BY kind;";
        using var r = cmd.ExecuteReader();
        var outp = new List<(string, long)>();
        while (r.Read()) outp.Add((r.GetString(0), r.GetInt64(1)));
        return outp;
    }
}

/// <summary>Everything derived from the observations, rebuilt together after new data lands.</summary>
public static class Derived
{
    public static (int Names, int Moves, int Stints, int Activity, int Latest, int HeroFit) RebuildAll(string path) =>
        (new NameHistory(path).Rebuild(), new ServerHistory(path).Rebuild(), new AllianceMembership(path).Rebuild(),
         new PlayerActivity(path).Rebuild(), new PlayerLatest(path).Rebuild(), new HeroModel(path).Rebuild());
}
