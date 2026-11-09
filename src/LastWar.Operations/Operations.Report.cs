using System.Globalization;
using LastWar.Storage;

namespace LastWar.Operations;

public static partial class Operations
{
    /// <summary>
    /// The whole transfer window in one table: every player whose HOME server changed between the
    /// baseline and now, anywhere in the range, with from and to.
    ///
    /// Home is the profile's srcServer (ProfileLookup.HomeOf), never where the base happens to sit.
    /// On teleport days bases move to other servers to fight and serverId follows them, but
    /// srcServer does not - so a teleport is reported as "based elsewhere", not as a move.
    /// </summary>
    static void AddReportOperations()
    {
        Add("migration.report", "Migration", "Migration report (all servers)",
            "Everyone who changed home server across the whole range since the baseline - one table, "
            + "from and to, alliance before and after, power and hero power, with totals. Teleports "
            + "for server wars are NOT moves: the home server comes from the profile's source server, "
            + "not from where the base sits. Takes a fresh profile sweep first (guests, ~4 min) unless "
            + "you turn that off.",
            async c =>
            {
                var (lo, hi) = Range(c.Text("servers", "1957-1988"));
                var baseline = Utc(c.Text("baseline", "2026-09-24 19:05"), "baseline");
                var until = c.Text("until").Length > 0 ? Utc(c.Text("until"), "until") : DateTimeOffset.UtcNow.AddYears(1);

                if (c.Flag("sweep", true))
                {
                    var sweep = await RunAsync("scan.profiles", new OperationContext
                    {
                        Args = new Dictionary<string, string> { ["servers"] = $"{lo}-{hi}" },
                        Database = c.Database, DataDir = c.DataDir, Cancellation = c.Cancellation, Progress = c.Progress,
                    });
                    c.Report(sweep.Summary);
                }

                var profiles = new ProfileStore(c.Database);
                profiles.EnsureSchema();   // applies the one-off srcServer home fix to older rows
                var before = profiles.Latest(null, null, baseline);
                var after = profiles.Latest(null, baseline, until);
                bool In(int s) => s >= lo && s <= hi;

                var moved = after.Values
                    .Where(a => before.TryGetValue(a.Uid, out var b) && b.HomeServer > 0 && a.HomeServer > 0
                                && b.HomeServer != a.HomeServer && (In(b.HomeServer) || In(a.HomeServer)))
                    .Select(a => (B: before[a.Uid], A: a))
                    .OrderBy(x => x.B.HomeServer).ThenBy(x => x.A.HomeServer).ThenByDescending(x => x.A.Power)
                    .ToList();
                // Teleported for a fight: home unchanged, base on another server right now.
                var teleported = after.Values.Count(a => In(a.HomeServer) && a.Visiting
                    && before.TryGetValue(a.Uid, out var b) && b.HomeServer == a.HomeServer);

                var uids = moved.Select(x => x.A.Uid).ToList();
                var hero = new WaveStore(c.Database).LatestHeroPower(uids);
                var applied = new WaveStore(c.Database).LatestApplication(uids);

                string[] cols = ["name", "from", "to", "alliance before", "alliance now", "power before", "power",
                                 "hero power", "based now", "applied to", "last seen at from", "seen at to", "uid"];
                var table = OperationTable.Of($"Moved home server, {lo}-{hi}", cols, moved.Select(x => new object?[]
                {
                    x.A.Name, x.B.HomeServer, x.A.HomeServer, x.B.AllianceTag, x.A.AllianceTag, x.B.Power, x.A.Power,
                    hero.TryGetValue(x.A.Uid, out var h) ? h : null,
                    x.A.Visiting ? x.A.CurrentServer : null,
                    applied.TryGetValue(x.A.Uid, out var ap) ? ap : null,
                    x.B.TakenAt.ToString("u"), x.A.TakenAt.ToString("u"), x.A.Uid,
                })) with
                {
                    Brief = ["name", "from", "to", "alliance before", "alliance now", "power", "hero power"],
                    Totals = ["power before", "power", "hero power"],
                    Links = new Dictionary<string, string> { ["name"] = "player:uid", ["from"] = "server:", ["to"] = "server:" },
                };

                var inside = moved.Count(x => In(x.B.HomeServer) && In(x.A.HomeServer));
                var summary = $"{moved.Count:N0} players changed home server since {baseline:u} "
                    + $"({inside:N0} within {lo}-{hi}, {moved.Count(x => !In(x.A.HomeServer)):N0} left the range, "
                    + $"{moved.Count(x => !In(x.B.HomeServer)):N0} arrived from outside it); "
                    + $"{moved.Sum(x => x.A.Power):N0} power moved. {teleported:N0} more are based on another server "
                    + "right now but still homed where they were - teleports, not counted. Newest profile per player "
                    + $"up to {(c.Text("until").Length > 0 ? until.ToString("u") : "now")}.";
                return new OperationResult(summary, [], []) { Tables = [table] };
            },
            needsSession: false,
            ps:
            [
                new OperationParam("servers", "Servers", "text", "1957-1988", Required: true),
                new OperationParam("baseline", "Baseline (UTC)", "text", "2026-09-24 19:05",
                    Help: "Home before = newest profile at or before this. Pick a moment before the window opened."),
                new OperationParam("until", "Until (UTC)", "text",
                    Help: "Optional. Home now = newest profile up to here - pin it to keep an old window's report fixed."),
                new OperationParam("sweep", "Take a fresh profile sweep first", "bool", "true",
                    Help: "Guests only, about four minutes for 32 servers. Turn off to report on what is already stored."),
            ]);
    }

    static DateTimeOffset Utc(string text, string what) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t : throw new ArgumentException($"{what} must be a UTC time, e.g. 2026-09-24 19:05");
}
