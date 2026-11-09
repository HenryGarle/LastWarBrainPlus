using Npgsql;

namespace LastWar.Storage;

/// <summary>
/// Hero power for EVERY player, not just a server's top 200: hero power = k x (power - armyPower),
/// both from the player's profile (get.user.info.multi). armyPower is their troops; what is left is
/// buildings, tech and heroes, and the heroes are a near-constant share of it.
///
/// Back-tested 2026-09-28 on all 32 servers' top-200 hero boards (6,198 players, profile taken
/// within 2 h of the board): k = 0.780 (per server 0.773-0.788); median error 1.0%, 90% within 2.4%,
/// worst 7%; the same across ranks 1-20 and 181-200 and for players with no troops. For comparison,
/// power x a fixed share missed by 10.6%, max power seen 7.8%, min power seen 10.7%.
///
/// hero_fit is DERIVED (rebuilt with the other histories): k per server and overall (server 0), and
/// how well it did on that server's newest board - so the estimate re-calibrates itself if the game
/// changes the formula, and the back-test is always visible.
/// </summary>
public sealed class HeroModel(string path)
{
    public const double DefaultK = 0.78;

    NpgsqlConnection Open() => Db.Open(path);

    public void EnsureSchema()
    {
        if (!Db.FirstTime(path, "HeroModel")) return;   // DDL once per process: it takes table locks
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS hero_fit (
                server BIGINT NOT NULL PRIMARY KEY, k DOUBLE PRECISION NOT NULL, players BIGINT NOT NULL,
                median_err DOUBLE PRECISION NOT NULL, p90_err DOUBLE PRECISION NOT NULL, worst_err DOUBLE PRECISION NOT NULL,
                bottom20_err DOUBLE PRECISION NOT NULL, bias DOUBLE PRECISION NOT NULL, board_at TEXT NOT NULL, fitted_at TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>One board row matched to the profile taken nearest it (within 3 h).</summary>
    public readonly record struct Pair(int Server, int Rank, long Hero, long Power, long Army);

    public List<Pair> Pairs()
    {
        using var c = Open();
        var outp = new List<Pair>();
        // Each server's newest hero board, each row with the profile taken nearest it (uid leads
        // player_profile's key, so the lateral lookup is a few thousand index probes).
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT b.server_id, b.rank, b.value, p.power, p.army_power FROM board_sample b
            JOIN LATERAL (
                SELECT power, army_power FROM player_profile p
                WHERE p.uid = b.uid AND p.taken_at::timestamptz BETWEEN b.taken_at::timestamptz - interval '3 hours'
                                                             AND b.taken_at::timestamptz + interval '3 hours'
                ORDER BY abs(extract(epoch FROM p.taken_at::timestamptz - b.taken_at::timestamptz)) LIMIT 1) p ON true
            WHERE b.board_type = 13 AND b.taken_at = (SELECT MAX(taken_at) FROM board_sample x WHERE x.board_type = 13 AND x.server_id = b.server_id);
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (r.GetInt64(3) > r.GetInt64(4)) outp.Add(new Pair((int)r.GetInt64(0), (int)r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));
        return outp;
    }

    static double Median(IEnumerable<double> xs) { var s = xs.Order().ToList(); return s.Count == 0 ? 0 : s[s.Count / 2]; }

    public int Rebuild()
    {
        EnsureSchema();
        var pairs = Pairs();
        if (pairs.Count == 0) return 0;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM hero_fit;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO hero_fit VALUES (@s,@k,@n,@m,@p,@w,@b20,@bias,@at,@now);";
        var now = DateTimeOffset.UtcNow.ToString("O");
        var overall = Median(pairs.Select(p => (double)p.Hero / (p.Power - p.Army)));
        foreach (var g in pairs.GroupBy(p => p.Server).Append(pairs.GroupBy(_ => 0).First()))
        {
            // The server's own k predicts others' boards; each server is scored with the OVERALL k so the
            // back-test never grades a fit on the very rows it was fitted to.
            var k = g.Key == 0 ? overall : Median(g.Select(p => (double)p.Hero / (p.Power - p.Army)));
            var errs = g.Select(p => (overall * (p.Power - p.Army) - p.Hero) / p.Hero).ToList();
            var abs = errs.Select(Math.Abs).Order().ToList();
            var bottom = g.Where(p => p.Rank > 180).Select(p => Math.Abs((overall * (p.Power - p.Army) - p.Hero) / p.Hero));
            foreach (var (n, v) in new (string, object)[] { ("@s", g.Key), ("@k", k), ("@n", abs.Count), ("@m", Median(abs)),
                         ("@p", abs[(int)(abs.Count * 0.9)]), ("@w", abs[^1]), ("@b20", Median(bottom)), ("@bias", Median(errs)),
                         ("@at", ""), ("@now", now) })
                cmd.Parameters.AddWithValue(n, v);
            cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();
        }
        tx.Commit();
        return pairs.Count;
    }

    /// <summary>
    /// Below this total power the model has no ground truth: the smallest accounts on any server's
    /// top-200 hero board have ~80M power (median ~133M). Small accounts are mostly buildings, and
    /// servers are full of small abandoned ones, so under it the estimate is TAPERED DOWN by
    /// sqrt(power / 80M) - 5M power counts a quarter, 20M half, 80M+ in full. Deliberately low:
    /// they rarely fight, and an overstated tail made crowded dead servers look strong.
    /// </summary>
    public const long TaperPower = 80_000_000;

    /// <summary>The estimate as SQL, for any power / troop-power columns and a k expression.</summary>
    public static string EstimateSql(string power, string army, string k) =>
        $"CAST(({k}) * GREATEST({power} - {army}, 0) * LEAST(1.0, sqrt(GREATEST({power}, 0) / {TaperPower}.0)) AS BIGINT)";

    /// <summary>k for a server (falls back to overall, then the constant found on 2026-09-28).</summary>
    public static string KSql(string server) =>
        $"COALESCE((SELECT k FROM hero_fit WHERE server = {server}), (SELECT k FROM hero_fit WHERE server = 0), {DefaultK})";
}
