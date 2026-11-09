using System.Text.Json;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using LastWar.Client;
using LastWar.Client.Auth;
using LastWar.Protocol.Sfs;

namespace LastWar.Server;

public enum RunnerState { Stopped, Connecting, Ready, Faulted }

/// <summary>Anything a runner emits: state changes, wire frames, pushes, call results.</summary>
public sealed record RunnerEvent(long Seq, DateTimeOffset At, string Kind, JsonNode? Data);

/// <summary>How a runner is configured. One file per runner under data/runners/.</summary>
public sealed record RunnerSpec
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ConfigPath { get; init; } = "";
    public string Note { get; init; } = "";
    /// <summary>guest, account, or external for a session file owned by something else.</summary>
    public string Kind { get; init; } = "external";
    /// <summary>guest / service / privileged (see AccountTier). Empty = derived from Kind.</summary>
    public string Tier { get; init; } = "";

    /// <summary>
    /// Unset tiers err towards caution: a guest is a guest, an account signed in by email (a real
    /// player's own) is privileged, and anything else - the pre-existing default session - is service.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public LastWar.Operations.AccountTier AccountTier => Tier.Length > 0 ? LastWar.Operations.AccountTiers.Parse(Tier)
        : Kind == "guest" ? LastWar.Operations.AccountTier.Guest
        : Kind == "account" ? LastWar.Operations.AccountTier.Privileged
        : LastWar.Operations.AccountTier.Service;
}

/// <summary>
/// One game session, owned by the dashboard: connect, call commands, observe the wire.
///
/// A runner is deliberately a thin box around <see cref="LastWarClient"/> rather than a
/// job scheduler. That keeps the fleet story simple - N runners are N of these, and a
/// scheduler on top can stay a separate concern.
///
/// ponytail: events are a bounded ring plus bounded per-subscriber channels, so a slow
/// browser drops old frames instead of growing memory. Upgrade path if history matters:
/// tee the ring to disk.
/// </summary>
public sealed class Runner(RunnerSpec spec)
{
    const int RingSize = 1000;

    readonly List<RunnerEvent> _ring = new();
    readonly List<Channel<RunnerEvent>> _subs = new();
    readonly SemaphoreSlim _callLock = new(1, 1);   // one request in flight per connection
    long _seq;

    public RunnerSpec Spec { get; set; } = spec;   // settable: the tier can be changed from the Sessions page
    public RunnerState State { get; private set; } = RunnerState.Stopped;
    public string? Error { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public int ServerId { get; private set; }
    public string Uid { get; private set; } = "";
    public long Calls { get; private set; }
    public long Frames { get; private set; }

    LastWarClient? _client;

    /// <summary>The live session, for things that ride on it (chat); null when stopped.</summary>
    public LastWarClient? Client => _client;

    public object Snapshot() => new
    {
        id = Spec.Id,
        name = Spec.Name,
        note = Spec.Note,
        kind = Spec.Kind,
        tier = LastWar.Operations.AccountTiers.Name(Spec.AccountTier),
        configPath = Spec.ConfigPath,
        state = State.ToString().ToLowerInvariant(),
        error = Error,
        startedAt = StartedAt,
        serverId = ServerId,
        uid = Uid,
        calls = Calls,
        frames = Frames,
    };

    // ---- event fan-out

    void Emit(string kind, JsonNode? data)
    {
        var e = new RunnerEvent(Interlocked.Increment(ref _seq), DateTimeOffset.UtcNow, kind, data);
        lock (_ring)
        {
            _ring.Add(e);
            if (_ring.Count > RingSize) _ring.RemoveRange(0, _ring.Count - RingSize);
            foreach (var s in _subs) s.Writer.TryWrite(e);
        }
    }

    /// <summary>Replays the ring, then streams live events until the caller stops reading.</summary>
    public async IAsyncEnumerable<RunnerEvent> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var ch = Channel.CreateBounded<RunnerEvent>(
            new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
        RunnerEvent[] backlog;
        lock (_ring) { backlog = _ring.ToArray(); _subs.Add(ch); }
        try
        {
            foreach (var e in backlog) yield return e;
            await foreach (var e in ch.Reader.ReadAllAsync(ct)) yield return e;
        }
        finally { lock (_ring) _subs.Remove(ch); }
    }

    // ---- lifecycle

    /// <summary>True once this runner has tried to log in. For a guest that is its one login spent.</summary>
    public bool LoginAttempted { get; private set; }

    /// <summary>A guest that has logged in once can never log in again (ec=28 E011).</summary>
    public bool Spent => Spec.Kind == "guest" && LoginAttempted;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (State is RunnerState.Ready or RunnerState.Connecting)
            throw new InvalidOperationException("runner is already running");

        State = RunnerState.Connecting;
        LoginAttempted = true;
        Error = null;
        Emit("state", new JsonObject { ["state"] = "connecting" });
        try
        {
            var config = SessionConfig.Load(Spec.ConfigPath);
            var identity = GameIdentity.LoadOrCreate(Path.GetDirectoryName(Spec.ConfigPath)!);
            var client = await LastWarClient.ConnectAsync(config, identity, ct);

            client.Connection.Frame += OnFrame;
            _client = client;
            ServerId = client.Server.Value;
            Uid = config.GameUid;
            StartedAt = DateTimeOffset.UtcNow;
            State = RunnerState.Ready;
            Emit("state", new JsonObject { ["state"] = "ready", ["serverId"] = ServerId });
        }
        catch (Exception ex)
        {
            State = RunnerState.Faulted;
            Error = ex.Message;
            Emit("error", new JsonObject { ["message"] = ex.Message });
            throw;
        }
    }

    void OnFrame(FrameRecord f)
    {
        Frames++;
        Emit("frame", new JsonObject
        {
            ["dir"] = f.Outbound ? "tx" : "rx",
            ["bytes"] = f.Body.Length,
            ["controller"] = f.Controller,
            ["action"] = f.Action,
            ["command"] = f.Command,
            ["hex"] = Convert.ToHexString(f.Body.AsSpan(0, Math.Min(f.Body.Length, 4096))),
            ["truncated"] = f.Body.Length > 4096,
            // Decoded both ways. The tap sees the same object the codec built, so a request can
            // be read as JSON instead of hex - and reading it here never consumes the channel
            // that awaited replies come from.
            ["params"] = f.Params.Count == 0 ? null : SfsJson.Write(f.Params),
        });
    }

    /// <summary>
    /// Waits out the login burst. A request sent while it is still arriving is never answered,
    /// so anything that starts a session and immediately uses it must call this first.
    /// </summary>
    public async Task SettleAsync(CancellationToken ct = default)
    {
        if (_client is null) return;
        if (LastWar.Client.Game.LoginPushRecorder.For(_client.Connection) is { } rec)
            await rec.WaitAsync(TimeSpan.FromSeconds(20), ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
    }

    public async Task StopAsync()
    {
        var c = _client;
        _client = null;
        State = RunnerState.Stopped;
        if (c is not null) { c.Connection.Frame -= OnFrame; await c.DisposeAsync(); }
        Emit("state", new JsonObject { ["state"] = "stopped" });
    }

    // ---- calling commands

    /// <summary>
    /// Runs a named operation against this runner's session. It shares the call lock, so an
    /// operation and an ad-hoc command can never interleave on one connection.
    /// </summary>
    public async Task<JsonNode> RunOperationAsync(
        string id, IReadOnlyDictionary<string, string> args, string database, string dataDir, CancellationToken ct,
        IProgress<string>? progress = null)
    {
        var info = LastWar.Operations.Operations.Find(id)
            ?? throw new KeyNotFoundException($"no operation '{id}'");
        if (info.NeedsSession && _client is null)
            throw new InvalidOperationException("this operation needs a running session");

        await _callLock.WaitAsync(ct);
        var started = DateTimeOffset.UtcNow;
        try
        {
            Emit("operation", new JsonObject { ["id"] = id, ["state"] = "started" });
            var ctx = new LastWar.Operations.OperationContext
            {
                Args = args,
                Database = database,
                DataDir = dataDir,
                Session = _client,
                Cancellation = ct,
                Progress = new Progress<string>(m =>
                {
                    Emit("progress", new JsonObject { ["id"] = id, ["message"] = m });
                    progress?.Report(m);
                }),
            };
            var result = await LastWar.Operations.Operations.RunAsync(id, ctx);
            var ms = Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds);
            Emit("operation", new JsonObject
            {
                ["id"] = id, ["state"] = "done", ["ms"] = ms, ["summary"] = result.Summary,
            });
            return new JsonObject
            {
                ["ok"] = true,
                ["ms"] = ms,
                ["summary"] = result.Summary,
                ["tables"] = JsonSerializer.SerializeToNode(result.Tables.Select(t => new
                {
                    title = t.Title, columns = t.Columns, rows = t.Rows, brief = t.Brief, links = t.Links, totals = t.Totals,
                })),                ["columns"] = new JsonArray([.. result.Columns.Select(c => (JsonNode?)c)]),
                ["rows"] = new JsonArray([.. result.Rows.Select(row =>
                    (JsonNode?)new JsonArray([.. row.Select(v => (JsonNode?)v)]))]),
            };
        }
        catch (Exception ex)
        {
            Emit("error", new JsonObject { ["id"] = id, ["message"] = ex.Message });
            return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        }
        finally { _callLock.Release(); }
    }

    /// <summary>
    /// Borrows the session for work that talks to it directly, under the same call lock as
    /// everything else - so nothing interleaves with an operation or an ad-hoc command.
    /// </summary>
    public async Task<T> WithSessionAsync<T>(Func<LastWarClient, Task<T>> work, CancellationToken ct = default)
    {
        var client = _client ?? throw new InvalidOperationException("runner is not running");
        await _callLock.WaitAsync(ct);
        try { return await work(client); }
        finally { _callLock.Release(); }
    }
    public async Task<JsonNode> CallAsync(string command, JsonNode? parameters, int timeoutSeconds)
    {
        var client = _client ?? throw new InvalidOperationException("runner is not running");
        var ps = parameters is null ? new SfsObject() : SfsJson.Parse(parameters.ToJsonString());

        await _callLock.WaitAsync();
        var started = DateTimeOffset.UtcNow;
        try
        {
            Emit("call", new JsonObject { ["command"] = command, ["params"] = parameters?.DeepClone() });
            var reply = await client.CallAsync(command, ps, TimeSpan.FromSeconds(timeoutSeconds));
            var ms = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
            Calls++;
            var result = SfsJson.Write(reply);
            Emit("reply", new JsonObject { ["command"] = command, ["ms"] = Math.Round(ms), ["keys"] = reply.Count });
            return new JsonObject { ["ok"] = true, ["ms"] = Math.Round(ms), ["result"] = result };
        }
        catch (Exception ex)
        {
            Emit("error", new JsonObject { ["command"] = command, ["message"] = ex.Message });
            return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        }
        finally { _callLock.Release(); }
    }
}
