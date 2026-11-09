namespace LastWar.Core.Model;

// What a guest can read about ANY alliance or server (probed 2026-09-27, data/probes/*.json).
// Times are UTC; a missing time is null rather than the epoch.

/// <summary>One member row of al.rank. For outsiders online/offLineTime are masked (all "online"),
/// but the donation fields are real: donateTime is the member's last alliance-tech donation.</summary>
public sealed record RosterMember
{
    public required string Uid { get; init; }
    public string Name { get; init; } = "";
    public int Rank { get; init; }                 // R1..R5
    public long Power { get; init; }
    public long Kills { get; init; }
    public int Hq { get; init; }
    public long DonateToday { get; init; }         // todayProgress: tech donation points today
    public long DonateWeek { get; init; }          // weeklyProgress
    public DateTimeOffset? DonatedAt { get; init; }
    public DateTimeOffset? WeekDonatedAt { get; init; }
    public DateTimeOffset? JoinedAt { get; init; }
    public DateTimeOffset? RankSince { get; init; }
    public long MonthCardEnd { get; init; }        // unix seconds
    public int Server { get; init; }
    public int CurrentServer { get; init; }
}

public sealed record AllianceRoster(string AllianceId, DateTimeOffset TakenAt, IReadOnlyList<RosterMember> Members,
    IReadOnlyList<(string Uid, int Type)> Officials, IReadOnlyList<string> RankNames);

/// <summary>get.al.info: the alliance's own card.</summary>
public sealed record AllianceInfo
{
    public required string AllianceId { get; init; }
    public required DateTimeOffset TakenAt { get; init; }
    public string Tag { get; init; } = "";
    public string Name { get; init; } = "";
    public int Server { get; init; }               // crossFightSrcServerId, else createServer
    public int CreateServer { get; init; }
    public long FightPower { get; init; }
    public int Members { get; init; }
    public int MaxMembers { get; init; }
    public long Engagement { get; init; }          // allianceEngagementPoint
    public int GiftLevel { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string Language { get; init; } = "";
    public string Country { get; init; } = "";
    public string LeaderUid { get; init; } = "";
    public string LeaderName { get; init; } = "";
    public bool JoinApproval { get; init; }
    public int LevelLimit { get; init; }
    public long PowerLimit { get; init; }
    public string Announce { get; init; } = "";
}

/// <summary>city.war.get.info: who holds each of a server's cities.</summary>
public sealed record CityHold(int Server, DateTimeOffset TakenAt, int CityId, string AllianceId, string Tag,
    string AllianceName, DateTimeOffset? FirstOccupiedAt, DateTimeOffset? ProtectedUntil, DateTimeOffset? GiveUpAt);

/// <summary>season.migrate.server.info: the server's card - state, score, president, migration inflow.</summary>
public sealed record ServerState
{
    public required int Server { get; init; }
    public required DateTimeOffset TakenAt { get; init; }
    public int State { get; init; }
    public long Score { get; init; }
    public string PresidentUid { get; init; } = "";
    public string PresidentName { get; init; } = "";
    public string PresidentTag { get; init; } = "";
    public string FirstLadyUid { get; init; } = "";
    public string FirstLadyName { get; init; } = "";
    public long ApplyPower { get; init; }
    public int InSuperLow { get; init; }
    public int InLow { get; init; }
    public int InMid { get; init; }
    public int InHigh { get; init; }
    public string Notice { get; init; } = "";
}

/// <summary>world.get.city.stronghold.info: a stronghold and its holder (a base64 protobuf list).</summary>
public sealed record Stronghold(int Server, DateTimeOffset TakenAt, int Id, string AllianceId, string Tag, string AllianceName, int Level);

/// <summary>get.cross.server.king.info: when a server opened, and its president right now.</summary>
public sealed record ServerCard(int Server, DateTimeOffset TakenAt, DateTimeOffset? CreatedAt, string KingUid, string KingName,
    string KingTag, long KingPower, DateTimeOffset? KingSince);

/// <summary>
/// weapon.info.view (the drone: level, power, chip level, stat bonuses) and get.new.user.info (the
/// profile card: career, alliance rank, badge) for one player. The card's battle counters read 0 for
/// other players (hidden), so they are kept only in case they ever answer.
/// </summary>
public sealed record PlayerDetail
{
    public required string Uid { get; init; }
    public required DateTimeOffset TakenAt { get; init; }
    public int DroneLevel { get; init; }
    public long DronePower { get; init; }
    public int ChipLevel { get; init; }
    public string DroneProps { get; init; } = "";
    public int CareerType { get; init; }
    public int CareerLevel { get; init; }
    public int GiftLevel { get; init; }
    public string BadgeId { get; init; } = "";
    public int AllianceRank { get; init; }
    public long BattleWin { get; init; }
    public long BattleLose { get; init; }
    public long ArmyDead { get; init; }
}

/// <summary>get.history.king.record: one past president.</summary>
public sealed record President(int Server, int Round, string Uid, string Name, string Tag, string AllianceName,
    DateTimeOffset? BecameAt, int OccupyServer, string Country);
