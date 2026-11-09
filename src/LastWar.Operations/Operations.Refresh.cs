using LastWar.Client;
using LastWar.Client.Auth;
using LastWar.Client.Game;

namespace LastWar.Operations;

public static partial class Operations
{
    /// <summary>
    /// The two things you actually run, split by what account they need. Everything else in
    /// Scan/Migration is a step of these.
    ///
    ///   refresh.stats  boards + profile sweep. Power, alliance, home/based server, every board's
    ///                  top 200 - for everyone ever seen on the range. Guests only. ~6 min for 32.
    ///   refresh.map    map sweep: positions, and new arrivals for the next profile sweep. Needs a
    ///                  real account - guests cannot view other servers' maps. ~2 s a server.
    ///
    /// Each step stores as it goes, so a failure keeps what earlier steps got. The derived
    /// histories are rebuilt once at the end, not after every step.
    /// </summary>
    static void AddRefreshOperations()
    {
        Add("refresh.stats", "Refresh", "Refresh stats (no map)",
            "Every leaderboard, then a profile sweep of every known player: power, alliance, home "
            + "server and where their base is right now. Guests only - never touches a real account. "
            + "About five minutes for 32 servers. Players new to the range are picked up once a map "
            + "sweep or a board has seen them.",
            async c =>
            {
                var steps = new List<object?[]>();
                await BoardsOnGuestAsync(c, steps);
                await StepAsync(c, steps, "scan.profiles", new() { ["servers"] = c.Text("servers", "1957-1988") }, null);
                // After the profile sweep, so alliance_latest knows which alliances belong to the range.
                LastWar.Storage.Derived.RebuildAll(c.Database);
                await StepAsync(c, steps, "intel.sweep", new() { ["servers"] = c.Text("servers", "1957-1988") }, null);
                if (c.Int("playerDetails", 50) is > 0 and var top)
                    await StepAsync(c, steps, "intel.players", new() { ["servers"] = c.Text("servers", "1957-1988"), ["top"] = top.ToString() }, null);
                return Finish(c, steps);
            },
            needsSession: false,
            ps: [RefreshServers, new OperationParam("playerDetails", "Drone + profile card for the top N per server", "number", "50")]);

        Add("refresh.map", "Refresh", "Refresh the map",
            "Where every base is, for every server in the range - and who is new there, so the next "
            + "Refresh stats asks about them. Needs a REAL account with HQ 13+: the game refuses other servers' maps below that (cross_server_view_tips). "
            + "At a person's pace: ~1 request a second, 10 servers a run (the longest-unscanned first), and it stops for the day if the game closes the connection. Pick the account's runner; if it is stopped it is "
            + "logged in for this run only. Run Refresh stats after it.",
            async c =>
            {
                var steps = new List<object?[]>();
                await StepAsync(c, steps, "scan.map", new() { ["servers"] = c.Text("servers", "1957-1988") }, c.Require());
                return Finish(c, steps);
            },
            ps: [RefreshServers]);
    }

    static readonly OperationParam RefreshServers =
        new("servers", "Servers", "text", "1957-1988", Required: true, Help: "A range, e.g. 1957-1988.");

    static OperationResult Finish(OperationContext c, List<object?[]> steps)
    {
        try { LastWar.Storage.Derived.RebuildAll(c.Database); steps.Add(["history rebuild", "", "done"]); }
        catch (Exception ex) { steps.Add(["history rebuild", "", ex.Message]); }
        var failed = steps.Where(s => s[2] is string r && r.StartsWith("FAILED", StringComparison.Ordinal)).ToList();
        // The summary is all a scheduled run keeps, so it names what failed.
        // A step can succeed partly (e.g. scan.map: 16 of 64 servers) - its own summary goes in, or "complete" hides it.
        var partial = steps.Where(s => s[2] is string r && r.Contains(" not scanned", StringComparison.Ordinal)).ToList();
        return OperationResult.Table(failed.Count > 0 ? $"refresh finished with {failed.Count} failed step(s): {failed[0][0]} {failed[0][2]}"
            : partial.Count > 0 ? $"refresh PARTLY done: {partial[0][0]} {partial[0][2]}" : "refresh complete",
            ["step", "seconds", "result"], steps);
    }

    /// <summary>Runs another operation as a step: its own args, no history rebuild, failure recorded not thrown.</summary>
    static async Task StepAsync(OperationContext c, List<object?[]> steps, string id,
        Dictionary<string, string> args, LastWarClient? session)
    {
        var entry = Entries.First(e => e.Info.Id == id);
        foreach (var p in entry.Info.Params)
            if (!args.ContainsKey(p.Name) && p.Default is { } d) args[p.Name] = d;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        c.Report($"== {id}");
        try
        {
            var r = await entry.Run(new OperationContext
            {
                Args = args, Database = c.Database, DataDir = c.DataDir, Session = session,
                Progress = c.Progress, Cancellation = c.Cancellation,
            });
            steps.Add([id, Math.Round(sw.Elapsed.TotalSeconds), r.Summary]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add([id, Math.Round(sw.Elapsed.TotalSeconds), $"FAILED: {ex.Message}"]);
        }
    }

    /// <summary>rank.get is cross-server, so a throwaway guest reads every board.</summary>
    static async Task BoardsOnGuestAsync(OperationContext c, List<object?[]> steps)
    {
        try
        {
            await WithGuestAsync(c, "boards", guest =>
                StepAsync(c, steps, "board.snapshot", new() { ["servers"] = c.Text("servers", "1957-1988") }, guest));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or LoginFailedException or HttpRequestException)
        {
            steps.Add(["board.snapshot", "", $"FAILED: guest login ({ex.Message})"]);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a brand-new throwaway guest: created, logged in once, settled
    /// (the first request sent while the init burst lands is never answered), then deleted. A guest
    /// can log in only once, so it is never kept.
    /// </summary>
    /// <param name="zone">"APS1967" asks the gateway to create the guest on that server (refused with E013 while the server is closed to new players).</param>
    internal static async Task WithGuestAsync(OperationContext c, string label, Func<LastWarClient, Task> work, string? zone = null)
    {
        var home = Path.Combine(c.DataDir, "sessions", "auto", $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{label}");
        try
        {
            // Two jobs making guests at the same moment (startup) can get "gateway returned no servers": one retry.
            var config = await Retry();
            async Task<LastWar.Client.Auth.SessionConfig> Once() => await GuestBootstrap.CreateAsync(home, zone, c.Cancellation);
            async Task<LastWar.Client.Auth.SessionConfig> Retry()
            {
                try { return await Once(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                c.Report($"guest bootstrap failed ({ex.Message}); retrying in 20 s");
                await Task.Delay(TimeSpan.FromSeconds(20), c.Cancellation);
                if (Directory.Exists(home)) Directory.Delete(home, recursive: true);   // a half-made guest home forces opt=fix, which is refused
                return await Once();
            }
            }
            await using var guest = await LastWarClient.ConnectAsync(config, GameIdentity.LoadOrCreate(home), c.Cancellation);
            if (LoginPushRecorder.For(guest.Connection) is { } rec) await rec.WaitAsync(TimeSpan.FromSeconds(20), c.Cancellation);
            await Task.Delay(TimeSpan.FromSeconds(3), c.Cancellation);
            await work(guest);
        }
        finally
        {
            if (GuestKeeper.Retire(c.DataDir, c.Database, home) is { } kept) c.Report($"guest landed on a tracked server - kept in {kept}");
        }
    }
}
