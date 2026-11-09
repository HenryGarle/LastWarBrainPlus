using LastWar.Client.Auth;
using LastWar.Core.Model;
using LastWar.Protocol.Sfs;

namespace LastWar.Client;

/// <summary>The game refused a login. The message says why in words; the codes are kept at the end.</summary>
public sealed class LoginFailedException(int errorCode, string detail) : Exception(Describe(errorCode, detail))
{
    public int ErrorCode { get; } = errorCode;
    /// <summary>The game's own reason code, e.g. E011, when it sent one.</summary>
    public string Reason { get; } = System.Text.RegularExpressions.Regex.Match(detail, @"E\d{3,}").Value;

    static string Describe(int ec, string detail)
    {
        var ep = System.Text.RegularExpressions.Regex.Match(detail, @"E\d{3,}").Value;
        var why = (ec, ep) switch
        {
            (28, "E011") => "The game no longer accepts this saved login: it has been used up (a guest can log in only once), "
                + "replaced by a newer sign-in to the same account, or saved for the wrong phone type. Sign in again by email, or import a session that works elsewhere.",
            (28, "E005") => "The game says this is the wrong device for the account - usually an iPhone account saved as Android or the other way round "
                + "(pick the right \"account made on\" when signing in).",
            (28, "E013") => "That server is not taking new accounts.",
            (28, _) => "The game refused the login for this account.",
            _ => "The game refused the login.",
        };
        return $"{why} (game code {ec}{(ep.Length > 0 ? " " + ep : "")})";
    }
}

/// <summary>
/// A logged-in game session. Commands go out and replies come back as SFS objects;
/// nothing above this layer needs to know about framing or envelopes.
/// </summary>
public sealed class LastWarClient(GameConnection connection) : IAsyncDisposable
{
    public GameConnection Connection { get; } = connection;
    public ServerId Server => new(Connection.ServerId);

    /// <summary>
    /// Reconnects an established account straight to its state server using a saved
    /// session. No GSL call, which is what makes this work for an account whose
    /// loginKey has stopped resolving.
    /// </summary>
    public static async Task<LastWarClient> ConnectAsync(
        SessionConfig config, GameIdentity identity, CancellationToken ct = default)
    {
        if (config.AccessToken.Length == 0)
            throw new ArgumentException("session config has no accessToken", nameof(config));
        if (BlockedAccounts.Why(config.GameUid) is { } why)
            throw new InvalidOperationException($"refusing to log in as {config.GameUid}: {why}");

        var conn = await GameConnection
            .ConnectAsync(config.PrimaryHost, config.Port, TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);

        // Tap the login push burst before the login request goes out. Buildings and visitors are
        // pushed once and never offered again, so a tap installed after the reply can miss them.
        LastWar.Client.Game.LoginPushRecorder.Attach(conn);

        var serverId = config.Zone.StartsWith("APS", StringComparison.Ordinal) ? config.Zone[3..] : "";
        var p = LoginParams.Build(new LoginParams.Input
        {
            DeviceId = identity.DeviceId,
            AirKey = identity.AirKey,
            GameUid = config.GameUid,
            AccessToken = config.AccessToken,
            ServerId = serverId,
            ShumeiBoxId = config.ShumeiBoxId,
            IosMode = config.IosMode,
            Device = DeviceProfile.Of(config.Device),
        });

        var content = new SfsObject();
        content.PutUtfString("zn", config.Zone);
        content.PutUtfString("un", config.GameUid);
        content.PutUtfString("pw", "");
        content.PutObject("p", p);

        await conn.SendEnvelopeAsync(Envelope.ControllerSystem, Envelope.ActionLogin, content)
            .ConfigureAwait(false);

        var reply = await conn.WaitForAsync(
            e => e.Controller == Envelope.ControllerSystem && e.Action == Envelope.ActionLogin,
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

        if (reply.Content.Has("ec"))
        {
            var ec = reply.Content.GetInt("ec");
            if (ec != 0)
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                throw new LoginFailedException(ec, reply.Content.ToString());
            }
        }
        return new LastWarClient(conn) { LoginReply = reply.Content };
    }

    /// <summary>The system login reply (for research: what the server says about a new account).</summary>
    public SfsObject? LoginReply { get; private init; }

    /// <summary>
    /// Sends a command and returns its reply. Replies are matched by command name, so
    /// callers must not overlap requests on one connection (see GameConnection).
    /// </summary>
    public async Task<SfsObject> CallAsync(
        string command, SfsObject? parameters = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        await Connection.SendCommandAsync(command, parameters).ConfigureAwait(false);
        var reply = await Connection
            .WaitForCommandAsync(timeout ?? TimeSpan.FromSeconds(8), ct, command)
            .ConfigureAwait(false);
        return reply.Params;
    }

    /// <summary>Like <see cref="CallAsync"/> but returns null on timeout instead of throwing.</summary>
    public async Task<SfsObject?> TryCallAsync(
        string command, SfsObject? parameters = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        try { return await CallAsync(command, parameters, timeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }

    /// <summary>
    /// The chat credentials as recorded from the init push, with no waiting and no reading of the
    /// inbound channel. Available for the life of the connection once login has happened.
    /// chatToken is ONLY published in that push - never in a command response.
    /// </summary>
    public ChatCredentials? ChatCredentials =>
        LastWar.Client.Game.LoginPushRecorder.For(Connection)?.Chat;

    /// <summary>
    /// Waits for the init push through the recorder's tap. It used to read Pushes() directly,
    /// which steals whatever replies are sitting on the one inbound channel.
    /// </summary>
    public async Task<ChatCredentials?> WaitForChatCredentialsAsync(
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (LastWar.Client.Game.LoginPushRecorder.For(Connection) is not { } recorder) return null;
        await recorder.WaitAsync(timeout ?? TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        return recorder.Chat;
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
