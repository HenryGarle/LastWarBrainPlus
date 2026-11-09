using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace LastWar.Client.Chat;

/// <summary>One inbound chat frame. Everything on this socket is JSON: {cmd, data|result, ...}.</summary>
public sealed record ChatFrame(string Command, JsonElement Root, string Raw)
{
    public JsonElement? Body =>
        Root.TryGetProperty("data", out var d) ? d
        : Root.TryGetProperty("result", out var r) ? r
        : null;
}

/// <summary>
/// The chat WebSocket.
///
/// Handshake auth is five headers. SIGN carries no secret - CHATTOKEN is the actual
/// credential, taken from the game login's init push. A bad handshake is DROPPED
/// silently rather than refused, so a hang here means wrong headers or the wrong
/// gateway, never a network fault.
///
/// Use the endpoints from `data_new`; the legacy `data` hosts accept TCP on 443 but
/// never complete the upgrade.
/// </summary>
public sealed class ChatConnection : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    string _uid = "";
    string _handshakeSign = "";
    readonly Channel<ChatFrame> _inbound = Channel.CreateUnbounded<ChatFrame>();
    readonly CancellationTokenSource _cts = new();
    Task? _reader;

    public static string Sign(string appId, string uid, long time) =>
        ChatDirectory.Md5(ChatDirectory.Md5(appId + uid) + time);

    public static async Task<ChatConnection> ConnectAsync(
        ChatEndpoint endpoint, string uid, string chatToken,
        string appId = ChatDirectory.AppId, CancellationToken ct = default)
    {
        var c = new ChatConnection();
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        c._ws.Options.SetRequestHeader("APPID", appId);
        c._ws.Options.SetRequestHeader("TIME", time.ToString());
        c._ws.Options.SetRequestHeader("UID", uid);
        c._ws.Options.SetRequestHeader("SIGN", Sign(appId, uid, time));   // also the per-message salt
        c._ws.Options.SetRequestHeader("CHATTOKEN", chatToken);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await c._ws.ConnectAsync(endpoint.Uri, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            c._ws.Dispose();
            throw new TimeoutException(
                $"{endpoint.Uri} did not complete the WebSocket upgrade. The chat gateway drops " +
                "bad handshakes silently - check CHATTOKEN freshness and that this is a data_new endpoint.");
        }
        c._uid = uid;
        c._handshakeSign = Sign(appId, uid, time);
        c._reader = Task.Run(c.ReadLoopAsync);
        return c;
    }

    async Task ReadLoopAsync()
    {
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;

                var raw = sb.ToString();
                sb.Clear();
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement.Clone();
                    var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
                    await _inbound.Writer.WriteAsync(new ChatFrame(cmd, root, raw), _cts.Token)
                        .ConfigureAwait(false);
                }
                catch (JsonException) { /* non-JSON frame: ignore rather than kill the stream */ }
            }
        }
        catch (Exception ex) { _inbound.Writer.TryComplete(ex is OperationCanceledException ? null : ex); }
        finally { _inbound.Writer.TryComplete(); }
    }

    /// <summary>
    /// Recursively rewrites an object with its keys in ordinal order. The signature is
    /// taken over the SORTED JSON (Lua `serialize_sorted`), so key order is part of the
    /// protocol, not cosmetic.
    /// </summary>
    static JsonNode? Sorted(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var sorted = new JsonObject();
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                    sorted[kv.Key] = Sorted(kv.Value?.DeepClone());
                return sorted;
            }
            case JsonArray a:
            {
                var arr = new JsonArray();
                foreach (var item in a) arr.Add(Sorted(item?.DeepClone()));
                return arr;
            }
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// sign = md5( uid + json(sorted params) + sendTime + handshakeSign ).
    /// Unsigned commands come back as error 1003 "no permission" - the gateway accepts the
    /// socket but refuses every request on it.
    /// </summary>
    public async Task SendSignedAsync(
        string command, object? parameters = null, CancellationToken ct = default)
    {
        var node = parameters is null
            ? new JsonObject()
            : JsonSerializer.SerializeToNode(parameters) ?? new JsonObject();
        var sorted = Sorted(node)!;
        var json = sorted.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var sendTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sign = ChatDirectory.Md5(_uid + json + sendTime + _handshakeSign);

        var envelope = new JsonObject
        {
            ["cmd"] = command,
            ["params"] = sorted,
            ["sendTime"] = sendTime,
            ["sign"] = sign,
        };
        await _ws.SendAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    public async Task SendAsync(string command, object? parameters = null, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["cmd"] = command,
            ["params"] = parameters ?? new Dictionary<string, object?>(),
        });
        await _ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Sends a command and waits for the first frame carrying the same cmd back.</summary>
    public async Task<ChatFrame?> CallAsync(
        string command, object? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await SendAsync(command, parameters, ct).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            await foreach (var f in _inbound.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
                if (f.Command == command) return f;
        }
        catch (OperationCanceledException) { }
        return null;
    }

    /// <summary>Every frame as it arrives, including server pushes.</summary>
    public IAsyncEnumerable<ChatFrame> Frames(CancellationToken ct = default) =>
        _inbound.Reader.ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _ws.Abort(); } catch { /* already gone */ }
        if (_reader is not null) { try { await _reader.ConfigureAwait(false); } catch { } }
        _ws.Dispose();
        _cts.Dispose();
    }
}
