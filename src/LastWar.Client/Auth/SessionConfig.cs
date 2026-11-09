using System.Text.Json;
using System.Text.Json.Serialization;

namespace LastWar.Client.Auth;

/// <summary>
/// A saved session: enough to reconnect to a state server directly, with no GSL call.
/// This is the path that keeps working when an account's loginKey no longer resolves
/// through GSL ("no servers returned") but its access token is still live.
/// Mirrors ~/.lastwar_goclient_session.json so existing sessions are reusable.
/// </summary>
public sealed record SessionConfig
{
    [JsonPropertyName("ip")] public string Ip { get; init; } = "";
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("zone")] public string Zone { get; init; } = "";
    [JsonPropertyName("gameUid")] public string GameUid { get; init; } = "";
    [JsonPropertyName("accessToken")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("shumeiBoxId")] public string ShumeiBoxId { get; init; } = "";
    [JsonPropertyName("iosMode")] public bool IosMode { get; init; }
    /// <summary>The gateway's refresh token (opt=refresh), kept so a guest worth keeping can be tried again later.</summary>
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; init; } = "";
    /// <summary>"android" / "iphone" (DeviceProfile); empty = a session from before profiles, which keeps the old phone_model.</summary>
    [JsonPropertyName("device")] public string Device { get; init; } = "";

    /// <summary>The ip field is a pipe-delimited fallback list; the first host is the one to dial.</summary>
    public string PrimaryHost => Ip.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    public static SessionConfig Load(string path) =>
        JsonSerializer.Deserialize<SessionConfig>(File.ReadAllText(path))
        ?? throw new InvalidDataException($"empty session config: {path}");
}
