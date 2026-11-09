namespace LastWar.Storage;

/// <summary>
/// Alliance vs alliance fight over ONE building the whole defending side garrisons (season war
/// buildings, e.g. the Season 3 Alliance Center: 100 reinforcement slots; secondary buildings 20).
/// Built from what the season guides describe (researched 2026-09-28, see AGENTS.md):
///  - each defender can have 1 squad in the building at a time; the garrison must be beaten before
///    the building takes damage;
///  - a march stops after beating 5 garrison squads, even with troops left;
///  - attackers land in timed waves: everyone's strongest squad at once, then their 2nd, then 3rd;
///  - between waves, defenders who are ON can re-garrison with their next squad.
/// Each duel is Lanchester's square law (damage is linear in troops): the stronger squad wins and
/// keeps sqrt(a^2 - d^2) of its strength, so a strong march can chew through several weaker squads.
/// Counters, morale and gear are unseen: each duel gets a small random swing (Luck) and each whole
/// fight one shared swing for the attacking side (SideLuck). Monte Carlo over who turns up and that luck; "breaks" = the chance the garrison
/// is empty after the last wave (the building is open to damage).
/// ponytail: three waves stand in for a 20-40 minute war of heal-and-resend; squad shares of hero
/// power are a guess (strongest squad ~34%, next 26%, 20%) - both sides get the same rules, so the
/// comparison holds even where the absolute numbers are rough.
/// </summary>
public static class Siege
{
    /// <summary>Hero = estimated hero power. Present = chance their first squad is already in the
    /// building when it starts (garrisoned beforehand). Online = chance they are on during it.</summary>
    public sealed record Member(double Hero, double Present, double Online);

    public sealed record Result(double Breaks, double GarrisonLeft, double AttackersOn, double DefendersIn);

    public static readonly double[] SquadShare = [0.34, 0.26, 0.20];
    public const int MarchCap = 5;
    public const double Luck = 0.2;
    /// <summary>
    /// One swing per fight for the whole attacking side against the whole defending side: counters,
    /// gear, buffs and coordination are shared by an alliance, so they do not average out over many
    /// duels the way per-duel luck does. 0.25 = about a type counter's worth (guides: ~40% swing).
    /// </summary>
    public const double SideLuck = 0.25;

    public static Result Run(IReadOnlyList<Member> attackers, IReadOnlyList<Member> defenders, int slots, int sims = 400, int seed = 1)
    {
        var rng = new Random(seed);
        double Normal() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var side = 1.0;
        double Swing() => side * Math.Exp(Luck * Normal());
        int breaks = 0;
        double left = 0, atkOnSum = 0, defInSum = 0;
        var nextSquad = new int[defenders.Count];
        var inside = new bool[defenders.Count];
        var defOn = new bool[defenders.Count];
        var garrison = new List<(double S, int Who)>();
        for (var sim = 0; sim < sims; sim++)
        {
            side = Math.Exp(SideLuck * Normal());
            var atk = attackers.Where(m => rng.NextDouble() < m.Online).ToList();
            atkOnSum += atk.Count;
            garrison.Clear();
            var start = new List<(double S, int Who)>();
            for (var i = 0; i < defenders.Count; i++)
            {
                var u = rng.NextDouble();   // one draw: someone who is on has also garrisoned
                defOn[i] = u < defenders[i].Online;
                var present = u < Math.Max(defenders[i].Present, defenders[i].Online);
                nextSquad[i] = present ? 1 : 0;
                inside[i] = false;
                if (present) start.Add((defenders[i].Hero * SquadShare[0], i));
            }
            // Over capacity, the strongest get the slots; the fighting order inside is unknown, so shuffled.
            foreach (var g in start.OrderByDescending(x => x.S).Take(slots).OrderBy(_ => rng.Next()))
            { garrison.Add(g); inside[g.Who] = true; }
            foreach (var g in start.Where(g => !inside[g.Who])) nextSquad[g.Who] = 0;   // no slot: squad stays home
            defInSum += garrison.Count;

            for (var w = 0; w < SquadShare.Length; w++)
            {
                foreach (var s0 in atk.Select(m => m.Hero * SquadShare[w]).OrderDescending())
                {
                    var s = s0;
                    for (var beaten = 0; s > 0 && beaten < MarchCap && garrison.Count > 0;)
                    {
                        var eff = s * Swing();
                        var (d, who) = garrison[0];
                        if (eff > d) { s = Math.Sqrt(eff * eff - d * d); garrison.RemoveAt(0); inside[who] = false; beaten++; }
                        else { garrison[0] = (Math.Sqrt(d * d - eff * eff), who); s = 0; }
                    }
                    if (garrison.Count == 0) break;
                }
                if (w == SquadShare.Length - 1) break;
                // Re-garrison: defenders who are on send their next squad, one squad each, while slots last.
                for (var i = 0; i < defenders.Count && garrison.Count < slots; i++)
                    if (defOn[i] && !inside[i] && nextSquad[i] < SquadShare.Length)
                    { garrison.Add((defenders[i].Hero * SquadShare[nextSquad[i]], i)); inside[i] = true; nextSquad[i]++; }
            }
            if (garrison.Count == 0) breaks++;
            left += garrison.Count;
        }
        return new(breaks / (double)sims, left / sims, atkOnSum / sims, defInSum / sims);
    }
}
