using LastWar.Core.Model;

namespace LastWar.Core.Analysis;

public enum ActivityStatus
{
    /// <summary>Power rose since the previous snapshot. Someone logged in and did something.</summary>
    Active,
    /// <summary>Power unchanged. No evidence either way.</summary>
    Flat,
    /// <summary>Power fell. NOT evidence of inactivity - troops die, buildings get
    /// demolished, a dormant account gets farmed.</summary>
    Down,
    /// <summary>Not present in the previous snapshot.</summary>
    New,
}

public sealed record ActivityResult
{
    public required PlayerUid Uid { get; init; }
    public required ActivityStatus Status { get; init; }
    public required long Power { get; init; }
    public long? PreviousPower { get; init; }
    public long Delta { get; init; }
    public string Name { get; init; } = "";
    public string AllianceTag { get; init; } = "";
    public ServerId? OriginServer { get; init; }
    /// <summary>True if this account rose at ANY step in the supplied history, so a
    /// recovery after a drop still marks the account as one that has shown life.</summary>
    public bool ActiveEver { get; init; }
    /// <summary>When the newest rise was seen (the later reading of the pair), if ever.</summary>
    public DateTimeOffset? LastRise { get; init; }
}

/// <summary>
/// Turns a series of power snapshots into activity flags.
///
/// The rule, and the reason for it: power only ever increases because a human logged
/// in and did something. So ANY rise against the PREVIOUS snapshot is proof of
/// activity. A fall proves nothing at all and is never treated as evidence of
/// dormancy. Each step is judged on its own: 1000 -> 500 is a fall, and the following
/// 500 -> 600 is activity, regardless of the earlier peak of 1000.
/// </summary>
public static class ActivityAnalyzer
{
    public static ActivityStatus Classify(long? previous, long current) => previous switch
    {
        null => ActivityStatus.New,
        var p when current > p => ActivityStatus.Active,
        var p when current == p => ActivityStatus.Flat,
        _ => ActivityStatus.Down,
    };

    /// <param name="snapshots">Snapshots ordered OLDEST first. The last is "now".</param>
    /// <param name="window">
    /// When given, a rise at ANY step inside this window before the newest snapshot marks the
    /// account Active, not only a rise on the last step. Two snapshots an hour apart otherwise
    /// make nearly everyone "flat". Null keeps the strict last-step comparison.
    /// </param>
    public static IReadOnlyList<ActivityResult> Compare(
        IReadOnlyList<IReadOnlyDictionary<PlayerUid, PowerSample>> snapshots, TimeSpan? window = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(snapshots.Count, 2);

        // Rising at any single step is enough to have shown life, so this is computed
        // step by step rather than against an all-time peak.
        // Each step compares an account's reading with its OWN previous reading, wherever that
        // was - an account missing from one snapshot is compared across the gap.
        var lastRise = new Dictionary<PlayerUid, DateTimeOffset>();
        var last = new Dictionary<PlayerUid, PowerSample>();
        foreach (var snap in snapshots)
            foreach (var (uid, now) in snap)
            {
                if (last.TryGetValue(uid, out var before) && now.Power > before.Power) lastRise[uid] = now.TakenAt;
                last[uid] = now;
            }

        var previous = snapshots[^2];
        var current = snapshots[^1];
        var results = new List<ActivityResult>(current.Count);
        foreach (var (uid, now) in current)
        {
            long? before = previous.TryGetValue(uid, out var p) ? p.Power : null;
            var rose = lastRise.TryGetValue(uid, out var r) ? r : (DateTimeOffset?)null;
            var status = Classify(before, now.Power);
            if (window is { } w && rose is { } at && at >= now.TakenAt - w) status = ActivityStatus.Active;
            results.Add(new ActivityResult
            {
                Uid = uid,
                Status = status,
                LastRise = rose,
                Power = now.Power,
                PreviousPower = before,
                Delta = before is { } b ? now.Power - b : 0,
                Name = now.Name,
                AllianceTag = now.AllianceTag,
                OriginServer = uid.OriginServer,
                ActiveEver = rose is not null,
            });
        }
        return results.OrderByDescending(r => r.Delta).ToList();
    }

    /// <summary>Accounts present in the previous snapshot but missing from the newest.</summary>
    public static IReadOnlyList<PlayerUid> Disappeared(
        IReadOnlyDictionary<PlayerUid, PowerSample> previous,
        IReadOnlyDictionary<PlayerUid, PowerSample> current) =>
        previous.Keys.Where(u => !current.ContainsKey(u)).OrderBy(u => u.Value).ToList();
}
