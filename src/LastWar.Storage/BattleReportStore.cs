using System.Text.Json;
using LastWar.Core.Model;

namespace LastWar.Storage;

/// <summary>
/// Battle reports people share with our accounts by DM, kept to learn what wins fights.
///
///   battle_report_share  every share seen in a private chat (who shared which report, where, when)
///   battle_report        one row per report: the game's own protobuf bytes (raw, so a newer schema can
///                        re-read them), the decoded JSON, and a fetch error if it failed
///   battle_fight         one row per side of each fight in a report: factors and outcome as JSON maps.
///                        Derived from battle_report and rebuilt at will (RebuildFights), so the analysis can
///                        grow new measures over time.
/// </summary>
public sealed class BattleReportStore(string path)
{
    public sealed record Share(string ReportUid, string OwnerUid, string RoomId, long SeqId, string SharedBy, string SharedByName,
        DateTimeOffset SharedAt, int MailType, int ResultState, string Runner);

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "BattleReportStore")) return;
        Db.Exec(path, """
            CREATE TABLE IF NOT EXISTS battle_report_share (
                report_uid TEXT NOT NULL, owner_uid TEXT NOT NULL, room_id TEXT NOT NULL, seq_id BIGINT NOT NULL,
                shared_by TEXT NOT NULL DEFAULT '', shared_by_name TEXT NOT NULL DEFAULT '', shared_at TEXT NOT NULL,
                mail_type BIGINT NOT NULL DEFAULT 0, result_state BIGINT NOT NULL DEFAULT 0, runner TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (report_uid, room_id, seq_id));
            CREATE TABLE IF NOT EXISTS battle_report (
                report_uid TEXT PRIMARY KEY, owner_uid TEXT NOT NULL, mail_type BIGINT NOT NULL DEFAULT 0,
                uuid TEXT NOT NULL DEFAULT '', battle_type BIGINT NOT NULL DEFAULT 0, battle_time TEXT NOT NULL DEFAULT '',
                address TEXT NOT NULL DEFAULT '', fetched_at TEXT NOT NULL, raw BYTEA, json TEXT NOT NULL DEFAULT '', error TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS battle_fight (
                report_uid TEXT NOT NULL, fight BIGINT NOT NULL, side BIGINT NOT NULL, won BOOLEAN NOT NULL,
                uid TEXT NOT NULL DEFAULT '', name TEXT NOT NULL DEFAULT '', tag TEXT NOT NULL DEFAULT '', server BIGINT NOT NULL DEFAULT 0,
                factors TEXT NOT NULL, outcome TEXT NOT NULL, heroes TEXT NOT NULL,
                PRIMARY KEY (report_uid, fight, side));
            """);
    }

    static string Now() => DateTimeOffset.UtcNow.ToString("O");

    /// <summary>Stores shares not seen before; returns how many were new.</summary>
    public int AddShares(IEnumerable<Share> shares)
    {
        EnsureSchema();
        var n = 0;
        foreach (var s in shares)
            n += Db.Exec(path, """
                INSERT INTO battle_report_share (report_uid, owner_uid, room_id, seq_id, shared_by, shared_by_name, shared_at, mail_type, result_state, runner)
                VALUES (@r, @o, @room, @seq, @by, @byn, @at, @mt, @rs, @run) ON CONFLICT DO NOTHING;
                """, ("@r", s.ReportUid), ("@o", s.OwnerUid), ("@room", s.RoomId), ("@seq", s.SeqId), ("@by", s.SharedBy), ("@byn", s.SharedByName),
                ("@at", s.SharedAt.ToString("O")), ("@mt", (long)s.MailType), ("@rs", (long)s.ResultState), ("@run", s.Runner));
        return n;
    }

    /// <summary>Shared reports not fetched yet (or whose fetch failed), oldest share first.</summary>
    public List<(string ReportUid, string OwnerUid)> Pending(int max = 50)
    {
        EnsureSchema();
        using var c = Db.Open(path);
        return Db.Rows(c, """
            SELECT s.report_uid, MIN(s.owner_uid) AS owner FROM battle_report_share s
            LEFT JOIN battle_report r ON r.report_uid = s.report_uid
            WHERE r.report_uid IS NULL OR (r.json = '' AND r.fetched_at < @retry)
            GROUP BY s.report_uid ORDER BY MIN(s.shared_at) LIMIT @n;
            """, ("@retry", DateTimeOffset.UtcNow.AddHours(-6).ToString("O")), ("@n", (long)max))
            .Select(r => ((string)r["report_uid"]!, (string)r["owner"]!)).ToList();
    }

    public void SaveReport(string reportUid, string ownerUid, int mailType, string uuid, int battleType, DateTimeOffset? battleTime,
        string address, byte[]? raw, string json, IReadOnlyList<FightSide> fights, string error = "")
    {
        EnsureSchema();
        using var c = Db.Open(path);
        using var tx = c.BeginTransaction();
        void Run(string sql, params (string, object?)[] ps) { using var cmd = Db.Command(c, sql, ps); cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
        Run("""
            INSERT INTO battle_report (report_uid, owner_uid, mail_type, uuid, battle_type, battle_time, address, fetched_at, raw, json, error)
            VALUES (@r, @o, @mt, @u, @bt, @time, @a, @now, @raw, @json, @err)
            ON CONFLICT (report_uid) DO UPDATE SET owner_uid = @o, mail_type = @mt, uuid = @u, battle_type = @bt, battle_time = @time,
                address = @a, fetched_at = @now, raw = COALESCE(@raw, battle_report.raw), json = CASE WHEN @json = '' THEN battle_report.json ELSE @json END, error = @err;
            """, ("@r", reportUid), ("@o", ownerUid), ("@mt", (long)mailType), ("@u", uuid), ("@bt", (long)battleType),
            ("@time", battleTime?.ToString("O") ?? ""), ("@a", address), ("@now", Now()), ("@raw", raw), ("@json", json), ("@err", error));
        if (fights.Count > 0) Run("DELETE FROM battle_fight WHERE report_uid = @r;", ("@r", reportUid));
        foreach (var f in fights)
            Run("""
                INSERT INTO battle_fight (report_uid, fight, side, won, uid, name, tag, server, factors, outcome, heroes)
                VALUES (@r, @f, @s, @w, @uid, @n, @t, @srv, @fa, @o, @h);
                """, ("@r", reportUid), ("@f", (long)f.Fight), ("@s", (long)f.Side), ("@w", f.Won), ("@uid", f.Uid), ("@n", f.Name), ("@t", f.Tag),
                ("@srv", (long)f.Server), ("@fa", JsonSerializer.Serialize(f.Factors)), ("@o", JsonSerializer.Serialize(f.Outcome)), ("@h", JsonSerializer.Serialize(f.Heroes)));
        tx.Commit();
    }

    /// <summary>Every stored report's raw bytes, to rebuild battle_fight after the reading changes.</summary>
    public List<(string ReportUid, byte[] Raw)> Raws()
    {
        EnsureSchema();
        using var c = Db.Open(path);
        return Db.Rows(c, "SELECT report_uid, raw FROM battle_report WHERE raw IS NOT NULL;").Select(r => ((string)r["report_uid"]!, (byte[])r["raw"]!)).ToList();
    }

    public void ReplaceFights(string reportUid, IReadOnlyList<FightSide> fights)
    {
        EnsureSchema();
        using var c = Db.Open(path);
        using var tx = c.BeginTransaction();
        using (var d = Db.Command(c, "DELETE FROM battle_fight WHERE report_uid = @r;", ("@r", reportUid))) { d.Transaction = tx; d.ExecuteNonQuery(); }
        foreach (var f in fights)
        {
            using var cmd = Db.Command(c, """
                INSERT INTO battle_fight (report_uid, fight, side, won, uid, name, tag, server, factors, outcome, heroes)
                VALUES (@r, @f, @s, @w, @uid, @n, @t, @srv, @fa, @o, @h);
                """, ("@r", reportUid), ("@f", (long)f.Fight), ("@s", (long)f.Side), ("@w", f.Won), ("@uid", f.Uid), ("@n", f.Name), ("@t", f.Tag),
                ("@srv", (long)f.Server), ("@fa", JsonSerializer.Serialize(f.Factors)), ("@o", JsonSerializer.Serialize(f.Outcome)), ("@h", JsonSerializer.Serialize(f.Heroes)));
            cmd.Transaction = tx; cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Every report with its shares and its fights' sides (no JSON), newest battle first.</summary>
    public List<Dictionary<string, object?>> List()
    {
        EnsureSchema();
        using var c = Db.Open(path);
        var reports = Db.Rows(c, """
            SELECT s.report_uid AS "reportUid", MIN(s.owner_uid) AS owner, MIN(s.mail_type) AS "mailType", MIN(s.shared_at) AS "sharedAt",
                   string_agg(DISTINCT s.shared_by_name, ', ') AS "sharedBy", r.uuid, r.battle_type AS "battleType",
                   NULLIF(r.battle_time, '') AS "battleTime", r.fetched_at AS "fetchedAt", NULLIF(r.error, '') AS error, (r.json <> '') AS fetched
            FROM battle_report_share s LEFT JOIN battle_report r ON r.report_uid = s.report_uid
            GROUP BY s.report_uid, r.uuid, r.battle_type, r.battle_time, r.fetched_at, r.error, r.json
            ORDER BY COALESCE(NULLIF(r.battle_time, ''), MIN(s.shared_at)) DESC;
            """);
        var sides = Db.Rows(c, "SELECT report_uid, fight, side, won, uid, name, tag, server, factors FROM battle_fight ORDER BY report_uid, fight, side;")
            .GroupBy(r => (string)r["report_uid"]!).ToDictionary(g => g.Key, g => g.Select(r => new Dictionary<string, object?>
            {
                ["fight"] = r["fight"], ["side"] = r["side"], ["won"] = r["won"], ["uid"] = r["uid"], ["name"] = r["name"], ["tag"] = r["tag"], ["server"] = r["server"],
                ["heroPower"] = JsonSerializer.Deserialize<Dictionary<string, double>>((string)r["factors"]!)?.GetValueOrDefault("heroPower"),
            }).ToList());
        foreach (var r in reports) r["sides"] = sides.GetValueOrDefault((string)r["reportUid"]!) ?? [];
        return reports;
    }

    public Dictionary<string, object?>? Get(string reportUid)
    {
        EnsureSchema();
        using var c = Db.Open(path);
        var r = Db.Rows(c, """
            SELECT report_uid AS "reportUid", owner_uid AS owner, mail_type AS "mailType", uuid, battle_type AS "battleType",
                   NULLIF(battle_time, '') AS "battleTime", fetched_at AS "fetchedAt", NULLIF(error, '') AS error, json
            FROM battle_report WHERE report_uid = @r;
            """, ("@r", reportUid)).FirstOrDefault();
        if (r is null) return null;
        r["fights"] = Db.Rows(c, "SELECT fight, side, won, uid, name, tag, server, factors, outcome, heroes FROM battle_fight WHERE report_uid = @r ORDER BY fight, side;", ("@r", reportUid))
            .Select(f => new Dictionary<string, object?>
            {
                ["fight"] = f["fight"], ["side"] = f["side"], ["won"] = f["won"], ["uid"] = f["uid"], ["name"] = f["name"], ["tag"] = f["tag"], ["server"] = f["server"],
                ["factors"] = JsonSerializer.Deserialize<Dictionary<string, double>>((string)f["factors"]!),
                ["outcome"] = JsonSerializer.Deserialize<Dictionary<string, double>>((string)f["outcome"]!),
                ["heroes"] = JsonSerializer.Deserialize<List<FightHero>>((string)f["heroes"]!),
            }).ToList();
        r["shares"] = Db.Rows(c, "SELECT room_id AS room, seq_id AS seq, shared_by AS \"sharedBy\", shared_by_name AS \"sharedByName\", shared_at AS \"sharedAt\", runner FROM battle_report_share WHERE report_uid = @r;", ("@r", reportUid));
        return r;
    }

    /// <summary>Labels, not amounts: never compared as "more".</summary>
    static readonly HashSet<string> Categorical = ["mainType"];

    public sealed record FactorResult(string Factor, int Fights, int WinnerHigher, int WinnerLower, int Equal, double MedianRatio, double WinnerHigherShare);

    /// <summary>
    /// What winners had that losers did not: for every factor, across every fight with both sides known,
    /// how often the winner had MORE of it, and the median winner/loser ratio. A factor that decides fights
    /// sits far from 50%; with few fights everything is noise, so the counts are returned with it.
    /// </summary>
    /// <summary>Line-up measures with few values: the win rate of every side at each value (does a type advantage win?).</summary>
    public static readonly string[] SplitBy = ["typeAdvantage", "formationBonus", "frontDamageDealers", "frontShieldedByChip", "droneChipsMatchingType", "mainType"];

    public List<Dictionary<string, object?>> Splits()
    {
        EnsureSchema();
        using var c = Db.Open(path);
        var sides = Db.Rows(c, "SELECT won, factors FROM battle_fight;")
            .Select(r => (Won: r["won"] is true, F: JsonSerializer.Deserialize<Dictionary<string, double>>((string)r["factors"]!)!)).ToList();
        return [.. SplitBy.Select(k => new Dictionary<string, object?>
        {
            ["factor"] = k,
            ["values"] = sides.Where(s => s.F.ContainsKey(k)).GroupBy(s => s.F[k]).OrderBy(g => g.Key)
                .Select(g => new Dictionary<string, object?> { ["value"] = g.Key, ["sides"] = g.Count(), ["wins"] = g.Count(s => s.Won) }).ToList(),
        })];
    }

    public (List<FactorResult> Factors, List<Dictionary<string, object?>> Upsets, int Fights) Analysis()
    {
        EnsureSchema();
        using var c = Db.Open(path);
        var rows = Db.Rows(c, "SELECT report_uid, fight, side, won, name, tag, factors FROM battle_fight;");
        var pairs = rows.GroupBy(r => ((string)r["report_uid"]!, Convert.ToInt64(r["fight"])))
            .Select(g => (Win: g.FirstOrDefault(r => r["won"] is true), Lose: g.FirstOrDefault(r => r["won"] is false), Key: g.Key))
            .Where(p => p.Win is not null && p.Lose is not null)
            .Select(p => (p.Key, Win: p.Win!, Lose: p.Lose!,
                W: JsonSerializer.Deserialize<Dictionary<string, double>>((string)p.Win!["factors"]!)!,
                L: JsonSerializer.Deserialize<Dictionary<string, double>>((string)p.Lose!["factors"]!)!))
            .ToList();
        var factors = pairs.SelectMany(p => p.W.Keys.Intersect(p.L.Keys)).Distinct().Where(k => !Categorical.Contains(k)).Select(k =>
        {
            var both = pairs.Where(p => p.W.ContainsKey(k) && p.L.ContainsKey(k) && (p.W[k] != 0 || p.L[k] != 0)).ToList();
            int hi = both.Count(p => p.W[k] > p.L[k]), lo = both.Count(p => p.W[k] < p.L[k]);
            var ratios = both.Where(p => p.L[k] > 0).Select(p => p.W[k] / p.L[k]).Order().ToList();
            var median = ratios.Count == 0 ? 0 : ratios.Count % 2 == 1 ? ratios[ratios.Count / 2] : (ratios[ratios.Count / 2 - 1] + ratios[ratios.Count / 2]) / 2;
                        // Share among the fights where the sides differed: always-equal measures (same hero level cap) say nothing.
            return new FactorResult(k, both.Count, hi, lo, both.Count - hi - lo, Math.Round(median, 3), hi + lo == 0 ? 0.5 : Math.Round((double)hi / (hi + lo), 3));
        }).Where(f => f.Fights > 0).OrderByDescending(f => Math.Abs(f.WinnerHigherShare - 0.5) * Math.Sqrt(f.WinnerHigher + f.WinnerLower)).ThenByDescending(f => f.Fights).ToList();
        // Upsets: the side with less hero power won - the fights that say most about everything else.
        var upsets = pairs.Where(p => p.W.GetValueOrDefault("heroPower") < p.L.GetValueOrDefault("heroPower")).Select(p => new Dictionary<string, object?>
        {
            ["reportUid"] = p.Key.Item1, ["fight"] = p.Key.Item2, ["winner"] = p.Win["name"], ["winnerTag"] = p.Win["tag"], ["loser"] = p.Lose["name"], ["loserTag"] = p.Lose["tag"],
            ["heroRatio"] = p.L.GetValueOrDefault("heroPower") > 0 ? Math.Round(p.W.GetValueOrDefault("heroPower") / p.L["heroPower"], 3) : 0,
            ["winnerAhead"] = p.W.Keys.Where(k => p.L.TryGetValue(k, out var l) && p.W[k] > l).ToList(),
        }).ToList();
        return (factors, upsets, pairs.Count);
    }
}
