namespace LastWar.Core.Model;

/// <summary>A Last War state/server id, e.g. 1967.</summary>
public readonly record struct ServerId(int Value)
{
    public override string ToString() => Value.ToString();
    public static implicit operator int(ServerId id) => id.Value;
    public static implicit operator ServerId(int v) => new(v);

    /// <summary>
    /// The port a state server listens on. Constant across every server observed:
    /// 1967 -> 11967, 348 -> 10348.
    /// </summary>
    public int GamePort => 10000 + Value;

    /// <summary>SFS2X zone name, e.g. "APS1967".</summary>
    public string Zone => $"APS{Value}";
}

/// <summary>
/// A player account id. The last six digits, zero padded, are the account's ORIGIN
/// server, which is what makes cross-server migration visible without any migration
/// command: 1240780578001963 started life on 1963.
/// </summary>
public readonly record struct PlayerUid(string Value)
{
    public override string ToString() => Value;
    public static implicit operator string(PlayerUid u) => u.Value;

    public ServerId? OriginServer =>
        Value.Length >= 6 && int.TryParse(Value[^6..], out var s) && s > 0 ? new ServerId(s) : null;

    public bool IsValid => !string.IsNullOrWhiteSpace(Value);
}
