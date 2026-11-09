using LastWar.Protocol.Sfs;
using LastWar.Storage;

namespace LastWar.Operations;

public static partial class Operations
{
    /// <summary>
    /// Warzone Duel (SvS) as the game shows it to an account on a server in the event (Government -> Warzone Duel,
    /// ZoneWarManager): cross.throne.schedule (the 8 servers, rounds, settle times), cross.throne.all.round.info
    /// (every round's pairs, scores, wins), cross.throne.cur.round.score.detail (the live round: score by source),
    /// cross.throne.cur.round.score.alliance.rank. All read-only, no parameters; probed on a guest 2026-10-07.
    /// It only covers the account's OWN event group. Every snapshot is stored (SvsStore).
    /// </summary>
    static void AddSvsOperations()
    {
        Add("svs.forecast", "Events", "Keep SvS forecasts",
            "Stores today's points forecast (every race day, every metric) for each SvS pair forecast in the last 8 days, so the "
            + "estimates can be compared with the real result later. No network.",
            c => Task.FromResult(OperationResult.Text($"{new SvsStore(c.Database).RecordRecentForecasts()} pairs: today's forecast kept")),
            needsSession: false, writes: true);

        Add("svs.status", "Events", "Warzone Duel (SvS) snapshot",
            "Reads this account's Warzone Duel: the 8 servers, every round's matchups and points, and the live round's score "
            + "breakdown, and stores it. Only the account's own event group.",
            async c =>
            {
                var g = c.Require();
                async Task<SfsObject?> Call(string cmd, SfsObject? p = null)
                {
                    var r = await g.TryCallAsync(cmd, p ?? new SfsObject(), TimeSpan.FromSeconds(10), c.Cancellation);
                    await Task.Delay(LastWar.Client.Jitter.Of(TimeSpan.FromMilliseconds(800)), c.Cancellation);
                    return r;
                }
                var schedule = await Call("cross.throne.schedule");
                if (schedule is null || schedule.GetString("errorCode") is { Length: > 0 })
                    return OperationResult.Text($"no Warzone Duel for #{g.Server.Value} right now ({schedule?.GetString("errorCode") ?? "no reply"})");
                var sj = Json(schedule);
                if (!sj.TryGetProperty("cfg", out var cfg)) return OperationResult.Text($"#{g.Server.Value} is not in a Warzone Duel right now");
                var ids = Str(cfg, "server_ids");
                var rounds = await Call("cross.throne.all.round.info");
                var dp = new SfsObject();
                dp.PutInt("page", 1);
                dp.PutInt("pageSize", 20);
                var detail = await Call("cross.throne.cur.round.score.detail", dp);
                var al = await Call("cross.throne.cur.round.score.alliance.rank");
                string J(SfsObject? o) => o is null ? "" : SfsJson.Write(o).ToJsonString();
                var servers = ";" + ids.Replace('|', ';') + ";";
                new SvsStore(c.Database).Save(g.Server.Value, Num(sj, "cfgId"), servers, J(schedule), J(rounds), J(detail), J(al));
                return OperationResult.Text($"Warzone Duel {Num(sj, "cfgId")}: {ids.Replace(";", ", ").Replace("|", " vs ")}, round {Num(sj, "curRound")} - stored");
            });
    }
}
