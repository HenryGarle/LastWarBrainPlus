using LastWar.Core.Analysis;
using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Operations;

public static partial class Operations
{
    static void AddActivityOperations()
    {
        Add("activity.check", "Scan", "Activity check",
            "Who on a server is being played. A rise in power is the only thing that proves a login "
            + "- a fall proves nothing, since power drops when troops die - and a rise at ANY point in "
            + "the last N days counts, not just against the snapshot before. Change columns are against "
            + "the previous snapshot.",
            c =>
            {
                var db = new LastWarDb(c.Database);
                var server = new ServerId(c.Int("server", 1967));
                var times = db.SnapshotTimes(server);
                if (times.Count < 2)
                    return Task.FromResult(OperationResult.Text(
                        $"server {server} has {times.Count} snapshot(s) - take a power snapshot to compare against"));

                var history = times.Select(t => db.LoadSnapshot(server, t)).ToList();
                var days = c.Int("days", LastWar.Storage.PlayerActivity.WindowDays);
                var results = ActivityAnalyzer.Compare(history, TimeSpan.FromDays(days));
                var gone = ActivityAnalyzer.Disappeared(history[^2], history[^1]);

                var show = c.Text("show", "active");
                var rows = results
                    .Where(r => show == "all" || r.Status.ToString().Equals(show, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.Delta)
                    .Select(r => new object?[]
                    {
                        r.Name, r.AllianceTag, r.Status, r.PreviousPower, r.Power, r.Delta, r.LastRise?.ToString("u"),
                        r.OriginServer?.Value, r.Uid.Value,
                    });

                var counts = string.Join(", ", results.GroupBy(r => r.Status).OrderBy(g => g.Key)
                    .Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"));
                return Task.FromResult(OperationResult.Table(
                    $"active = a rise in the {days} days to {times[^1]:u}: {counts}, {gone.Count} gone since {times[^2]:u}",
                    ["name", "alliance", "status", "before", "now", "change", "last rise", "origin", "uid"], rows));
            },
            needsSession: false,
            ps:
            [
                new OperationParam("server", "Server", "number", "1967", Required: true),
                new OperationParam("days", "Window (days)", "number", "7",
                    Help: "A rise anywhere in this many days before the newest snapshot counts as active."),
                new OperationParam("show", "Show", "text", "active",
                    Help: "active, flat, down, new - or all."),
            ]);
    }
}
