namespace LastWar.Core.Model;

/// <summary>
/// A player's own profile at one moment, from get.user.info.multi - the authoritative answer to
/// "which server is this player FROM", as opposed to where a map happened to see their base.
///
/// Three different servers hide in a profile and they must not be confused:
///   HomeServer    - the server they belong to. crossFightSrcServerId while one is set, else
///                   serverId (Chat/Model/ChatUserInfo.getServerId). Changes with a TRANSFER,
///                   not with a weekend or season teleport to fight somewhere else.
///   CurrentServer - where their base physically is right now. A visitor over for a war shows
///                   the war server here while HomeServer stays put.
///   Origin        - the server the account was created on (uid suffix). Never changes.
/// </summary>
public sealed record PlayerProfile
{
    public required string Uid { get; init; }
    public required DateTimeOffset TakenAt { get; init; }
    public string Name { get; init; } = "";
    public int HomeServer { get; init; }
    public int ServerId { get; init; }
    public int CurrentServer { get; init; }
    public int SrcServer { get; init; }
    public int CrossFightSrcServer { get; init; }
    public string AllianceId { get; init; } = "";
    public string AllianceTag { get; init; } = "";
    public string AllianceName { get; init; } = "";
    public long Power { get; init; }
    public long ArmyPower { get; init; }
    public long ArmyKill { get; init; }
    public int Level { get; init; }
    public string Lang { get; init; } = "";
    public string Country { get; init; } = "";
    /// <summary>
    /// lastUpdateTime: when the game last refreshed this player's info - it moves when they are
    /// in the game. Measured 2026-09-27: 100 players whose power never rose had it 1 h - 14 days
    /// old; 100 whose power rose that day had it mostly under 12 h. The best "last seen" there is.
    /// </summary>
    public DateTimeOffset? LastSeen { get; init; }
    public int SvipLevel { get; init; }
    /// <summary>VIP active until (unix seconds, svipEndTime). The level stays after it lapses; buying VIP time pushes this out.</summary>
    public long SvipEnd { get; init; }
    /// <summary>The flag the player picked to show (countryflag): ISO code, "UN" for the neutral flag, "ENG" and the like. Country is where the game thinks they are.</summary>
    public string Flag { get; init; } = "";
    /// <summary>Monthly-card expiry (unix seconds); in the future = a paying player right now.</summary>
    public long MonthCardEnd { get; init; }
    public long ThumbsUp { get; init; }
    /// <summary>"sweep" for a range sweep, "chat" for a chat-sender lookup.</summary>
    public string Source { get; init; } = "";

    /// <summary>Base is somewhere other than home: over on another server for a fight.</summary>
    public bool Visiting => CurrentServer > 0 && HomeServer > 0 && CurrentServer != HomeServer;
}
