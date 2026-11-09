using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LastWar.Client.Chat;

public sealed record ChatEndpoint(string Ip, int Port, string Protocol)
{
    public Uri Uri => new($"{Protocol}://{Ip}:{Port}/");
}

/// <summary>
/// Step one of reaching chat: ask the server-list service which WebSocket gateways to
/// use. A plain form POST - nothing about chat is encrypted beyond TLS.
/// </summary>
public sealed class ChatDirectory(HttpClient? http = null)
{
    public const string AppId = "100017";

    public static readonly string[] Urls =
    [
        "https://lastwar-serverlist-cf.lastwarapp.net/gameservice/chat_server_links.php",
        "https://lastwar-serverlist-us-aws-ali.lastwargame.com/gameservice/chat_server_links.php",
        "https://lastwar-serverlist-us-gcp-ali.lastwargame.com/gameservice/chat_server_links.php",
    ];

    readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// s = md5( appId + uid + md5( md5(first 3 chars of t) + md5(last 3 chars of t) ) ).
    /// Reproduced from ChatService.RequestServerList; the halves really are 3 characters.
    /// </summary>
    public static string RequestSignature(string appId, string uid, string t) =>
        Md5(appId + uid + Md5(Md5(t[..3]) + Md5(t[^3..])));

    public async Task<(IReadOnlyList<ChatEndpoint> Preferred, IReadOnlyList<ChatEndpoint> Legacy)>
        FetchAsync(string uid, int serverId, CancellationToken ct = default)
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var form = new Dictionary<string, string>
        {
            ["t"] = t,
            ["s"] = RequestSignature(AppId, uid, t),
            ["a"] = AppId,
            ["u"] = uid,
            ["serverId"] = serverId.ToString(),
        };

        Exception? last = null;
        foreach (var url in Urls)
        {
            try
            {
                using var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct)
                    .ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.GetProperty("code").GetInt32() != 1)
                    throw new InvalidOperationException(
                        $"chat server list refused: {root.GetProperty("message").GetString()}");
                return (Read(root, "data_new"), Read(root, "data"));
            }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException("no chat server list endpoint answered", last);
    }

    static IReadOnlyList<ChatEndpoint> Read(JsonElement root, string key) =>
        root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(e => new ChatEndpoint(
                e.GetProperty("ip").GetString() ?? "",
                e.GetProperty("port").GetInt32(),
                e.GetProperty("protocol").GetString() ?? "wss")).ToList()
            : [];

    internal static string Md5(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
