using System.Runtime.CompilerServices;
using LastWar.Client.Chat;
using LastWar.Protocol.Sfs;

namespace LastWar.Client.Game;

/// <summary>
/// Buildings and visitors are never requested - no "list my buildings" command exists. The server
/// pushes them once, during login, and they are gone by the time anyone asks.
///
/// So they are recorded as they go past, from the frame tap, rather than read off the inbound
/// channel. Reading them would mean pulling envelopes out of the queue that awaited command
/// replies come from, and it would work exactly once per connection: the first operation to ask
/// would consume the burst and every later one would see an empty base.
///
/// The recorder attaches at connect and keeps what it saw for as long as the connection lives, so
/// listing buildings, collecting resources and greeting visitors can each be run, in any order,
/// as many times as wanted.
/// </summary>
public sealed class LoginPushRecorder
{
    /// <summary>Flat bound on scan cost: no server-sent count exists to clamp against.</summary>
    const int MaxItemsPerPush = 2000;
    const int MaxBuildings = 300;
    const int MaxVisitors = 300;

    static readonly ConditionalWeakTable<GameConnection, LoginPushRecorder> Attached = new();

    readonly Dictionary<long, BuildingInfo> _buildings = [];
    readonly Dictionary<long, VisitorInfo> _visitors = [];
    readonly TaskCompletionSource _init = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock _gate = new();

    /// <summary>True once the authoritative init push has been seen on this connection.</summary>
    public bool HasInit => _init.Task.IsCompleted;

    /// <summary>
    /// The chat token, which is published exactly once - in the init push - and never appears in
    /// any command response. Tapping it here means chat can be opened at any point in a session's
    /// life, rather than only by a caller that happened to be waiting at login.
    /// </summary>
    public ChatCredentials? Chat { get; private set; }

    public static LoginPushRecorder Attach(GameConnection conn)
    {
        var recorder = Attached.GetValue(conn, _ => new LoginPushRecorder());
        conn.Frame += recorder.Observe;
        return recorder;
    }

    public static LoginPushRecorder? For(GameConnection conn) =>
        Attached.TryGetValue(conn, out var r) ? r : null;

    /// <summary>Waits for login to have delivered the burst, then hands back what it carried.</summary>
    public async Task<(List<BuildingInfo> Buildings, List<VisitorInfo> Visitors)> WaitAsync(
        TimeSpan wait, CancellationToken ct = default)
    {
        // A connection that has been up a while already has its pushes; only a fresh one waits.
        if (!HasInit)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(wait);
            try { await _init.Task.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* go with whatever arrived */ }
        }
        return Snapshot();
    }

    public (List<BuildingInfo> Buildings, List<VisitorInfo> Visitors) Snapshot()
    {
        lock (_gate) return ([.. _buildings.Values], [.. _visitors.Values]);
    }

    void Observe(FrameRecord f)
    {
        if (f.Outbound) return;
        switch (f.Command)
        {
            case "init":
                // building_new is what the game's own BuildManager reads. push.init.build is a
                // rarely-fired secondary push despite the more promising name.
                Add(f.Params, "building_new", null);
                lock (_gate)
                    foreach (var v in ParseVisitors(f.Params))
                        if (_visitors.Count < MaxVisitors) _visitors[v.Uid] = v;
                Chat ??= ReadChatCredentials(f.Params);
                _init.TrySetResult();
                break;

            case "push.init.build": Add(f.Params, "defaultBuilds", "buildInfo"); break;
            case "push.add.building": Add(f.Params, "buildings", null); break;

            // push.queue.add and push.build.queue.info carry production-queue items, which are
            // separate entities from buildings and contribute nothing here.
        }
    }

    void Add(SfsObject push, string arrayKey, string? wrapperKey)
    {
        if (!push.TryGetArray(arrayKey, out var arr)) return;
        lock (_gate)
            foreach (var entry in arr.Objects().Take(MaxItemsPerPush))
            {
                var b = entry;
                if (wrapperKey is not null && !entry.TryGetObject(wrapperKey, out b!)) continue;
                if (_buildings.Count >= MaxBuildings) return;
                var uuid = b.GetLong("uuid");
                _buildings[uuid] = new BuildingInfo(uuid, b.GetInt("bId"), b.GetInt("lv"), b.GetInt("pId"));
            }
    }

    static ChatCredentials? ReadChatCredentials(SfsObject init)
    {
        var token = init.GetString("chatToken");
        var uid = init.GetString("uid");
        if (uid.Length == 0 && init.TryGetObject("user", out var user)) uid = user.GetString("uid");
        return token.Length > 0 && uid.Length > 0
            ? new ChatCredentials(uid, token)
            : null;
    }

    static IEnumerable<VisitorInfo> ParseVisitors(SfsObject initParams) =>
        LoginPushes.ParseVisitors(initParams);
}
