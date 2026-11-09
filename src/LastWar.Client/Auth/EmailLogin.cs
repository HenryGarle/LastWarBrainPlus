using LastWar.Protocol.Sfs;

namespace LastWar.Client.Auth;

public sealed class EmailLoginException(string message) : Exception(message);

/// <summary>
/// Signing in with an email address, exactly as the client's own account screen does it.
///
/// The whole exchange rides the ordinary game socket - there is no account web service -
/// so it needs a connected session to speak through. A throwaway guest provides one.
/// Nothing here is signed; the only additions the transport makes are the correlation id
/// and the usual framing.
///
/// Two steps, because a human has to read their inbox in between:
///   1. RequestCodeAsync  -> account.login.send.verify.code { mail, lang }
///                        <- push.account.send.verify.code  { errorCode?, emailExpireTime }
///   2. SubmitCodeAsync   -> account.login.new { type=0, mail, verifyCode, pf, deviceId, airKey }
///                        <- push.account.login.new { errorCode?, loginKey, accountArr[] }
///
/// The loginKey that comes back is a ONE-SHOT bearer: it is redeemed at the gateway with
/// opt=login for the chosen role, which returns the access token a saved session needs.
/// The real client wipes it immediately afterwards, and so does this.
/// </summary>
public sealed class EmailLogin(LastWarClient client, GameIdentity identity, bool ios = false)
{
    /// <summary>The account was made on an iPhone/iPad. Must match: an iOS account signed in with
    /// Android fields is refused at the socket login with ec=28 / E011 (Sadmin is iOS).</summary>
    public bool Ios => ios;

    static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(45);

    public async Task<int> RequestCodeAsync(string email, CancellationToken ct = default)
    {
        var p = new SfsObject();
        p.PutUtfString("mail", email);
        p.PutUtfString("lang", "English");

        await client.Connection.SendCommandAsync("account.login.send.verify.code", p);
        var push = await client.Connection.WaitForCommandAsync(
            PushTimeout, ct, "push.account.send.verify.code");

        Fail(push.Params, "could not send a verification code");
        return push.Params.GetInt("emailExpireTime");
    }

    /// <summary>Submits the code and returns the roles on the account, each carrying the loginKey.</summary>
    public async Task<IReadOnlyList<Role>> SubmitCodeAsync(
        string email, string code, CancellationToken ct = default)
    {
        if (code.Trim().Length < 6) throw new EmailLoginException("the verification code is six characters");

        var p = new SfsObject();
        p.PutInt("type", 0);                                   // 0 = email
        p.PutUtfString("mail", email);
        p.PutUtfString("verifyCode", code.Trim());             // note: "code" in the UI, "verifyCode" on the wire
        p.PutUtfString("pf", ios ? "AppStore" : "market_global");
        p.PutUtfString("deviceId", identity.DeviceId);
        p.PutUtfString("airKey", identity.AirKey);

        await client.Connection.SendCommandAsync("account.login.new", p);
        var push = await client.Connection.WaitForCommandAsync(PushTimeout, ct, "push.account.login.new");
        var r = push.Params;

        Fail(r, "the verification code was not accepted");

        var loginKey = r.GetString("loginKey");
        if (loginKey.Length == 0) throw new EmailLoginException("no login key in the reply");

        var roles = new List<Role>();
        if (r.TryGetArray("accountArr", out var arr))
            foreach (var a in arr.Objects())
                roles.Add(new Role
                {
                    Zone = a.GetString("zone"),
                    GameUid = a.GetString("gameUid"),
                    Ip = a.GetString("ip"),
                    Port = a.GetInt("port"),
                    Name = a.GetString("gameUserName"),
                    Alliance = a.GetString("alAbbr"),
                    Power = a.GetLong("power"),
                    Level = a.GetInt("gameUserLevel"),
                    LoginKey = loginKey,
                });

        if (roles.Count == 0) throw new EmailLoginException("the account has no roles on any server");
        return roles;
    }

    /// <summary>
    /// Redeems the one-shot loginKey for the chosen role and returns a session that can
    /// reconnect on its own afterwards.
    /// </summary>
    public static async Task<SessionConfig> RedeemAsync(
        Role role, GameIdentity identity, bool ios, CancellationToken ct = default)
    {
        var gsl = new GslClient();
        var result = await gsl.GetServerListAsync(
            identity.DeviceId, "login", zone: role.Zone, gameUid: role.GameUid,
            loginKey: role.LoginKey, ct: ct);

        var server = result.Roles.FirstOrDefault(r => r.Zone == role.Zone) ?? result.Roles.FirstOrDefault();
        if (server is null || server.PrimaryHost.Length == 0)
            throw new EmailLoginException("the gateway returned no server for that role");
        if (result.AccessToken.Length == 0)
            throw new EmailLoginException("the gateway returned no access token");

        return new SessionConfig
        {
            Ip = server.Ip.Length > 0 ? server.Ip : role.Ip,
            Port = server.Port > 0 ? server.Port : role.Port,
            Zone = server.Zone.Length > 0 ? server.Zone : role.Zone,
            GameUid = server.GameUid.Length > 0 ? server.GameUid : role.GameUid,
            AccessToken = result.AccessToken,
            ShumeiBoxId = "",
            IosMode = ios,
            Device = DeviceProfile.For(ios).Key,   // a new sign-in is a new session: it says it is a current phone
        };
    }

    /// <summary>The account service reports failure as an errorCode on the push, not as an exception.</summary>
    static void Fail(SfsObject push, string what)
    {
        var code = push.Scalar("errorCode");
        if (code.Length > 0 && code != "0")
            throw new EmailLoginException($"{what} (errorCode {code})");
    }
}
