using System.Text;
using System.Text.Json;

namespace LastWar.Client.Auth;

/// <summary>One role on one server, as returned by GSL or by an email login.</summary>
public sealed record Role
{
    public string Zone { get; init; } = "";
    public string GameUid { get; init; } = "";
    public string Ip { get; init; } = "";
    public int Port { get; init; }
    public string Name { get; init; } = "";
    public string Alliance { get; init; } = "";
    public long Power { get; init; }
    public int Level { get; init; }
    public string LoginKey { get; init; } = "";

    /// <summary>The ip field is a pipe-delimited fallback list; the first host is the one to dial.</summary>
    public string PrimaryHost => Ip.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    public int ServerId => Zone.Length > 3 && Zone.StartsWith("APS", StringComparison.Ordinal)
        ? int.TryParse(Zone[3..], out var n) ? n : 0
        : 0;
}

public sealed record GslResult(IReadOnlyList<Role> Roles, string AccessToken, string RefreshToken);

/// <summary>
/// The account gateway: the only HTTP in the whole login. Two calls -
/// getlsu3dversion.php hands back the RSA public key and fixes which host to use, then
/// getserverlist.php does the real work wrapped in <see cref="GslCrypto"/>.
///
/// The "opt" argument is what makes this one class serve every case: "new" bootstraps a
/// fresh guest, "fix" re-resolves a device that already has a gameUid, and "login"
/// redeems the one-shot loginKey an email login returns.
/// </summary>
public sealed class GslClient(HttpClient? http = null)
{
    public const string PackageName = "com.fun.lastwar.gp";
    public const string AppVersion = "1.0.351";
    public const string VersionCode = "1835";
    public const string Platform = "Android";
    const string UnityVersion = "440";
    const int MaxResponseBytes = 1 << 20;

    public static readonly string[] Hosts =
    [
        "https://lastwar-serverlist-cf.lastwarapp.net",
        "https://lastwar-serverlist-us-aws-ali.lastwargame.com",
        "https://lastwar-serverlist-us-gcp-ali.lastwargame.com",
    ];

    // The order of the plaintext form is this whitelist, NOT alphabetical, and empty
    // values are still emitted. Field count matters.
    static readonly string[] FormOrder =
    [
        "uuid", "airKey", "loginFlag", "country", "is3D", "lang", "simOp", "platform",
        "isSimulator", "zone", "gameuid", "newServer", "openCountry", "opt", "loginKey", "rt",
    ];

    readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public string GateHost { get; private set; } = "";
    System.Security.Cryptography.RSA? _publicKey;

    /// <summary>Walks the host list until one answers with code == "". That host then sticks.</summary>
    public async Task CheckVersionAsync(CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var host in Hosts)
        {
            try
            {
                var q = new Dictionary<string, string>
                {
                    ["packageName"] = PackageName,
                    ["platform"] = Platform,
                    ["appVersion"] = AppVersion,
                    ["gm"] = "0",
                    ["server"] = "",
                    ["uid"] = "",
                    ["deviceId"] = "",          // deliberately empty on this call only
                    ["table_env"] = "",
                    ["buildId"] = VersionCode,
                    ["returnJson"] = "1",
                    ["unityVersion"] = UnityVersion,
                };
                var url = host + "/gameservice/getlsu3dversion.php?" +
                          string.Join("&", q.OrderBy(k => k.Key, StringComparer.Ordinal)
                              .Select(k => $"{Uri.EscapeDataString(k.Key)}={Uri.EscapeDataString(k.Value)}"));

                var body = await ReadCappedAsync(await _http.GetAsync(url, ct), ct);
                using var doc = JsonDocument.Parse(body);
                if (Str(doc.RootElement, "code").Length != 0)
                    throw new InvalidOperationException($"check-version refused: code={Str(doc.RootElement, "code")}");

                var key = Str(doc.RootElement, "resMsg");
                if (key.Length == 0) throw new InvalidOperationException("check-version returned no public key");

                _publicKey = GslCrypto.ParsePublicKey(key);
                GateHost = host;
                return;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException("no account gateway answered", last);
    }

    public async Task<GslResult> GetServerListAsync(
        string deviceId, string opt, string zone = "", string gameUid = "",
        string loginKey = "", string refreshToken = "", CancellationToken ct = default)
    {
        if (_publicKey is null) await CheckVersionAsync(ct);

        var form = new List<KeyValuePair<string, string>>
        {
            new("uuid", deviceId),
            new("airKey", GslCrypto.AirKey(deviceId)),
            new("loginFlag", "1"),
            new("country", "US"),
            new("is3D", "1"),
            new("lang", "en"),
            new("simOp", ""),
            new("platform", Platform),
            new("isSimulator", "0"),
            new("zone", zone),
            new("gameuid", gameUid),
            new("newServer", "1"),
            new("openCountry", "US"),
            // the app leaves opt out entirely while it holds a fresh access token (NetworkManager.GetServerListRequest)
        };
        if (opt.Length > 0) form.Add(new("opt", opt));
        if (opt == "login") form.Add(new("loginKey", loginKey));
        if (opt == "refresh") form.Add(new("rt", refreshToken));

        var plain = string.Join("&", FormOrder
            .Where(k => form.Any(f => f.Key == k))
            .Select(k => $"{k}={form.First(f => f.Key == k).Value}"));
        if (plain.Contains('&') && form.Any(f => f.Value.Contains('&')))
            throw new InvalidOperationException("a form value contains '&' and would corrupt the request");

        var crypto = new GslCrypto(_publicKey!);
        var (uuid, data) = crypto.EncryptRequest(plain);

        using var content = new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("data", data), new KeyValuePair<string, string>("uuid", uuid)]);
        var raw = await ReadCappedAsync(
            await _http.PostAsync(GateHost + "/gameservice/getserverlist.php", content, ct), ct);

        // The payload is either encrypted under "bin" or the body itself. Present but
        // empty is an error, not an empty success.
        using var outer = JsonDocument.Parse(raw);
        var json = raw;
        if (outer.RootElement.TryGetProperty("bin", out var bin))
        {
            var text = bin.ValueKind == JsonValueKind.String ? bin.GetString()! : "";
            if (text.Length == 0) throw new InvalidOperationException("GSL response: bin present but empty");
            json = crypto.DecryptResponse(text);
        }
        return ParseResult(json);
    }

    static GslResult ParseResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var roles = new List<Role>();

        if (root.TryGetProperty("serverList", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var s in list.EnumerateArray())
                roles.Add(new Role
                {
                    Zone = Str(s, "zone"),
                    GameUid = Str(s, "gameUid"),
                    Ip = Str(s, "ip"),
                    Port = Num(s, "port"),
                    Name = Str(s, "name"),
                });

        // opt=new often answers with no serverList at all: the account server itself is
        // the place to dial, and zone/gameUid arrive later from the login reply.
        if (roles.Count == 0 && root.TryGetProperty("loginServer", out var ls) && ls.ValueKind == JsonValueKind.Object)
            roles.Add(new Role { Ip = Str(ls, "ip"), Port = Num(ls, "port") });

        return new GslResult(roles, Token(root, "at"), Token(root, "rt"));
    }

    static string Token(JsonElement root, string name) =>
        root.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Object ? Str(t, "token") : "";

    /// <summary>Every scalar in these responses may be a string or a bare number.</summary>
    public static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.ToString(),
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => v.ToString(),
            }
            : "";

    public static int Num(JsonElement el, string name) =>
        int.TryParse(Str(el, name), out var n) ? n : 0;

    static async Task<string> ReadCappedAsync(HttpResponseMessage res, CancellationToken ct)
    {
        res.EnsureSuccessStatusCode();
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > MaxResponseBytes) throw new InvalidOperationException("gateway response too large");
        return Encoding.UTF8.GetString(bytes);
    }
}
