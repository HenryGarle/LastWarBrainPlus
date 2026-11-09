using LastWar.Protocol.Sfs;

namespace LastWar.Client.Game;

/// <summary>
/// City visitors: the "?"-bubble characters that arrive on their own, up to the server's own
/// maxNum (5 on a live account).
/// </summary>
public sealed class VisitorActions(LastWarClient client)
{
    public TimeSpan PushWait { get; init; } = TimeSpan.FromSeconds(12);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Greets every visitor present, one visitor.operate {uid, operate: 1} per head - the same
    /// per-tap shape the real client sends, which has no bulk form.
    ///
    /// Visitors come from the login push, not from a request (see LoginPushes). A visitor whose
    /// arrival window has not elapsed answers errorCode=visitor_err_coming "visitor not started";
    /// that is a normal outcome, not a failure, and explains why a real session leaves the
    /// newest-arrived visitor ungreeted.
    /// </summary>
    public async Task<GameActionResult> GreetAllAsync(CancellationToken ct = default)
    {
        var (_, visitors) = await LoginPushes
            .ReadAsync(client.Connection, PushWait, ct).ConfigureAwait(false);
        if (visitors.Count == 0) return GameActionResult.Text("no visitors to greet");

        var rows = new List<IReadOnlyList<string>>();
        var greeted = 0;
        foreach (var v in visitors)
        {
            ct.ThrowIfCancellationRequested();
            var p = new SfsObject();
            p.PutLong("uid", v.Uid);        // long: a visitor uid sent as an int is rejected
            p.PutInt("operate", 1);
            var reply = await client
                .TryCallAsync("visitor.operate", p, Timeout, ct).ConfigureAwait(false);
            if (Replies.Ok(reply)) greeted++;
            rows.Add([v.Uid.ToString(), v.EventId.ToString(), v.VisitorId.ToString(),
                Replies.Outcome(reply)]);
        }

        return new GameActionResult($"greeted {greeted} of {visitors.Count} visitors",
            ["Uid", "EventId", "VisitorId", "Result"], rows);
    }
}
