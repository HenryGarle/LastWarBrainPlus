using LastWar.Client.Game;
using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Operations;

public static partial class Operations
{
    /// <summary>
    /// intel.sweep: per server its cities, card and president history; per alliance (with at least
    /// minMembers members) its card and full roster with donations. Guest only - every command is a
    /// cross-server read (see LastWar.Client.Game.Intel). Rosters also feed power_sample as
    /// AllianceRoster readings, as scan.power's rosters always did.
    /// </summary>
    static void AddIntelOperations()
    {
        Add("hero.fit", "Research", "Hero power model back-test",
            "Refits hero power = k x (power - troop power) on every server's newest top-200 hero board and "
            + "shows how far off it is per server (it also runs with every history rebuild). No game connection.",
            c =>
            {
                var n = new HeroModel(c.Database).Rebuild();
                var rows = new Insights(c.Database).HeroFit().Select(r => (IReadOnlyList<string>)[
                    Convert.ToInt32(r["server"]) == 0 ? "all" : r["server"]!.ToString()!, $"{r["k"]:0.0000}", r["players"]!.ToString()!,
                    $"{r["medianErr"]:P1}", $"{r["p90Err"]:P1}", $"{r["worstErr"]:P1}", $"{r["bottom20Err"]:P1}", $"{r["bias"]:+0.0%;-0.0%}"]).ToList();
                return Task.FromResult(OperationResult.Table($"{n} board players matched to a profile",
                    ["server", "k", "players", "median error", "90% within", "worst", "ranks 181-200", "bias"], rows));
            },
            needsSession: false);

        Add("intel.sweep", "Scan", "Alliance & server intel",
            "On a throwaway guest: every server's cities (who holds what), score, president and "
            + "president history; every alliance's card and roster - rank, last donation, today's and "
            + "this week's donations, when they joined. Feeds activity, territory and war views.",
            async c =>
            {
                var servers = ParseServers(c.Text("servers", "1957-1988"));
                var minMembers = c.Int("minMembers", 10);
                var gap = TimeSpan.FromMilliseconds(c.Int("gapMs", 60));
                var store = new IntelStore(c.Database);
                store.EnsureSchema();
                var db = new LastWarDb(c.Database);
                var alliances = store.Alliances(servers.Min(), servers.Max(), minMembers);
                int cities = 0, rosters = 0, members = 0, presidents = 0;

                await WithGuestAsync(c, "intel", async g =>
                {
                    var intel = new Intel(g);
                    var at = DateTimeOffset.UtcNow;
                    foreach (var server in servers)
                    {
                        c.Cancellation.ThrowIfCancellationRequested();
                        var held = await intel.CitiesAsync(server, at, c.Cancellation);
                        store.Insert(held); cities += held.Count(h => h.AllianceId.Length > 0);
                        if (await intel.ServerStateAsync(server, at, c.Cancellation) is { } st) store.Insert(st);
                        var holds = await intel.StrongholdsAsync(server, at, c.Cancellation);
                        store.Insert(holds);
                        var kings = await intel.PresidentsAsync(server, ct: c.Cancellation);
                        store.Insert(kings, at); presidents += kings.Count;
                        c.Report($"{server}: {held.Count} cities, {kings.Count} presidents");
                        await Task.Delay(LastWar.Client.Jitter.Of(gap), c.Cancellation);
                    }
                    store.Insert(await intel.ServerCardsAsync(servers, at, c.Cancellation));
                    var n = 0;
                    foreach (var (id, tag, server) in alliances)
                    {
                        c.Cancellation.ThrowIfCancellationRequested();
                        if (await intel.AllianceInfoAsync(id, at, c.Cancellation) is { } info) store.Insert(info);
                        if (await intel.RosterAsync(id, at, c.Cancellation) is { } roster)
                        {
                            store.Insert(roster); rosters++; members += roster.Members.Count;
                            db.InsertPowerSamples([.. roster.Members.Select(m => new PowerSample
                            {
                                TakenAt = at, Server = new ServerId(m.Server > 0 ? m.Server : server), Uid = new PlayerUid(m.Uid),
                                Power = m.Power, Name = m.Name, AllianceId = id, AllianceTag = tag, KillArmy = m.Kills,
                                HqLevel = m.Hq, Source = PowerSource.AllianceRoster,
                            })]);
                        }
                        if (++n % 25 == 0) c.Report($"alliances {n}/{alliances.Count}, {members} members");
                        await Task.Delay(LastWar.Client.Jitter.Of(gap), c.Cancellation);
                    }
                });
                return OperationResult.Text($"{servers.Count} servers: {cities} held cities, {presidents} presidents; "
                    + $"{rosters}/{alliances.Count} alliance rosters, {members} members");
            },
            needsSession: false,
            ps:
            [
                RefreshServers,
                new OperationParam("minMembers", "Alliances with at least", "number", "10"),
                new OperationParam("gapMs", "Gap between requests (ms)", "number", "60"),
            ]);

        Add("intel.players", "Scan", "Player details (drone, profile card)",
            "On a throwaway guest, for the strongest players from each server: their drone (level, power, "
            + "chip level) and profile card (career, alliance rank, badge). Two requests a player, so it "
            + "covers the top of each server rather than everyone.",
            async c =>
            {
                var servers = ParseServers(c.Text("servers", "1957-1988"));
                var top = c.Int("top", 100);
                var gap = TimeSpan.FromMilliseconds(c.Int("gapMs", 40));
                var store = new IntelStore(c.Database);
                store.EnsureSchema();
                var players = store.TopPlayers(servers.Min(), servers.Max(), top);
                // missing=true: only players with no drone read in the last day (finishes a run that dropped).
                if (c.Flag("missing", false))
                {
                    var have = store.DetailedSince(DateTimeOffset.UtcNow.AddDays(-1));
                    players = [.. players.Where(p => !have.Contains(p.Uid))];
                }
                var got = 0;
                await WithGuestAsync(c, "players", async g =>
                {
                    var intel = new Intel(g);
                    var at = DateTimeOffset.UtcNow;
                    var batch = new List<PlayerDetail>();
                    // A dropped connection ends the run but keeps what was read (the next run redoes the lot).
                    try
                    {
                        foreach (var (uid, server) in players)
                        {
                            c.Cancellation.ThrowIfCancellationRequested();
                            if (await intel.PlayerDetailAsync(uid, server, at, c.Flag("card", false), c.Cancellation) is { } d) { batch.Add(d); got++; }
                            if (batch.Count >= 200) { store.Insert(batch); batch.Clear(); c.Report($"{got}/{players.Count} players"); }
                            await Task.Delay(LastWar.Client.Jitter.Of(gap), c.Cancellation);
                        }
                    }
                    finally { store.Insert(batch); }
                });
                return OperationResult.Text($"{got}/{players.Count} players (top {top} of {servers.Count} servers): drone and profile card");
            },
            needsSession: false,
            ps:
            [
                RefreshServers,
                new OperationParam("top", "Strongest players per server", "number", "100"),
                new OperationParam("card", "Also the profile card (career, badge; doubles the time)", "bool", "false"),
                new OperationParam("missing", "Only players not read in the last day", "bool", "false"),
                new OperationParam("gapMs", "Gap between requests (ms)", "number", "40"),
            ]);
    }
}
