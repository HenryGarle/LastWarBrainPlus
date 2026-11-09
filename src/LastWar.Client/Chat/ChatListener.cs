using System.Collections.Concurrent;
using LastWar.Core.Model;

namespace LastWar.Client.Chat;

/// <summary>
/// A live chat feed: one connection, one reader, every message handed to a callback.
///
/// The connection has a single inbound queue, exactly like the game socket, and the rule is the
/// same: only one thing may read it. So the listener IS that reader. Pushes go to the callback;
/// replies to requests go back to whoever sent them, through a waiter keyed by command. A history
/// request made while live therefore never swallows a push, and a push never swallows a reply.
///
/// The listener does not store anything itself - the caller decides where messages go - which
/// keeps this usable from the dashboard and from a plain command line alike.
/// </summary>
public sealed class ChatListener : IAsyncDisposable
{
    readonly ChatConnection _chat;
    readonly Action<IReadOnlyList<ChatMessage>> _onMessages;
    readonly ConcurrentDictionary<string, TaskCompletionSource<ChatFrame>> _waiting = new();
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _requestLock = new(1, 1);
    readonly Task _loop;

    public Uri Gateway { get; }
    public string Uid { get; }
    public long Received { get; private set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastPushAt { get; private set; }
    public Exception? Fault { get; private set; }
    public bool Alive => !_loop.IsCompleted;

    ChatListener(ChatConnection chat, Uri gateway, string uid, Action<IReadOnlyList<ChatMessage>> onMessages)
    {
        _chat = chat;
        Gateway = gateway;
        Uid = uid;
        _onMessages = onMessages;
        _loop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Opens chat for a logged-in game session, using the token its login push carried.</summary>
    public static async Task<ChatListener> StartAsync(
        LastWarClient game, Action<IReadOnlyList<ChatMessage>> onMessages, CancellationToken ct = default)
    {
        var creds = game.ChatCredentials
            ?? throw new InvalidOperationException("no chat token yet - the session is still logging in");
        var (preferred, _) = await new ChatDirectory().FetchAsync(creds.Uid, game.Server.Value).ConfigureAwait(false);
        if (preferred.Count == 0) throw new InvalidOperationException("the chat directory offered no gateway");

        Exception? last = null;
        foreach (var ep in preferred)
        {
            try
            {
                var chat = await ChatConnection.ConnectAsync(ep, creds.Uid, creds.ChatToken, ct: ct).ConfigureAwait(false);
                return new ChatListener(chat, ep.Uri, creds.Uid, onMessages);
            }
            catch (TimeoutException ex) { last = ex; }
        }
        throw new InvalidOperationException("no chat gateway accepted the handshake", last);
    }

    async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var f in _chat.Frames(_cts.Token).ConfigureAwait(false))
            {
                if (_waiting.TryRemove(f.Command, out var waiter)) { waiter.TrySetResult(f); continue; }
                if (!ChatMessageParser.MessagePushes.Contains(f.Command)) continue;
                if (ChatMessageParser.FromPush(f) is not { } m) continue;
                Received++;
                LastPushAt = DateTimeOffset.UtcNow;
                try { _onMessages([m]); } catch { /* a failing consumer must not stop the feed */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fault = ex; }
        finally
        {
            foreach (var w in _waiting.Values) w.TrySetCanceled();
        }
    }

    /// <summary>Sends a signed request and waits for the reply the read loop routes back.</summary>
    public async Task<ChatFrame?> RequestAsync(string command, object ps, TimeSpan timeout, CancellationToken ct = default)
    {
        // Replies carry no correlation id, only the command name, so requests go one at a time:
        // a poll and a catch-up asking for history.roomsv2 together would get each other's answer.
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var waiter = new TaskCompletionSource<ChatFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[command] = waiter;
            await _chat.SendSignedAsync(command, ps, ct).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(timeout);
            try { return await waiter.Task.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { _waiting.TryRemove(command, out _); return null; }
        }
        finally { _requestLock.Release(); }
    }

    public async Task<IReadOnlyList<ChatMessage>> PageAsync(string room, long cursor, int count = 50, CancellationToken ct = default)
    {
        var (command, ps) = ChatHistory.Request(room, cursor, count);
        var reply = await RequestAsync(command, ps, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return reply is null ? [] : ChatHistory.Parse(reply);
    }

    /// <summary>
    /// The newest block of several rooms in one request: history.roomsv2 takes a MAP of rooms,
    /// so polling ten rooms costs the same single round trip as polling one.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessage>> NewestAsync(IEnumerable<string> rooms, CancellationToken ct = default)
    {
        var map = rooms.Distinct().ToDictionary(r => r, _ => "0");
        if (map.Count == 0) return [];
        var reply = await RequestAsync("history.roomsv2", new Dictionary<string, object?> { ["rooms"] = map },
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return reply is null ? [] : ChatHistory.Parse(reply);
    }

    /// <summary>
    /// Fetches everything a room has said since <paramref name="afterSeq"/>, newest page first,
    /// walking back until it meets what is already held - so going live never leaves a hole
    /// between the last stored message and the first pushed one.
    /// </summary>
    public async Task<int> CatchUpAsync(string room, long afterSeq, int maxPages = 200, CancellationToken ct = default)
    {
        long cursor = 0;
        var total = 0;
        for (var page = 0; page < maxPages; page++)
        {
            var msgs = await PageAsync(room, cursor, 50, ct).ConfigureAwait(false);
            if (msgs.Count == 0) break;
            var fresh = msgs.Where(m => m.SeqId > afterSeq).ToList();
            if (fresh.Count > 0) { _onMessages(fresh); total += fresh.Count; }
            var oldest = msgs.Min(m => m.SeqId);
            if (oldest <= afterSeq + 1 || oldest <= 1) break;
            if (cursor != 0 && oldest >= cursor) break;   // not advancing: the room has given all it will
            cursor = oldest;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        return total;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _chat.DisposeAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
