using LastWar.Storage;

namespace LastWar.Server;

/// <summary>
/// Pulls rooms' history back to a target date, one room at a time, each on a throwaway guest
/// (chat.backfill.guest with until=). A room is re-tried while it is short of the target and the
/// last attempt still found older messages; it is finished when the target is reached or the
/// game has nothing older. Queue and progress live in memory: a server restart drops the queue,
/// and a rerun resumes below the oldest message held, so nothing is fetched twice.
/// </summary>
public sealed class ChatBackfillJobs(string database, string dataDir, ChatStore store)
{
    public sealed class Job
    {
        public required string Room { get; init; }
        public required string Until { get; init; }
        public string State { get; set; } = "queued";      // queued, running, done, failed, cancelled
        public string Progress { get; set; } = "";
        public string? HeldFrom { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
    }

    const int MaxAttempts = 5;
    readonly object _gate = new();
    readonly List<Job> _jobs = [];
    CancellationTokenSource? _running;
    Task? _worker;

    public object Snapshot() { lock (_gate) return _jobs.Select(j => new { j.Room, j.Until, j.State, j.Progress, j.HeldFrom, j.Attempts, j.StartedAt, j.FinishedAt }).ToList(); }

    public void Enqueue(IEnumerable<string> rooms, string until)
    {
        lock (_gate)
        {
            _jobs.RemoveAll(j => j.State is "done" or "failed" or "cancelled");
            foreach (var room in rooms.Distinct())
                if (!_jobs.Any(j => j.Room == room && j.State is "queued" or "running"))
                    _jobs.Add(new Job { Room = room, Until = until, HeldFrom = store.OldestAt(room) });
            if (_worker is null || _worker.IsCompleted) _worker = Task.Run(RunAsync);
        }
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            foreach (var j in _jobs.Where(j => j.State == "queued")) j.State = "cancelled";
            _running?.Cancel();
        }
    }

    async Task RunAsync()
    {
        while (true)
        {
            Job? job;
            lock (_gate) { job = _jobs.FirstOrDefault(j => j.State == "queued"); if (job is null) return; job.State = "running"; job.StartedAt = DateTimeOffset.UtcNow; _running = new(); }
            var ct = _running.Token;
            var lastError = "";
            try
            {
                while (true)
                {
                    var before = store.OldestAt(job.Room);
                    if (Reached(before, job.Until)) { Finish(job, "done", $"reached {job.Until}"); break; }
                    if (++job.Attempts > MaxAttempts) { Finish(job, "failed", $"stopped after {MaxAttempts} attempts, held from {before} - last: {lastError}"); break; }
                    var ok = false;
                    try
                    {
                        await LastWar.Operations.Operations.RunAsync("chat.backfill.guest", new LastWar.Operations.OperationContext
                        {
                            Args = new Dictionary<string, string> { ["room"] = job.Room, ["until"] = job.Until, ["pages"] = "5000" },
                            Database = database, DataDir = dataDir, Cancellation = ct,
                            Progress = new Progress<string>(m => { job.Progress = m; job.HeldFrom = store.OldestAt(job.Room); }),
                        });
                        ok = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        job.Progress = lastError = $"attempt {job.Attempts}: {ex.Message}";
                        await Task.Delay(TimeSpan.FromSeconds(20), ct);   // a busy database or a refused guest: give it a moment
                    }
                    var after = store.OldestAt(job.Room);
                    job.HeldFrom = after;
                    // A clean run that found nothing older: the game keeps no more of this room.
                    // (A failed run proves nothing - it is simply retried.)
                    if (ok && after == before && !Reached(after, job.Until)) { Finish(job, "done", $"the game has nothing older than {after}"); break; }
                }
            }
            catch (OperationCanceledException) { Finish(job, "cancelled", "cancelled"); }
        }
    }

    static bool Reached(string? heldFrom, string until) => heldFrom is not null && string.CompareOrdinal(heldFrom, until) <= 0;

    void Finish(Job j, string state, string note)
    {
        j.State = state; j.Progress = note; j.FinishedAt = DateTimeOffset.UtcNow; j.HeldFrom = store.OldestAt(j.Room);
    }
}
