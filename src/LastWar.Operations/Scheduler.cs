using System.Collections.Concurrent;
using LastWar.Storage;

namespace LastWar.Operations;

/// <summary>
/// Runs operations on a timetable (ScheduleStore holds the jobs and the run history). Any front end
/// can host it - the server does. A job never overlaps itself; pausing stops new runs but lets the
/// current one finish (Stop cancels it). Next due = the start of the run just made + the interval,
/// so an hourly job stays hourly however long a run takes.
/// </summary>
public sealed class Scheduler(string database, string dataDir)
{
    public sealed class Live
    {
        public required long RunId { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public required CancellationTokenSource Stop { get; init; }
        public ConcurrentQueue<string> Lines { get; } = new();
    }

    readonly ScheduleStore _store = new(database);
    readonly ConcurrentDictionary<string, Live> _running = new();
    public ScheduleStore Store => _store;

    /// <summary>
    /// Runs a job on a named account instead of with no session - set by the host, which owns the
    /// runners (the server: a stopped runner is logged in for that one run only). A job names its
    /// account with the arg "runner"; operations that need a session cannot be scheduled without one.
    /// Returns the run's summary.
    /// </summary>
    public Func<string, string, IReadOnlyDictionary<string, string>, IProgress<string>, CancellationToken, Task<string>>? OnRunner { get; set; }

    /// <summary>The standing jobs. Added once; after that the user's edits and pauses win.</summary>
    public void SeedDefaults()
    {
        _store.EnsureSchema();
        _store.Rename("activity-pulse", "Activity pulse (hourly profile check)", "Activity pulse (profile check)");   // its period can change now
        _store.Seed("activity-pulse", "Activity pulse (profile check)", "scan.profiles",
            new() { ["servers"] = "1957-1988", ["changes"] = "true", ["gap"] = "500" }, 60, enabled: true);
        _store.Seed("alliance-rosters", "Alliance rosters and territory (intel.sweep)", "intel.sweep",
            new() { ["servers"] = "1957-1988" }, 360, enabled: false);
        // Needs a real account (guests cannot view other servers' maps): off until one is picked.
        // Off since lw_2's ban (2026-11-02). Re-enabled, a run does 10 servers at ~1 request/s and stops if the game closes the connection.
        _store.Seed("map-refresh", "Map refresh (bases, cities, alliance buildings)", "refresh.map",
            new() { ["servers"] = "1957-1988", ["runner"] = "" }, 1440, enabled: false);   // once a day (Henry 2026-10-08); Maps page has "Rescan maps now"

        // 2026-11-07: which season group (config) every tracked server is on - a guest, ~64 refused requests a day.
        _store.Seed("season-groups", "Season groups (tracked servers)", "servers.season", new() { ["servers"] = "@full" }, 1440, enabled: true);

        // 2026-09-30: every server the game knows, at the level set on the Tracking page.
        var registry = new LastWar.Storage.ServerRegistry(database);
        registry.EnsureSchema();
        new LastWar.Storage.ProfileStore(database).EnsureSchema();   // player_seen / profile_reading exist before any page reads them
        try { registry.IngestTables(Path.Combine(dataDir, "assets", "tables")); } catch (Exception ex) { Console.Error.WriteLine($"game tables: {ex.Message}"); }
        registry.SeedTracking();
        try { new LastWar.Storage.EventCalendar(database).Seed(registry, DateTime.UtcNow); } catch (Exception ex) { Console.Error.WriteLine($"event calendar: {ex.Message}"); }
        // The jobs that named 1957-1988 now follow the Tracking page: whatever is set to "full".
        foreach (var job in _store.Jobs().Where(j => j.Args.GetValueOrDefault("servers") == "1957-1988"))
            _store.SetArg(job.Id, "servers", "@full");
        _store.Seed("server-registry", "Server registry (every server: list, open dates, kings, badges)", "servers.registry",
            new() { ["openTimes"] = "600" }, 1440, enabled: true);
        _store.Seed("server-basic", "Season zones (servers tracked basic or full)", "servers.basic",
            new() { ["servers"] = "@basic" }, 1440, enabled: true);
        // The hourly pulse stores only login moments (player_seen); rankings snapshots, power history, rosters,
        // leaderboards and top-player detail come only from this once-a-day refresh.stats (2026-10-09).
        _store.Seed("stats-refresh", "Daily stats (boards, full profile sweep, rosters, top players)", "refresh.stats",
            new() { ["servers"] = "@full", ["playerDetails"] = "50" }, 1440, enabled: true);
        _store.Seed("game-tables", "Game tables (server wars, event groups, seasons)", "servers.tables", new(), 1440, enabled: true);
        // SvS: keep each day's forecast for every pair being followed, so estimates can be judged against the result.
        _store.Seed("svs-forecast", "SvS forecasts kept daily", "svs.forecast", new(), 1440, enabled: true);
        // Retention (Henry 2026-10-09): full snapshots one a day for 14 days, then one a week; login moments kept.
        _store.Seed("data-thin", "Thin old snapshots (daily 14 days, then weekly)", "data.thin", new(), 1440, enabled: true);
        // Season timeline, season group, war-zone badge servers, transfer group: only an account ON the server can read them.
        _store.Seed("own-server", "Own server season facts (needs an account there)", "servers.own", new() { ["runner"] = "" }, 10080, enabled: false);
    }

    public Live? Running(string jobId) => _running.GetValueOrDefault(jobId);

    public async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var job in _store.Jobs().Where(j => j.Enabled && (j.NextAt is null || j.NextAt <= DateTimeOffset.UtcNow)))
                    Start(job.Id, "schedule");
            }
            catch (Exception ex) { Console.Error.WriteLine($"scheduler: {ex.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(20), ct).ContinueWith(_ => { });
        }
    }

    /// <summary>Starts a run now unless one is already going. Returns false if it was.</summary>
    public bool Start(string jobId, string trigger)
    {
        var job = _store.Jobs().FirstOrDefault(j => j.Id == jobId) ?? throw new KeyNotFoundException(jobId);
        var started = DateTimeOffset.UtcNow;
        var live = new Live { RunId = 0, StartedAt = started, Stop = new CancellationTokenSource() };
        if (!_running.TryAdd(jobId, live)) return false;
        var runId = _store.StartRun(jobId, trigger);
        live = new Live { RunId = runId, StartedAt = started, Stop = live.Stop };
        _running[jobId] = live;
        _store.SetNext(jobId, started + LastWar.Client.Jitter.Of(TimeSpan.FromMinutes(job.EveryMinutes), 0.2));   // +-20%, never a fixed beat

        var lastSaved = DateTimeOffset.MinValue;
        var progress = new Progress<string>(line =>
        {
            live.Lines.Enqueue($"{DateTimeOffset.Now:HH:mm:ss} {line}");   // server-local, like the rest of the page
            while (live.Lines.Count > 200) live.Lines.TryDequeue(out _);
            if (DateTimeOffset.UtcNow - lastSaved > TimeSpan.FromSeconds(10)) { lastSaved = DateTimeOffset.UtcNow; _store.Progress(runId, line); }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                string summary;
                var runner = job.Args.GetValueOrDefault("runner", "");
                if (runner.Length > 0)
                {
                    if (OnRunner is null) throw new InvalidOperationException("this host cannot run jobs on an account");
                    var args = job.Args.Where(a => a.Key != "runner").ToDictionary(a => a.Key, a => a.Value);
                    summary = await OnRunner(runner, job.Op, args, progress, live.Stop.Token);
                }
                else if (Operations.Find(job.Op) is { NeedsSession: true })
                    throw new InvalidOperationException("pick the account to run this on (Scheduled page)");
                else
                    summary = (await Operations.RunAsync(job.Op, new OperationContext
                    {
                        Args = job.Args, Database = database, DataDir = dataDir, Progress = progress, Cancellation = live.Stop.Token,
                    })).Summary;
                _store.FinishRun(runId, "done", summary);
            }
            catch (OperationCanceledException) { _store.FinishRun(runId, "stopped", "stopped by hand"); }
            catch (Exception ex) { _store.FinishRun(runId, "failed", ex.Message); }
            finally { _running.TryRemove(jobId, out _); }
        });
        return true;
    }

    public bool Stop(string jobId)
    {
        if (!_running.TryGetValue(jobId, out var live)) return false;
        live.Stop.Cancel();
        return true;
    }

    /// <summary>Resuming a job that is overdue runs it straight away; otherwise it waits for its time.</summary>
    public void Resume(string jobId) => _store.SetEnabled(jobId, true);
    public void Pause(string jobId) => _store.SetEnabled(jobId, false);

    /// <summary>A new period counts from the start of the last run, so 1 h -> 6 h does not wait out the old hour.</summary>
    public void SetEvery(string jobId, int minutes)
    {
        _store.SetEvery(jobId, minutes);
        if (_store.Runs(jobId, 1).FirstOrDefault()?["startedAt"] is string s && DateTimeOffset.TryParse(s, out var last))
            _store.SetNext(jobId, last + LastWar.Client.Jitter.Of(TimeSpan.FromMinutes(minutes), 0.2));
    }

    public object View() => _store.Jobs().Select(j =>
    {
        var live = Running(j.Id);
        var last = _store.Runs(j.Id, 5).FirstOrDefault(r => (string?)r["state"] != "running");
        return new
        {
            j.Id, j.Name, j.Op, j.Args, j.EveryMinutes, j.Enabled, NextAt = j.NextAt?.ToString("O"),
            Running = live is null ? null : new { live.RunId, StartedAt = live.StartedAt.ToString("O"), Lines = live.Lines.ToArray() },
            LastRun = last,
            Description = Operations.Find(j.Op)?.Description,
        };
    }).ToList();
}
