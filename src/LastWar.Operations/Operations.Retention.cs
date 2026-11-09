using LastWar.Storage;

namespace LastWar.Operations;

public static partial class Operations
{
    /// <summary>Retention (Henry 2026-10-09): login moments kept, full snapshots one a day for 14 days then one a week.</summary>
    static void AddRetentionOperations()
    {
        Add("data.thin", "Data", "Thin old snapshots",
            "Keeps full snapshots (profile sweeps, power readings, alliance rosters) one a day for the last 14 days and one a "
            + "week before that; today is never touched. The first run also moves login moments out of the old hourly "
            + "profiles and removes those. No network.",
            c =>
            {
                var r = new Retention(c.Database);
                c.Report("login moments: converting the old pulse profiles (first run only)");
                var pulse = r.Backfill();
                c.Report("thinning snapshots");
                var gone = r.Thin(DateTimeOffset.UtcNow, Math.Clamp(c.Int("dailyDays", Retention.DailyDays), 2, 365));
                if (c.Flag("compact", false)) { c.Report("compacting (VACUUM FULL)"); r.Compact(); }
                var rows = gone.Select(kv => new object?[] { kv.Key, kv.Value }).Prepend(["player_profile (old pulse rows)", pulse]).ToList();
                return Task.FromResult(OperationResult.Table($"{gone.Values.Sum() + pulse:N0} rows removed", ["table", "rows removed"], rows));
            },
            needsSession: false, writes: true,
            ps:
            [
                new OperationParam("dailyDays", "Days kept daily", "number", Retention.DailyDays.ToString()),
                new OperationParam("compact", "Give the space back to the disk", "bool", "false",
                    Help: "VACUUM FULL: each big table is locked while it is rewritten (minutes). Without it the space is reused by new data."),
            ]);
    }
}
