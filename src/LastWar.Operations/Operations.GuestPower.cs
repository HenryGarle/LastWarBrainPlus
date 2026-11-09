using LastWar.Client;
using LastWar.Client.Auth;
using LastWar.Client.Scanning;
using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Operations;

public static partial class Operations
{
    /// <summary>
    /// Power collection that never touches a real account. Every attempt at a server runs on a
    /// brand-new guest, created for it and thrown away after.
    ///
    /// Long detail sweeps get their connection dropped, on guests and on Sadmin alike, and on
    /// Sadmin that means repeatedly re-logging a real account into a server that has just stopped
    /// answering it - the pattern most likely to get an account flagged. Guests are disposable,
    /// but a guest can log in only once, so a dropped attempt continues on a NEW guest.
    ///
    /// Collection is resumable: what an attempt fetched before its connection died is kept, and
    /// the next attempt asks only about the players still missing. So a drop costs the time since
    /// the last answer, not the whole server.
    /// </summary>
    static void AddGuestPowerOperation()
    {
        Add("scan.power.guests", "Scan", "Power snapshot (guests)",
            "Power for a server range using a fresh throwaway guest per attempt, from a map already "
            + "stored by a sweep. Resumes after a dropped connection instead of starting over, and "
            + "never uses a real account - the safe way to run a long collection.",
            async c =>
            {
                var db = new LastWarDb(c.Database);
                db.EnsureSchema();
                var wave = new WaveStore(c.Database);
                var maxAge = c.Int("mapMaxAgeHours", 12);
                var attempts = c.Int("attempts", 6);
                var gap = TimeSpan.FromMilliseconds(c.Int("gap", 100));
                var root = Path.Combine(c.DataDir, "sessions", "auto");

                var rows = new List<object?[]>();
                foreach (var id in ParseServers(c.Text("servers")))
                {
                    var server = new ServerId(id);
                    var (tiles, mapAt) = wave.LatestTiles(id, DateTimeOffset.UtcNow.AddHours(-maxAge));
                    if (tiles.Count == 0)
                    {
                        rows.Add([id, "", 0, 0, 0, $"no map in the last {maxAge}h - sweep it first"]);
                        continue;
                    }

                    var at = DateTimeOffset.UtcNow;
                    var collected = new Dictionary<PlayerUid, PowerSample>();
                    var used = 0;
                    var completed = false;

                    for (var attempt = 1; attempt <= attempts && !completed; attempt++)
                    {
                        var remaining = tiles.Where(t => !collected.ContainsKey(t.OwnerUid)).ToList();
                        if (remaining.Count == 0) { completed = true; break; }

                        used = attempt;
                        var home = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{id}_{attempt}");
                        try
                        {
                            var config = await GuestBootstrap.CreateAsync(home, null, c.Cancellation);
                            await using var client = await LastWarClient.ConnectAsync(
                                config, GameIdentity.LoadOrCreate(home), c.Cancellation);
                            c.Report($"{server} attempt {attempt}: guest on {client.Server.Value}, "
                                + $"{remaining.Count} of {tiles.Count} players left");

                            await new PowerCollector(client) { Gap = gap }.CollectAsync(
                                server, remaining, at,
                                new Progress<PowerScanProgress>(p =>
                                {
                                    if (p.Done % 250 == 0)
                                        c.Report($"{server} {p.Stage}: {p.Done}/{p.Total}, {collected.Count} held");
                                }), c.Cancellation, collected);
                            completed = true;
                        }
                        catch (Exception ex) when (ex is IOException or TimeoutException
                                                        or LoginFailedException or HttpRequestException
                                                        or InvalidOperationException)
                        {
                            c.Report($"{server} attempt {attempt} lost ({ex.Message}); {collected.Count} held");
                            // Back off harder each time. If this is throttling, walking straight
                            // back in is the wrong response.
                            await Task.Delay(TimeSpan.FromSeconds(30 * attempt), c.Cancellation);
                        }
                        finally
                        {
                            // The guest is spent either way - it cannot log in a second time.
                            if (GuestKeeper.Retire(c.DataDir, c.Database, home) is { } kept) c.Report($"guest landed on a tracked server - kept in {kept}");
                        }
                    }

                    var stored = collected.Count > 0 ? db.InsertPowerSamples([.. collected.Values]) : 0;
                    rows.Add([id, at.ToString("u"), tiles.Count, stored, used,
                        completed ? "stored" : $"PARTIAL after {used} attempts"]);
                    c.Report($"{server} {(completed ? "done" : "PARTIAL")}: {stored} accounts over {used} guest(s)");

                    // A breather between servers keeps the aggregate rate down.
                    await Task.Delay(TimeSpan.FromSeconds(c.Int("pauseSeconds", 20)), c.Cancellation);
                }

                var ok = rows.Count(r => (string?)r[5] == "stored");
                return OperationResult.Table(
                    $"{ok} of {rows.Count} servers complete, {rows.Sum(r => Convert.ToInt64(r[3])):N0} accounts",
                    ["server", "taken", "players", "accounts", "guests", "result"], rows);
            },
            needsSession: false,
            ps:
            [
                new OperationParam("servers", "Servers", "text", Required: true,
                    Help: "A list or a range. Each needs a recent map - run Scan the map first."),
                new OperationParam("gap", "Gap between player requests (ms)", "number", "100"),
                new OperationParam("attempts", "Guests per server at most", "number", "6"),
                new OperationParam("pauseSeconds", "Pause between servers (s)", "number", "20"),
                new OperationParam("mapMaxAgeHours", "Stored map no older than (hours)", "number", "12"),
            ]);
    }
}
