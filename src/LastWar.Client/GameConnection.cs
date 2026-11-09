using System.Net.Sockets;
using System.Threading.Channels;
using LastWar.Protocol.Sfs;

namespace LastWar.Client;

/// <summary>An inbound envelope: controller + action, with its content object.</summary>
public sealed record Envelope(byte Controller, short Action, SfsObject Content)
{
    public const byte ControllerSystem = 0;
    public const byte ControllerExtension = 1;
    public const short ActionHandshake = 0;
    public const short ActionLogin = 1;
    public const short ActionCallExtension = 13;
    public const short ActionPingPong = 29;

    /// <summary>Extension messages carry the command name in "c" and its payload in "p".</summary>
    public bool IsExtension => Controller == ControllerExtension;
    public string Command => IsExtension ? Content.GetString("c") : "";
    public SfsObject Params =>
        IsExtension && Content.TryGetObject("p", out var p) ? p : new SfsObject();
}

/// <summary>
/// A live SFS2X connection: framing, a background reader, and a heartbeat.
///
/// Inbound envelopes go to an unbounded channel. Callers await a specific reply with
/// <see cref="WaitForCommandAsync"/>; everything else stays in the channel for
/// <see cref="Pushes"/> to drain.
///
/// ponytail: one request in flight at a time, matched by command name. The protocol
/// carries a client-generated "_id" that would allow pipelining, but every scan this
/// project performs is a sequential request/response walk, so the simpler model holds.
/// Upgrade path if throughput ever matters: key pending completions by "_id".
/// </summary>
/// <summary>One frame as it crossed the socket, for observers that want the wire view.</summary>
public sealed record FrameRecord(
    DateTimeOffset At, bool Outbound, byte[] Body, byte Controller, short Action, string Command,
    SfsObject Params);

public sealed class GameConnection : IAsyncDisposable
{
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly Channel<Envelope> _inbound = Channel.CreateUnbounded<Envelope>();
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    Task? _reader;
    Task? _heartbeat;

    /// <summary>
    /// Raised for every decoded frame in both directions. Costs nothing when nobody is
    /// subscribed, which is the normal case for scans - only the dashboard listens.
    /// Handlers must not throw and must not block; this runs on the read/write path.
    ///
    /// This is a TAP, not a consumer: inbound envelopes still go to the channel that
    /// WaitForCommandAsync reads. An observer that instead drained Pushes() would steal
    /// replies out from under callers, which shows up as every command timing out.
    /// </summary>
    public event Action<FrameRecord>? Frame;

    void Observe(bool outbound, byte[] body, SfsObject outer)
    {
        if (Frame is not { } handler) return;
        var e = new Envelope((byte)outer.GetInt("c"), (short)outer.GetInt("a"),
            outer.TryGetObject("p", out var p) ? p : new SfsObject());
        try { handler(new FrameRecord(DateTimeOffset.UtcNow, outbound, body, e.Controller, e.Action, e.Command, e.Params)); }
        catch { /* an observer must never break the connection */ }
    }

    GameConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public string RemoteEndpoint => _tcp.Client.RemoteEndPoint?.ToString() ?? "";

    /// <summary>The state server this socket is attached to: port = 10000 + serverId.</summary>
    public int ServerId =>
        _tcp.Client.RemoteEndPoint is System.Net.IPEndPoint ep && ep.Port > 10000 ? ep.Port - 10000 : 0;

    public static async Task<GameConnection> ConnectAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
        var c = new GameConnection(tcp);
        c.Start();
        return c;
    }

    void Start()
    {
        _reader = Task.Run(ReadLoopAsync);
        _heartbeat = Task.Run(HeartbeatAsync);
    }

    async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var body = await SfsPacket.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                var outer = SfsCodec.Decode(body);
                Observe(false, body, outer);
                var controller = (byte)outer.GetInt("c");
                var action = (short)outer.GetInt("a");
                outer.TryGetObject("p", out var content);
                await _inbound.Writer.WriteAsync(
                    new Envelope(controller, action, content ?? new SfsObject()), _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _inbound.Writer.TryComplete(ex is OperationCanceledException ? null : ex);
        }
        finally
        {
            _inbound.Writer.TryComplete();
        }
    }

    async Task HeartbeatAsync()
    {
        var start = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                var pp = new SfsObject();
                pp.PutLong("clientTime", (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds);
                await SendEnvelopeAsync(Envelope.ControllerSystem, Envelope.ActionPingPong, pp)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { await DisposeAsync().ConfigureAwait(false); }
    }

    public async Task SendEnvelopeAsync(byte controller, short action, SfsObject content)
    {
        var outer = new SfsObject();
        outer.Put("c", new SfsValue(SfsType.Byte, controller));
        outer.Put("a", new SfsValue(SfsType.Short, action));
        outer.PutObject("p", content);
        var body = SfsCodec.Encode(outer);
        Observe(true, body, outer);
        var frame = SfsPacket.Encode(body);

        await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try { await _stream.WriteAsync(frame, _cts.Token).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Sends an extension command (the "p" of an ActionCallExtension envelope).</summary>
    public Task SendCommandAsync(string command, SfsObject? parameters = null)
    {
        var content = new SfsObject();
        content.PutUtfString("c", command);
        content.PutInt("r", -1);
        content.PutObject("p", parameters ?? new SfsObject());
        return SendEnvelopeAsync(Envelope.ControllerExtension, Envelope.ActionCallExtension, content);
    }

    /// <summary>Waits for the next envelope matching <paramref name="match"/>, discarding others.</summary>
    public async Task<Envelope> WaitForAsync(
        Func<Envelope, bool> match, TimeSpan timeout, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(timeout);
        try
        {
            await foreach (var e in _inbound.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
                if (match(e)) return e;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no matching message within {timeout.TotalSeconds:0.#}s");
        }
        throw new IOException("connection closed while waiting for a message");
    }

    public Task<Envelope> WaitForCommandAsync(
        TimeSpan timeout, CancellationToken ct = default, params string[] commands) =>
        WaitForAsync(e => e.IsExtension && commands.Contains(e.Command), timeout, ct);

    /// <summary>Everything the reader received that nobody awaited - server pushes.</summary>
    public IAsyncEnumerable<Envelope> Pushes(CancellationToken ct = default) =>
        _inbound.Reader.ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _tcp.Dispose();
        foreach (var t in new[] { _reader, _heartbeat })
            if (t is not null) { try { await t.ConfigureAwait(false); } catch { /* shutting down */ } }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
