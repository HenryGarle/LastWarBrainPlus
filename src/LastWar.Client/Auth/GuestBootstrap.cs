using System.Security.Cryptography;

namespace LastWar.Client.Auth;

/// <summary>
/// Creates a brand new guest account and returns a session that can be reconnected
/// later, with no Go tooling in the loop.
///
/// Each guest gets its OWN home directory. The device identity, and therefore the
/// account, is tied to that folder - which is what keeps a guest from ever touching the
/// real account's saved credentials.
/// </summary>
public static class GuestBootstrap
{
    /// <summary>A device id as the client generates one when the native call fails: 32 hex + "_n3d".</summary>
    public static string NewDeviceId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() + "_n3d";

    public static async Task<SessionConfig> CreateAsync(
        string homeDir, string? preferredZone = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(homeDir);
        var idFile = Path.Combine(homeDir, ".lastwar_goclient_device_id");
        if (!File.Exists(idFile)) File.WriteAllText(idFile, NewDeviceId());
        var identity = GameIdentity.LoadOrCreate(homeDir);

        var uidFile = Path.Combine(homeDir, ".lastwar_goclient_gameuid");
        var knownUid = File.Exists(uidFile) ? File.ReadAllText(uidFile).Trim() : "";

        // A device the gateway already knows must ask for "fix"; re-sending "new" for it
        // is what produces ec=28 E011.
        var opt = knownUid.Length > 0 ? "fix" : "new";

        var gsl = new GslClient();
        var result = await gsl.GetServerListAsync(
            identity.DeviceId, opt, zone: preferredZone ?? "", gameUid: knownUid, ct: ct);

        if (result.Roles.Count == 0) throw new InvalidOperationException("gateway returned no servers");
        var role = result.Roles[0];
        if (role.PrimaryHost.Length == 0 || role.Port <= 0)
            throw new InvalidOperationException("gateway returned no usable address");

        if (role.GameUid.Length > 0) File.WriteAllText(uidFile, role.GameUid);

        var config = new SessionConfig
        {
            Ip = role.Ip,
            Port = role.Port,
            Zone = role.Zone,
            GameUid = role.GameUid,
            AccessToken = result.AccessToken,
            ShumeiBoxId = "",
            IosMode = false,
            RefreshToken = result.RefreshToken,
            Device = DeviceProfile.Android.Key,
        };
        // Saved beside the device id: a guest that lands on a server we follow is kept (Operations.RetireGuest).
        File.WriteAllText(Path.Combine(homeDir, ".lastwar_goclient_session.json"), System.Text.Json.JsonSerializer.Serialize(config));
        return config;
    }
}
