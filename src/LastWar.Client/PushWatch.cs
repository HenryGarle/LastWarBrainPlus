using System.Threading.Channels;
using LastWar.Protocol.Sfs;

namespace LastWar.Client;

/// <summary>
/// Waits for server pushes without consuming the inbound channel.
///
/// It exists because a push that arrives late - after the call that asked for it gave up - stays
/// queued, and the next caller then reads that stale push as though it were its own answer. Mail
/// paging showed this as alternating full and empty results: a run would leave its last, empty
/// page behind and the following run would consume it and stop immediately.
///
/// A watch is opened BEFORE the request goes out and only sees frames from that moment on, so a
/// leftover from an earlier request can never be mistaken for this one's reply.
/// </summary>
public sealed class PushWatch : IDisposable
{
    readonly GameConnection _conn;
    readonly HashSet<string> _commands;
    readonly Channel<SfsObject> _seen =
        Channel.CreateBounded<SfsObject>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    PushWatch(GameConnection conn, IEnumerable<string> commands)
    {
        _conn = conn;
        _commands = [.. commands];
        _conn.Frame += Observe;
    }

    public static PushWatch On(GameConnection conn, params string[] commands) => new(conn, commands);

    void Observe(FrameRecord f)
    {
        if (!f.Outbound && _commands.Contains(f.Command)) _seen.Writer.TryWrite(f.Params);
    }

    /// <summary>The next matching push to arrive since this watch was opened.</summary>
    public async Task<SfsObject?> NextAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try { return await _seen.Reader.ReadAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public void Dispose() => _conn.Frame -= Observe;
}
