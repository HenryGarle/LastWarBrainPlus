namespace LastWar.Client.Game;

/// <summary>The two once-per-day VIP screen claims.</summary>
public sealed class VipActions(LastWarClient client)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The daily login-streak bonus on the VIP screen. Genuinely parameterless - the real client's
    /// captured request carried no params at all.
    ///
    /// Once per day. Replaying it on an account that had already claimed today returned a real,
    /// well-formed errorCode=120289 "no score", not a protocol error, so it is safe to call every
    /// run and let the server say no.
    /// </summary>
    public async Task<GameActionResult> DailyLoginAsync(CancellationToken ct = default)
    {
        var reply = await client
            .TryCallAsync("vip.add.login.score", null, Timeout, ct).ConfigureAwait(false);
        return GameActionResult.Text($"vip daily login score: {Replies.Outcome(reply)}");
    }

    /// <summary>
    /// The VIP Daily Freebie chest - a separate reward from the login score above, both visible as
    /// distinct actions on the same screen in one capture. Also parameterless on the wire: the
    /// message declares an actId argument but never puts it on the object.
    ///
    /// Same once-per-day shape, answering errorCode=120289 "no reward" when already claimed.
    /// </summary>
    public async Task<GameActionResult> DailyRewardAsync(CancellationToken ct = default)
    {
        var reply = await client
            .TryCallAsync("vip.get.every.day.reward", null, Timeout, ct).ConfigureAwait(false);
        return GameActionResult.Text($"vip daily freebie: {Replies.Outcome(reply)}");
    }
}
