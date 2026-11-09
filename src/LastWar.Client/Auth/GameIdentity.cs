using System.Security.Cryptography;
using System.Text;

namespace LastWar.Client.Auth;

/// <summary>
/// The device identity the server ties an account to. Persisted beside the session so a
/// reconnect presents the same device; the file names match the Go client's, so an
/// identity already on disk keeps working.
/// </summary>
public sealed class GameIdentity
{
    public required string DeviceId { get; init; }

    public string AirKey => "lwDid_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(DeviceId));

    public static GameIdentity LoadOrCreate(string homeDir)
    {
        var path = Path.Combine(homeDir, ".lastwar_goclient_device_id");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length > 0) return new GameIdentity { DeviceId = existing };
        }
        var id = Guid.NewGuid().ToString();
        Directory.CreateDirectory(homeDir);
        File.WriteAllText(path, id);
        return new GameIdentity { DeviceId = id };
    }

    public static string Md5Hex(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public static string Sha1Hex(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
