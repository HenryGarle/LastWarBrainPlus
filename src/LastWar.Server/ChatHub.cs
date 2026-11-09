using LastWar.Client.Game;
using System.Text.Json;
using System.Threading.Channels;
using LastWar.Client.Chat;
using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Server;

/// <summary>
/// Live chat for the dashboard.
///
/// Each running runner can carry one <see cref="ChatListener"/>. Whatever it hears - pushes for
/// rooms its account is in - is stored and fanned out to browsers. On top of that the hub polls
/// the FOLLOWED rooms (world chat of any server, by default the runner's own) through whichever
/// listener is alive, because pushes only cover rooms the account has joined but world chat can
/// be read from anywhere once the request is signed.
///
/// Every message goes through <see cref="ChatStore.InsertNew"/> first, so a message heard by a
/// push, a poll and a catch-up is stored once and shown once.
/// </summary>
public sealed class ChatHub : IAsyncDisposable
{
    static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(8);

    readonly ChatStore _store;
    readonly string _followPath;
    readonly Lock _gate = new();
    readonly Dictionary<string, ChatListener> _listeners = new();    // runner id -> listener
    readonly Dictionary<string, Runner> _runners = new();              // runner id -> the runner under it
    readonly Dictionary<string, PlayerProfile> _users = new();          // uid -> newest profile known
    readonly ProfileStore _profiles;
    readonly HashSet<string> _want = new();                             // uids waiting for a lookup
    readonly ChatTranslator _translator = new(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
    readonly List<Channel<string>> _subs = new();    // pre-built SSE frames
    readonly HashSet<string> _follow;
    readonly HashSet<string> _catching = new();    // rooms the poll must leave alone for now
    readonly CancellationTokenSource _cts = new();
    readonly Task _poll;

    public string? LastError { get; private set; }
    public DateTimeOffset? LastPollAt { get; private set; }

    public ChatHub(string database, string followPath)
    {
        _store = new ChatStore(database);
        _store.EnsureSchema();
        _profiles = new ProfileStore(database);
        _profiles.EnsureSchema();
        _followPath = followPath;
        _follow = File.Exists(followPath)
            ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(followPath)) ?? []
            : [];
        _poll = Task.Run(PollLoopAsync);
    }

    public ChatStore Store => _store;

    public string[] Followed { get { lock (_gate) return [.. _follow.Order()]; } }

    public void Follow(string room, bool on)
    {
        lock (_gate)
        {
            if (on ? !_follow.Add(room) : !_follow.Remove(room)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_followPath)!);
            File.WriteAllText(_followPath, JsonSerializer.Serialize(_follow));
        }
        if (on) CatchUp(room, null);
    }

    /// <summary>Archive: stop following, keep every message, leave it out of chat stats. Off = restore.</summary>
    public void Archive(string room, bool on)
    {
        _store.Archive(room, on);
        if (on) Follow(room, false);
    }

    /// <summary>Stop following and delete the room's messages for good; later pushes for it are dropped.</summary>
    public int Delete(string room)
    {
        Follow(room, false);
        return _store.DeleteRoom(room);
    }

    /// <summary>
    /// Fills a room from <paramref name="afterSeq"/> (default: the newest stored) up to now, in
    /// the background. The poll skips the room meanwhile: had it stored the newest block first,
    /// the newest stored seq would jump past the hole and the hole would never be seen again.
    /// A room never captured is left to the poll - its whole history is chat.backfill's job.
    /// </summary>
    public bool CatchUp(string room, long? afterSeq)
    {
        ChatListener? l;
        lock (_gate)
        {
            l = _listeners.Values.FirstOrDefault(x => x.Alive);
            if (l is null || !_catching.Add(room)) return false;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if ((afterSeq ?? _store.NewestSeqId(room)) is { } from)
                    await l.CatchUpAsync(room, from, ct: _cts.Token);
            }
            catch (Exception ex) { LastError = $"catch-up {room}: {ex.Message}"; }
            finally { lock (_gate) _catching.Remove(room); }
        });
        return true;
    }

    public object Status()
    {
        lock (_gate)
            return new
            {
                followed = _follow.Order().ToArray(),
                catchingUp = _catching.Order().ToArray(),
                lastPollAt = LastPollAt,
                lastError = LastError,
                listeners = _listeners.Select(kv => new
                {
                    runner = kv.Key,
                    uid = kv.Value.Uid,
                    gateway = kv.Value.Gateway.ToString(),
                    alive = kv.Value.Alive,
                    received = kv.Value.Received,
                    startedAt = kv.Value.StartedAt,
                    lastPushAt = kv.Value.LastPushAt,
                    fault = kv.Value.Fault?.Message,
                }).ToArray(),
            };
    }

    public bool Listening(string runnerId) { lock (_gate) return _listeners.ContainsKey(runnerId); }

    ChatListener ListenerOf(string runnerId)
    {
        lock (_gate)
            return _listeners.GetValueOrDefault(runnerId) is { Alive: true } l ? l
                : throw new InvalidOperationException($"chat is not open on {runnerId}: start the runner and its chat first");
    }

    /// <summary>The account uid behind a runner's chat link.</summary>
    public string UidOf(string runnerId) => ListenerOf(runnerId).Uid;

    /// <summary>One page of a room through one runner's link (cursor 0 = newest). Not stored: private rooms stay out of the store.</summary>
    /// For a private room the newest page comes from history.roomlast: history.roomsv2 answers msgs [] for one.
    public async Task<IReadOnlyList<ChatMessage>> PageAsync(string runnerId, string room, long cursor, CancellationToken ct)
    {
        var l = ListenerOf(runnerId);
        if (cursor != 0 || !ChatStore.IsPrivate(room)) return await l.PageAsync(room, cursor, 50, ct);
        var reply = await l.RequestAsync("history.roomlast", new Dictionary<string, object?> { ["rooms"] = new Dictionary<string, string> { [room] = "0" } },
            TimeSpan.FromSeconds(15), ct);
        return reply is null ? [] : ChatHistory.Parse(reply);
    }

    /// <summary>Admin / research: any chat-socket command through one runner's chat link, raw reply back.</summary>
    public async Task<string?> RawAsync(string runnerId, string command, object ps, CancellationToken ct) =>
        (await ListenerOf(runnerId).RequestAsync(command, ps, TimeSpan.FromSeconds(15), ct))?.Raw;

    // ---- listeners

    /// <summary>
    /// Opens chat on a runner's session, follows its own world room, and fills the gap between
    /// the newest stored message and now before the feed counts as live.
    /// </summary>
    public async Task AttachAsync(Runner runner, CancellationToken ct = default)
    {
        var client = runner.Client ?? throw new InvalidOperationException("runner is not running");
        await Detach(runner.Spec.Id);
        await client.WaitForChatCredentialsAsync(TimeSpan.FromSeconds(20), ct);
        var listener = await ChatListener.StartAsync(client, Accept, ct);
        lock (_gate) { _listeners[runner.Spec.Id] = listener; _runners[runner.Spec.Id] = runner; }

        // An account's own world room is followed for it - unless it is a guest, whose home is
        // whatever new server it was dropped on and is only here to lend the chat link.
        // An archived or deleted room stays out: the account's owner took it out of use.
        if (runner.Spec.Kind != "guest" && !_store.States().ContainsKey($"country_{client.Server.Value}"))
            Follow($"country_{client.Server.Value}", true);
        foreach (var room in Followed) CatchUp(room, null);
    }

    public async Task Detach(string runnerId)
    {
        ChatListener? l;
        lock (_gate) { _runners.Remove(runnerId); if (!_listeners.Remove(runnerId, out l)) return; }
        await l.DisposeAsync();
    }

    void Accept(IReadOnlyList<ChatMessage> messages)
    {
        List<ChatMessage> fresh;
        // Private rooms (DMs, player-made groups) never reach the store or the live feed: viewers read
        // chat. The admin Inbox reads them live from the game instead (Inbox.cs).
        messages = [.. messages.Where(m => !ChatStore.IsPrivate(m.RoomId))];
        if (messages.Count == 0) return;
        try { fresh = _store.InsertNew([.. messages]); }
        catch (Exception ex) { LastError = $"store: {ex.Message}"; return; }
        if (fresh.Count == 0) return;
        Want(fresh.Select(m => m.SenderUid));
        lock (_gate)
            foreach (var m in fresh.OrderBy(m => m.SeqId))
            {
                var frame = $"data: {JsonSerializer.Serialize(Shape(m, null, _users.GetValueOrDefault(m.SenderUid)))}\n\n";
                foreach (var s in _subs) s.Writer.TryWrite(frame);
            }
    }

    async Task PollLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(PollEvery, _cts.Token); } catch (OperationCanceledException) { return; }
            ChatListener? l;
            string[] rooms;
            lock (_gate)
            {
                l = _listeners.Values.FirstOrDefault(x => x.Alive);
                rooms = [.. _follow.Except(_catching)];
            }
            if (l is null || rooms.Length == 0) continue;
            try
            {
                // ponytail: ten rooms per request is a guess at a safe batch; nothing has been
                // seen to refuse more. Raise it if following dozens of servers.
                foreach (var chunk in rooms.Chunk(10))
                    Accept(await l.NewestAsync(chunk, _cts.Token));
                LastPollAt = DateTimeOffset.UtcNow;
                LastError = null;
                await LookupUsersAsync();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { LastError = $"poll: {ex.Message}"; }
        }
    }

    // ---- browsers

    public async IAsyncEnumerable<string> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var ch = Channel.CreateBounded<string>(
            new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _subs.Add(ch);
        try { await foreach (var m in ch.Reader.ReadAllAsync(ct)) yield return m; }
        finally { lock (_gate) _subs.Remove(ch); }
    }

    // ---- who is speaking

    static readonly TimeSpan UserFresh = TimeSpan.FromHours(6);

    /// <summary>
    /// Queues senders for a profile lookup. A chat message only carries name and tag; the server
    /// a player is FROM (what the game prints in chat, and what changes when they teleport over
    /// for a war) comes from get.user.info.multi, which answers for any server from any account.
    /// </summary>
    public void Want(IEnumerable<string> uids)
    {
        lock (_gate)
            foreach (var u in uids)
                if (u.Length > 6 && u.All(char.IsAsciiDigit)
                    && (!_users.TryGetValue(u, out var have) || DateTimeOffset.UtcNow - have.TakenAt > UserFresh))
                    _want.Add(u);
    }

    public Dictionary<string, PlayerProfile> Users(IEnumerable<string> uids)
    {
        var list = uids.Distinct().ToList();
        Dictionary<string, PlayerProfile> outp;
        lock (_gate) outp = list.Where(_users.ContainsKey).ToDictionary(u => u, u => _users[u]);
        var missing = list.Where(u => !outp.ContainsKey(u)).ToList();
        if (missing.Count > 0)
            foreach (var (u, x) in _profiles.Latest(missing))
            {
                outp[u] = x;
                lock (_gate) _users.TryAdd(u, x);
            }
        Want(list);
        return outp;
    }

    /// <summary>
    /// One batched lookup per poll, on the game socket of whichever runner is live (through its
    /// call lock, so it never interleaves with an operation). Results land in player_profile as
    /// source "chat" and are pushed to browsers as a "users" event so names gain their server.
    /// A range sweep's rows count too: anyone swept in the last few hours is not asked again.
    /// </summary>
    async Task LookupUsersAsync()
    {
        string[] batch;
        Runner? runner;
        lock (_gate)
        {
            runner = _runners.Values.FirstOrDefault(r => r.State == RunnerState.Ready);
            batch = [.. _want.Take(200)];
        }
        if (runner is null || batch.Length == 0) return;

        var held = _profiles.Latest(batch).Values.Where(x => DateTimeOffset.UtcNow - x.TakenAt <= UserFresh).ToList();
        var ask = batch.Except(held.Select(h => h.Uid)).ToArray();
        var got = new List<PlayerProfile>(held);
        if (ask.Length > 0)
        {
            try
            {
                var fetched = await runner.WithSessionAsync(client => ProfileLookup.FetchAsync(
                    client, ask, DateTimeOffset.UtcNow, "chat", TimeSpan.FromSeconds(10)));
                _profiles.Insert(fetched);
                got.AddRange(fetched);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException)
            {
                LastError = $"user lookup: {ex.Message}";
                return;
            }
        }

        lock (_gate)
        {
            foreach (var u in batch) _want.Remove(u);
            foreach (var x in got) _users[x.Uid] = x;
            if (got.Count == 0) return;
            var frame = $"event: users\ndata: {JsonSerializer.Serialize(got.ToDictionary(x => x.Uid, UserShape))}\n\n";
            foreach (var s in _subs) s.Writer.TryWrite(frame);
        }
    }

    static object? UserShape(PlayerProfile? u) => u is null ? null : new
    {
        server = u.HomeServer,
        current = u.CurrentServer,
        power = u.Power,
        level = u.Level,
        lang = u.Lang,
        country = u.Country,
        allianceId = u.AllianceId,
        at = u.TakenAt,
    };
    // ---- translation

    /// <summary>
    /// A message in <paramref name="lang"/>: the stored translation if there is one, otherwise the
    /// game's translation service, fed the dozen messages before it as context, and then stored.
    /// Borrows the credentials of whichever session is live; nothing goes over the game socket.
    /// </summary>
    public async Task<ChatTranslation> TranslateAsync(string room, long seq, string lang, bool refresh, CancellationToken ct)
    {
        var m = _store.Get(room, seq) ?? throw new KeyNotFoundException($"{room} has no message {seq} stored");
        if (!refresh && _store.Translations(room, lang, seq, seq).TryGetValue(seq, out var held))
            return new ChatTranslation(room, seq, lang, held.Text, held.SourceLang);

        LastWar.Client.LastWarClient? session;
        lock (_gate) session = _runners.Values.Select(r => r.Client).FirstOrDefault(c => c is not null);
        var creds = session?.ChatCredentials
            ?? throw new InvalidOperationException("translation needs a live session - go live first");
        var context = _store.Page(room, seq, 12);
        var (done, raw) = await _translator.TranslateAsync(creds, session!.Server.Value, lang, [m], context, SrcLang(m.Raw), ct);
        var t = done.FirstOrDefault(d => d.SeqId == seq)
            ?? throw new InvalidOperationException($"no translation came back: {(raw.Length > 300 ? raw[..300] : raw)}");
        _store.SaveTranslation(room, seq, lang, t.Text, t.SourceLang);
        return t;
    }

    /// <summary>The sender's language as their client reported it (extra.srcLang, e.g. zh_TW).</summary>
    static string SrcLang(string raw)
    {
        try
        {
            using var d = JsonDocument.Parse(raw);
            return d.RootElement.TryGetProperty("extra", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("srcLang", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    public static object Shape(ChatMessage m, (string Text, string SourceLang)? translation, PlayerProfile? user) => new
    {
        room = m.RoomId,
        seq = m.SeqId,
        uid = m.SenderUid,
        name = m.SenderName,
        alliance = m.SenderAlliance,
        origin = m.SenderOriginServer?.Value,
        text = m.Text,
        at = m.SentAt,
        type = m.Type,
        gift = m.Text.Length == 0 ? GiftOf(m.Raw) : null,
        translation = translation is { } t ? new { text = t.Text, from = t.SourceLang } : null,
        sender = UserShape(user),
    };

    /// <summary>
    /// Empty-text messages from "system" are chat gifts: who sent how many to whom sits in
    /// extra.customJsonParam, a JSON document inside a string. Null for anything else.
    /// </summary>
    static object? GiftOf(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("extra", out var extra)
                || !extra.TryGetProperty("customJsonParam", out var p) || p.ValueKind != JsonValueKind.String)
                return null;
            using var g = JsonDocument.Parse(p.GetString()!);
            var r = g.RootElement;
            string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var to = r.TryGetProperty("targetInfo", out var t) ? t : default;
            var from = r.TryGetProperty("senderInfo", out var f) ? f : default;
            return new
            {
                from = S(r, "sendName"),
                fromUid = S(r, "sendUid"),
                fromAlliance = from.ValueKind == JsonValueKind.Object ? S(from, "abbr") : "",
                to = to.ValueKind == JsonValueKind.Object ? S(to, "name") : "",
                toUid = to.ValueKind == JsonValueKind.Object ? S(to, "uid") : "",
                num = r.TryGetProperty("num", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 1,
                anonymous = r.TryGetProperty("isAnonymous", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 1,
            };
        }
        catch (JsonException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        string[] ids;
        lock (_gate) ids = [.. _listeners.Keys];
        foreach (var id in ids) await Detach(id);
        try { await _poll; } catch { /* shutting down */ }
    }
}
