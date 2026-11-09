using LastWar.Protocol.Sfs;

namespace LastWar.Client.Game;

/// <summary>Alliance-side chores: help, gifts, tech donation.</summary>
public sealed class AllianceActions(LastWarClient client)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Bulk-completes every pending alliance help request in one call, the same thing the game's
    /// own help-all button sends.
    ///
    /// cmdBaseTime is the only field that reaches the wire, as a Long - the real client sent an
    /// absolute Unix-epoch-milliseconds value. Everything else the message takes (button
    /// positions, animation flags) is local UI state and is never sent.
    ///
    /// Safe to call on every run: the call is unconditional and the game shows a generic "helped"
    /// tip even when nothing was pending.
    /// </summary>
    public async Task<GameActionResult> HelpAllAsync(CancellationToken ct = default)
    {
        var p = new SfsObject();
        p.PutLong("cmdBaseTime", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var reply = await client.TryCallAsync("al.help.all", p, Timeout, ct).ConfigureAwait(false);
        return GameActionResult.Text($"alliance help-all: {Replies.Outcome(reply)}");
    }

    /// <summary>
    /// Claims both Alliance Gifts tabs. A true bulk claim - the request carries only a type, no
    /// per-gift ids.
    ///
    /// The two tabs are claimed independently, so neither outcome gates the other. type=2 (Regular)
    /// is the one actually captured live; type=1 (Premium) is sent on the strength of the game's
    /// own tip-string branch, which maps type==1 to the Premium string and anything else to
    /// Regular. Nothing treats a claim with zero pending gifts as an error, but no Premium-
    /// ineligible reply has been captured either, so if type=1 ever surfaces an unexpected error
    /// code, capture it rather than guessing at one now.
    /// </summary>
    public async Task<GameActionResult> ClaimGiftsAsync(CancellationToken ct = default)
    {
        var rows = new List<IReadOnlyList<string>>();
        var claimed = 0;
        foreach (var (type, tab) in new[] { (1, "Premium"), (2, "Regular") })
        {
            var p = new SfsObject();
            p.PutInt("type", type);
            var reply = await client
                .TryCallAsync("alliance.reward.allreceive", p, Timeout, ct).ConfigureAwait(false);
            if (Replies.Ok(reply)) claimed++;
            rows.Add([tab, type.ToString(), Replies.Outcome(reply)]);
        }
        return new GameActionResult($"claimed {claimed} of 2 gift tabs",
            ["Tab", "Type", "Result"], rows);
    }

    /// <summary>
    /// Finds whichever alliance tech is currently marked Recommended and puts one resource-cost
    /// donation into it.
    ///
    /// science.data.refresh takes no parameters and returns the whole tech tree - about 45 entries
    /// on a live account, of which exactly one had state=1, and its progress matched the
    /// thumbs-up-badged tech in the game's own UI at the same moment. That is what makes state=1
    /// the recommended marker. Setting it is an officer action through a different command; this
    /// only reads it.
    ///
    /// option=1 is the value the real client sent for a successful donation. Its exact meaning was
    /// never determined because no other value was ever observed, so it is reused as-is.
    ///
    /// A per-donation cooldown exists and answers errorCode=120471 "Donate science CD time is not
    /// finish"; its real duration has not been measured. A separate daily count caps the free path
    /// at 30, so one attempt per run is all that makes sense.
    ///
    /// Deliberately not sent: al.science.donate.gold, the gem-cost path. It has no cooldown but
    /// spends premium currency per use. The "hold to donate more" behaviour is also absent on
    /// purpose - neither donate message has a count field, so holding the button is just the client
    /// repeating this same single-donation request.
    /// </summary>
    public async Task<GameActionResult> DonateTechAsync(CancellationToken ct = default)
    {
        var tree = await client
            .TryCallAsync("science.data.refresh", null, Timeout, ct).ConfigureAwait(false);
        if (tree is null) return GameActionResult.Text("alliance tech donate: no reply");
        if (!tree.TryGetArray("allianceScience", out var arr))
            return GameActionResult.Text("alliance tech donate: no tech tree returned");

        var scienceId = RecommendedTech(arr);
        if (scienceId is null)
            return GameActionResult.Text("alliance tech donate: nothing is currently recommended");

        var p = new SfsObject();
        p.PutInt("scienceId", scienceId.Value);
        p.PutInt("option", 1);
        var reply = await client
            .TryCallAsync("al.science.donate", p, Timeout, ct).ConfigureAwait(false);
        return GameActionResult.Text(
            $"alliance tech donate (scienceId {scienceId}): {Replies.Outcome(reply)}");
    }

    /// <summary>Bounds the scan; a live tree has around 45 entries, so this is slack, not a limit.</summary>
    const int TechScanCap = 1000;

    static int? RecommendedTech(SfsArray tree)
    {
        foreach (var tech in tree.Objects().Take(TechScanCap))
        {
            // Both fields are checked before use. A wrong-typed state coerces to 0 and simply never
            // matches, but a wrong-typed scienceId coerces to 0 too - and that one would send a
            // real donation against the wrong tech.
            if (!tech.HasNumber("state") || tech.GetInt("state") != 1) continue;
            if (!tech.HasNumber("scienceId")) continue;
            return tech.GetInt("scienceId");
        }
        return null;
    }
}
