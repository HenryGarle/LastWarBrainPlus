namespace LastWar.Storage;

/// <summary>
/// Warzone Duel points race, one 1 v 1 week, metric by metric - from what we can read about the two servers, never from
/// the board itself (Henry, 2026-10-09: logged scores are hard to get, so they are for LEARNING the model, not correcting it).
///
/// Rules (Henry, 2026-10-08):
///   - The race runs Monday 00:00 to the end of Friday (UTC, server time); invasion rights are decided at the end of Friday,
///     Saturday is the attack. Monday already counts Sunday's Arms Race 1st places and truck plunders.
///   - Alliance Duel victory 30K and Alliance Duel MVP 6K, both scored the NEXT day (Monday's on Tuesday ... Thursday's on
///     Friday; Friday's would land after the decision) - 4 duel days count.
///   - Desert Storm victory 50K - Friday only, up to two storms per alliance.
///   - Wanted Boss warzone total damage victory 250K for each day won; Wanted Boss 1st place (individual) 150K a day.
///   - Each successful plunder of a hostile warzone truck 100 (as it happens, Sunday on).
///   - 1st place in the Arms Race 2,000 each, scored the next day (Sunday's on Monday ... Thursday's on Friday: 5 days count).
///
/// v1.5 fit (2026-10-09) on the Weekly Results logged Thursday 2026-10-08 (3 pairs, evening; midday ones agree), with each
/// server's strength read the same day:
///   #1967 v #1979: duel 780K/1.02M, MVP 174K/186K, boss damage 750K/0, boss 1st 0/450K, trucks 409.5K/158.4K, arms 730K/454K
///   #1962 v #1972: 1.41M/840K, 234K/186K, 750K/0, 0/450K, 307.6K/173.7K, 724K/568K
///   #1974 v #1978: 720K/990K, 162K/186K, 750K/0, 450K/0, 505.2K/330K, 912K/804K
/// What they taught:
///   - Duel match-ups a day were 20, 25 and 19, not a fixed 20. They follow the alliances with 10+ members active in the last
///     3 days: the mean of the two servers' counts was 20, 23.5 and 19. MVP awards follow the same count.
///   - Who wins the duels does NOT follow strength: matching alliances rank for rank, the stronger side would have won 100%,
///     100% and 47%; it won 43%, 63% and 42%. Even split stays.
///   - Trucks: ~1.35K a day per billion of active hero power on both sides (was 1.44K), and the stronger side takes MORE than
///     its share: active hero power to the power 1.3 fits two pairs of three (#1974 took 60% with equal power - unexplained).
///   - Arms Race: ~3.24K per counted day per billion (was 3.15K), and the split is FLATTER than active hero power: to the
///     power 0.6 fits all three.
///   - Boss damage day: the bigger top-100 hero power, 3 of 3.
///   - Boss 1st place is a contest between a few whales, and damage per hero power varies between players: on the #1967 v
///     #1979 ranking it ran 70-81 per hero power point (angel 11.2G on 142M, Ghost 9.8G on 139M; two #1967 players with 124-125M
///     were not in the top 6). So it is a chance, not a call: each side's 5 strongest active players, damage = hero power x a
///     random spread of 12% (log), and the side whose best hit is highest wins - played out 4,000 times. The plain "bigger
///     top-1 hero" call was right 3 of 4; Henry reported it wrong for #1981 v #1970 (#1970's Genie, 156M, against #1981's best at 139M).
///   - Desert Storm: still unseen (Friday) - match-ups x 2 storms x 50K, split evenly.
/// </summary>
public static class SvsModel
{
    public const double RaceDays = 5;
    /// <summary>v1 = first fit; v1.1 = race timing; v1.2 = the real per-metric values; v1.3 = days counted by when things happen;
    /// v1.4 = paced on logged scores (dropped, Henry 2026-10-09); v1.5 = refit from the logged scores (2026-10-09).</summary>
    public const string Version = "v1.5";
    public static readonly string[] Metrics = ["duel", "mvp", "bossDamage", "boss1st", "trucks", "arms", "desertStorm"];
    public static readonly string[] MetricNames =
        ["Alliance Duel victory", "Alliance Duel MVP", "Wanted Boss warzone damage", "Wanted Boss 1st place", "Truck plunders", "Arms Race 1st", "Desert Storm victory"];

    const double DefaultMatchUps = 20;          // when the alliance counts are unknown
    const double DuelWin = 30_000, Mvp = 6_000, Storm = 50_000;
    const double BossDamagePerDay = 250_000, BossFirstPerDay = 150_000;
    // Sunday counts toward Monday: Arms Race fully, trucks at half - people are often lax on plunders on Sunday (Henry).
    const double SundayTrucks = 0.5;
    const double TrucksPerBillionDay = 1_350, ArmsPerBillionDay = 3_240;
    const double TruckShareExponent = 1.3, ArmsShareExponent = 0.6;
    /// <summary>Spread (log) of boss damage per hero power point between players.</summary>
    public const double DamageSpread = 0.12;
    public const int DuelDays = 4;

    /// <param name="TopHeroes">Hero power of the server's strongest active players (5), for boss 1st place.</param>
    /// <param name="Alliances">Alliances with 10+ members active in the last 3 days: the duel match-ups.</param>
    public sealed record Side(double Top1Hero, double Top100Hero, double ActiveHero, double[]? TopHeroes = null, double Alliances = 0);

    /// <summary>Alliance Duel match-ups a day: the mean of the two servers' alliances with 10+ active members.</summary>
    public static double MatchUps(Side a, Side b) => a.Alliances + b.Alliances > 0 ? (a.Alliances + b.Alliances) / 2 : DefaultMatchUps;

    /// <summary>
    /// Chance that side A has the day's top boss damage: each side's strongest active players, damage = hero power x
    /// exp(N(0, DamageSpread)), best hit wins. Fixed seed, so the same inputs always give the same answer.
    /// </summary>
    public static double FirstPlaceChance(Side a, Side b)
    {
        double[] Heroes(Side s) => s.TopHeroes is { Length: > 0 } h ? h : s.Top1Hero > 0 ? [s.Top1Hero] : [];
        var (x, y) = (Heroes(a), Heroes(b));
        if (x.Length == 0 || y.Length == 0) return x.Length > 0 ? 1 : y.Length > 0 ? 0 : 0.5;
        var rng = new Random(87);
        double Normal() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        double Best(double[] h) { var m = 0.0; foreach (var v in h) m = Math.Max(m, v * Math.Exp(DamageSpread * Normal())); return m; }
        const int Draws = 4000;
        var wins = 0;
        for (var i = 0; i < Draws; i++) if (Best(x) > Best(y)) wins++;
        return (double)wins / Draws;
    }

    static double Share(double x, double y, double exponent)
    {
        var (px, py) = (Math.Pow(Math.Max(x, 0), exponent), Math.Pow(Math.Max(y, 0), exponent));
        return px + py <= 0 ? 0.5 : px / (px + py);
    }

    /// <summary>Both sides' points given the counted units: duel/MVP days, boss days, truck days, Arms Race days, storm (0/1).</summary>
    static (double[] A, double[] B) Points(Side a, Side b, double duelDays, double bossDays, double truckDays, double armsDays, bool storm)
    {
        var activeB = (a.ActiveHero + b.ActiveHero) / 1e9;
        var matchUps = MatchUps(a, b);
        var damageA = a.Top100Hero >= b.Top100Hero;
        var first = FirstPlaceChance(a, b);
        var trucks = Share(a.ActiveHero, b.ActiveHero, TruckShareExponent);
        var arms = Share(a.ActiveHero, b.ActiveHero, ArmsShareExponent);
        double[] Side(bool isA) =>
        [
            DuelWin * matchUps * duelDays * 0.5,
            Mvp * matchUps * duelDays * 0.5,
            damageA == isA ? BossDamagePerDay * bossDays : 0,
            BossFirstPerDay * bossDays * (isA ? first : 1 - first),
            TrucksPerBillionDay * activeB * truckDays * (isA ? trucks : 1 - trucks),
            ArmsPerBillionDay * activeB * armsDays * (isA ? arms : 1 - arms),
            storm ? Storm * matchUps * 2 * 0.5 : 0,
        ];
        return (Side(true), Side(false));
    }

    /// <summary>
    /// Points earned by the end of race day <paramref name="day"/> (1 = Monday .. 5 = Friday), counted by the day things HAPPEN
    /// (Henry, 2026-10-08) rather than the day the board shows them: day 1 has Monday's duel, MVP, bosses, Arms Race and trucks,
    /// plus Sunday's Arms Race and trucks. Friday's duel, MVP and Arms Race would be scored on Saturday - after the decision -
    /// so day 5 adds only the bosses, trucks and Desert Storm. Day 5 is the final score.
    /// </summary>
    public static (double[] A, double[] B) EndOfDay(Side a, Side b, int day)
    {
        var n = Math.Clamp(day, 1, (int)RaceDays);
        return Points(a, b, Math.Min(n, DuelDays), n, n + SundayTrucks, Math.Min(n + 1, 5), n == RaceDays);
    }

    /// <summary>Points each side should have on the BOARD after <paramref name="days"/> of the race (0 = Monday 00:00, 5 = end of Friday).</summary>
    public static (double[] A, double[] B) Predict(Side a, Side b, double days)
    {
        var d = Math.Clamp(days, 0, RaceDays);
        var whole = Math.Floor(d);                       // days fully over
        // duel/MVP scored the next day (Monday..Thursday); bosses at the end of each day; Arms Race Sunday..Thursday, each
        // scored the next day; trucks as they happen from Sunday (half); Friday's Desert Storms at the end of Friday.
        return Points(a, b, Math.Min(whole, DuelDays), whole, d + SundayTrucks, Math.Min(whole + 1, 5), d >= RaceDays);
    }
}
