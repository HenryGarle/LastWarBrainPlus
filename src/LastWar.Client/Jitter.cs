namespace LastWar.Client;

/// <summary>
/// Random spread on every wait and schedule, so nothing we do repeats on a fixed beat the game could
/// pick out (Henry, 2026-10-02, after Sadmin was banned). spread 0.3 = anywhere from 70% to 130%.
/// </summary>
public static class Jitter
{
    public static TimeSpan Of(TimeSpan t, double spread = 0.3) =>
        t <= TimeSpan.Zero ? t : t * (1 + (Random.Shared.NextDouble() * 2 - 1) * spread);
}
