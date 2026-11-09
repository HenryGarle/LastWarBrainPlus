using LastWar.Core.Model;
using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// Snapshots of what a guest can read about alliances and servers (see LastWar.Client.Game.Intel).
/// Every table is append-only, keyed by the moment it was taken, so history is never overwritten:
///
///   alliance_member_sample  al.rank rows: rank, donations (today/week, last donation), joined
///   alliance_official       al.rank officer slots (type 1..4)
///   alliance_info_sample    get.al.info: fight power, engagement, gift level, leader, rules
///   city_sample             city.war.get.info: holder of each city
///   server_state_sample     season.migrate.server.info: score, state, president, inflow
///   president               get.history.king.record: one row per (server, round), kept once
/// </summary>
public sealed class IntelStore(string path)
{
    NpgsqlConnection Open() => Db.Open(path);

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "IntelStore")) return;   // DDL once per process: it takes table locks
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS alliance_member_sample (
                taken_at TEXT NOT NULL, alliance_id TEXT NOT NULL, uid TEXT NOT NULL, name TEXT NOT NULL DEFAULT '',
                rank BIGINT NOT NULL DEFAULT 0, power BIGINT NOT NULL DEFAULT 0, kills BIGINT NOT NULL DEFAULT 0,
                hq BIGINT NOT NULL DEFAULT 0, donate_today BIGINT NOT NULL DEFAULT 0, donate_week BIGINT NOT NULL DEFAULT 0,
                donated_at TEXT NOT NULL DEFAULT '', week_donated_at TEXT NOT NULL DEFAULT '', joined_at TEXT NOT NULL DEFAULT '',
                rank_since TEXT NOT NULL DEFAULT '', month_card_end BIGINT NOT NULL DEFAULT 0,
                server BIGINT NOT NULL DEFAULT 0, current_server BIGINT NOT NULL DEFAULT 0,
                PRIMARY KEY (taken_at, alliance_id, uid));
            CREATE INDEX IF NOT EXISTS ix_ams_uid ON alliance_member_sample(uid, taken_at);
            CREATE INDEX IF NOT EXISTS ix_ams_alliance ON alliance_member_sample(alliance_id, taken_at);
            CREATE TABLE IF NOT EXISTS alliance_official (
                taken_at TEXT NOT NULL, alliance_id TEXT NOT NULL, uid TEXT NOT NULL, type BIGINT NOT NULL,
                PRIMARY KEY (taken_at, alliance_id, type));
            CREATE TABLE IF NOT EXISTS alliance_info_sample (
                taken_at TEXT NOT NULL, alliance_id TEXT NOT NULL, tag TEXT NOT NULL DEFAULT '', name TEXT NOT NULL DEFAULT '',
                server BIGINT NOT NULL DEFAULT 0, create_server BIGINT NOT NULL DEFAULT 0, fight_power BIGINT NOT NULL DEFAULT 0,
                members BIGINT NOT NULL DEFAULT 0, max_members BIGINT NOT NULL DEFAULT 0, engagement BIGINT NOT NULL DEFAULT 0,
                gift_level BIGINT NOT NULL DEFAULT 0, created_at TEXT NOT NULL DEFAULT '', language TEXT NOT NULL DEFAULT '',
                country TEXT NOT NULL DEFAULT '', leader_uid TEXT NOT NULL DEFAULT '', leader_name TEXT NOT NULL DEFAULT '',
                join_approval BIGINT NOT NULL DEFAULT 0, level_limit BIGINT NOT NULL DEFAULT 0, power_limit BIGINT NOT NULL DEFAULT 0,
                announce TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (taken_at, alliance_id));
            CREATE INDEX IF NOT EXISTS ix_ais_alliance ON alliance_info_sample(alliance_id, taken_at);
            CREATE TABLE IF NOT EXISTS city_sample (
                taken_at TEXT NOT NULL, server BIGINT NOT NULL, city_id BIGINT NOT NULL, alliance_id TEXT NOT NULL DEFAULT '',
                tag TEXT NOT NULL DEFAULT '', alliance_name TEXT NOT NULL DEFAULT '', first_occupied_at TEXT NOT NULL DEFAULT '',
                protected_until TEXT NOT NULL DEFAULT '', give_up_at TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (taken_at, server, city_id));
            CREATE INDEX IF NOT EXISTS ix_city_server ON city_sample(server, taken_at);
            CREATE INDEX IF NOT EXISTS ix_city_alliance ON city_sample(alliance_id, taken_at);
            CREATE TABLE IF NOT EXISTS server_state_sample (
                taken_at TEXT NOT NULL, server BIGINT NOT NULL, state BIGINT NOT NULL DEFAULT 0, score BIGINT NOT NULL DEFAULT 0,
                president_uid TEXT NOT NULL DEFAULT '', president_name TEXT NOT NULL DEFAULT '', president_tag TEXT NOT NULL DEFAULT '',
                first_lady_uid TEXT NOT NULL DEFAULT '', first_lady_name TEXT NOT NULL DEFAULT '', apply_power BIGINT NOT NULL DEFAULT 0,
                in_super_low BIGINT NOT NULL DEFAULT 0, in_low BIGINT NOT NULL DEFAULT 0, in_mid BIGINT NOT NULL DEFAULT 0,
                in_high BIGINT NOT NULL DEFAULT 0, notice TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (taken_at, server));
            CREATE TABLE IF NOT EXISTS president (
                server BIGINT NOT NULL, round BIGINT NOT NULL, uid TEXT NOT NULL, name TEXT NOT NULL DEFAULT '',
                tag TEXT NOT NULL DEFAULT '', alliance_name TEXT NOT NULL DEFAULT '', became_at TEXT NOT NULL DEFAULT '',
                occupy_server BIGINT NOT NULL DEFAULT 0, country TEXT NOT NULL DEFAULT '', first_seen TEXT NOT NULL,
                PRIMARY KEY (server, round));
            CREATE TABLE IF NOT EXISTS stronghold_sample (
                taken_at TEXT NOT NULL, server BIGINT NOT NULL, stronghold_id BIGINT NOT NULL, alliance_id TEXT NOT NULL DEFAULT '',
                tag TEXT NOT NULL DEFAULT '', alliance_name TEXT NOT NULL DEFAULT '', level BIGINT NOT NULL DEFAULT 0,
                PRIMARY KEY (taken_at, server, stronghold_id));
            CREATE INDEX IF NOT EXISTS ix_stronghold_alliance ON stronghold_sample(alliance_id, taken_at);
            CREATE TABLE IF NOT EXISTS server_card_sample (
                taken_at TEXT NOT NULL, server BIGINT NOT NULL, created_at TEXT NOT NULL DEFAULT '', king_uid TEXT NOT NULL DEFAULT '',
                king_name TEXT NOT NULL DEFAULT '', king_tag TEXT NOT NULL DEFAULT '', king_power BIGINT NOT NULL DEFAULT 0,
                king_since TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (taken_at, server));
            CREATE TABLE IF NOT EXISTS player_detail_sample (
                taken_at TEXT NOT NULL, uid TEXT NOT NULL, drone_level BIGINT NOT NULL DEFAULT 0, drone_power BIGINT NOT NULL DEFAULT 0,
                chip_level BIGINT NOT NULL DEFAULT 0, drone_props TEXT NOT NULL DEFAULT '', career_type BIGINT NOT NULL DEFAULT 0,
                career_level BIGINT NOT NULL DEFAULT 0, gift_level BIGINT NOT NULL DEFAULT 0, badge_id TEXT NOT NULL DEFAULT '',
                alliance_rank BIGINT NOT NULL DEFAULT 0, battle_win BIGINT NOT NULL DEFAULT 0, battle_lose BIGINT NOT NULL DEFAULT 0,
                army_dead BIGINT NOT NULL DEFAULT 0,
                PRIMARY KEY (uid, taken_at));
            """;
        cmd.ExecuteNonQuery();
    }

    static string T(DateTimeOffset? t) => t?.ToString("O") ?? "";

    /// <summary>Alliances whose members are mostly from lo..hi, biggest first (alliance_latest).</summary>
    public List<(string Id, string Tag, int Server)> Alliances(int lo, int hi, int minMembers)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT alliance_id, tag, server FROM alliance_latest
            WHERE server BETWEEN @lo AND @hi AND members >= @m ORDER BY server, members DESC;
            """;
        cmd.Parameters.AddWithValue("@lo", lo);
        cmd.Parameters.AddWithValue("@hi", hi);
        cmd.Parameters.AddWithValue("@m", minMembers);
        using var r = cmd.ExecuteReader();
        var outp = new List<(string, string, int)>();
        while (r.Read()) outp.Add((r.GetString(0), r.GetString(1), (int)r.GetInt64(2)));
        return outp;
    }

    void Batch(string sql, IEnumerable<object[]> rows)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        var ps = new List<NpgsqlParameter>();
        foreach (var row in rows)
        {
            if (ps.Count == 0)
                for (var i = 0; i < row.Length; i++) { var p = new NpgsqlParameter($"@p{i}", DBNull.Value); ps.Add(p); cmd.Parameters.Add(p); }
            for (var i = 0; i < row.Length; i++) ps[i].Value = row[i] ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    static string Values(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $"@p{i}"));

    public void Insert(AllianceRoster r)
    {
        var at = r.TakenAt.ToString("O");
        Batch($"INSERT INTO alliance_member_sample VALUES ({Values(17)}) ON CONFLICT DO NOTHING;", r.Members.Select(m => new object[]
        {
            at, r.AllianceId, m.Uid, m.Name, m.Rank, m.Power, m.Kills, m.Hq, m.DonateToday, m.DonateWeek,
            T(m.DonatedAt), T(m.WeekDonatedAt), T(m.JoinedAt), T(m.RankSince), m.MonthCardEnd, m.Server, m.CurrentServer,
        }));
        Batch($"INSERT INTO alliance_official VALUES ({Values(4)}) ON CONFLICT DO NOTHING;",
            r.Officials.Where(o => o.Uid.Length > 0).Select(o => new object[] { at, r.AllianceId, o.Uid, o.Type }));
    }

    public void Insert(AllianceInfo a) =>
        Batch($"INSERT INTO alliance_info_sample VALUES ({Values(20)}) ON CONFLICT DO NOTHING;", [[
            a.TakenAt.ToString("O"), a.AllianceId, a.Tag, a.Name, a.Server, a.CreateServer, a.FightPower, a.Members, a.MaxMembers,
            a.Engagement, a.GiftLevel, T(a.CreatedAt), a.Language, a.Country, a.LeaderUid, a.LeaderName, a.JoinApproval ? 1 : 0,
            a.LevelLimit, a.PowerLimit, a.Announce,
        ]]);

    public void Insert(IReadOnlyCollection<CityHold> cities) =>
        Batch($"INSERT INTO city_sample VALUES ({Values(9)}) ON CONFLICT DO NOTHING;", cities.Select(h => new object[]
        {
            h.TakenAt.ToString("O"), h.Server, h.CityId, h.AllianceId, h.Tag, h.AllianceName,
            T(h.FirstOccupiedAt), T(h.ProtectedUntil), T(h.GiveUpAt),
        }));

    public void Insert(ServerState s) =>
        Batch($"INSERT INTO server_state_sample VALUES ({Values(15)}) ON CONFLICT DO NOTHING;", [[
            s.TakenAt.ToString("O"), s.Server, s.State, s.Score, s.PresidentUid, s.PresidentName, s.PresidentTag,
            s.FirstLadyUid, s.FirstLadyName, s.ApplyPower, s.InSuperLow, s.InLow, s.InMid, s.InHigh, s.Notice,
        ]]);

    public void Insert(IReadOnlyCollection<Stronghold> rows) =>
        Batch($"INSERT INTO stronghold_sample VALUES ({Values(7)}) ON CONFLICT DO NOTHING;", rows.Select(s => new object[]
            { s.TakenAt.ToString("O"), s.Server, s.Id, s.AllianceId, s.Tag, s.AllianceName, s.Level }));

    public void Insert(IReadOnlyCollection<ServerCard> rows)
    {
        Batch($"INSERT INTO server_card_sample VALUES ({Values(8)}) ON CONFLICT DO NOTHING;", rows.Select(s => new object[]
            { s.TakenAt.ToString("O"), s.Server, T(s.CreatedAt), s.KingUid, s.KingName, s.KingTag, s.KingPower, T(s.KingSince) }));
        // The registry's president follows the newest reading (it used to change only in the daily registry job).
        var reg = new ServerRegistry(path);
        foreach (var s in rows) reg.SeenKing(s.Server, s.KingUid, s.KingName, s.KingTag, s.KingPower, (string)T(s.KingSince));
    }

    public void Insert(IReadOnlyCollection<PlayerDetail> rows) =>
        Batch($"INSERT INTO player_detail_sample VALUES ({Values(14)}) ON CONFLICT DO NOTHING;", rows.Select(p => new object[]
        {
            p.TakenAt.ToString("O"), p.Uid, p.DroneLevel, p.DronePower, p.ChipLevel, p.DroneProps, p.CareerType, p.CareerLevel,
            p.GiftLevel, p.BadgeId, p.AllianceRank, p.BattleWin, p.BattleLose, p.ArmyDead,
        }));

    public HashSet<string> DetailedSince(DateTimeOffset since)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT uid FROM player_detail_sample WHERE taken_at >= @t;";
        cmd.Parameters.AddWithValue("@t", since.ToString("O"));
        using var r = cmd.ExecuteReader();
        var outp = new HashSet<string>();
        while (r.Read()) outp.Add(r.GetString(0));
        return outp;
    }

    /// <summary>The n strongest players FROM each server in lo..hi, by their newest profile of the last 3 days.</summary>
    public List<(string Uid, int Server)> TopPlayers(int lo, int hi, int n)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"""
            WITH p AS (SELECT uid, home_server, power, ROW_NUMBER() OVER (PARTITION BY uid ORDER BY taken_at DESC) AS rn
                       FROM player_profile WHERE home_server BETWEEN @lo AND @hi AND taken_at >= {Db.AgoIso("3 days")}),
                 q AS (SELECT uid, home_server, ROW_NUMBER() OVER (PARTITION BY home_server ORDER BY power DESC) AS r FROM p WHERE rn = 1)
            SELECT uid, home_server FROM q WHERE r <= @n ORDER BY home_server, r;
            """;
        cmd.Parameters.AddWithValue("@lo", lo);
        cmd.Parameters.AddWithValue("@hi", hi);
        cmd.Parameters.AddWithValue("@n", n);
        using var r = cmd.ExecuteReader();
        var outp = new List<(string, int)>();
        while (r.Read()) outp.Add((r.GetString(0), (int)r.GetInt64(1)));
        return outp;
    }

    /// <summary>A past president never changes, so the first sighting is kept and later ones ignored.</summary>
    public void Insert(IReadOnlyCollection<President> ps, DateTimeOffset seen) =>
        Batch($"INSERT INTO president VALUES ({Values(10)}) ON CONFLICT DO NOTHING;", ps.Select(p => new object[]
        {
            p.Server, p.Round, p.Uid, p.Name, p.Tag, p.AllianceName, T(p.BecameAt), p.OccupyServer, p.Country, seen.ToString("O"),
        }));
}
