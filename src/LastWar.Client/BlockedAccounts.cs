namespace LastWar.Client;

/// <summary>
/// Accounts this tooling must never log in as, whatever a runner file, session or job says. Checked in
/// LastWarClient.ConnectAsync - the one door every login (server runners, CLI, scheduled jobs) goes
/// through - so nothing reaches the game socket as these accounts. Henry's rules, 2026-10-02.
/// </summary>
public static class BlockedAccounts
{
    static readonly Dictionary<string, string> Blocked = new()
    {
        ["1001690072001967"] = "Ghost is Henry's personal account - never used by this tooling, ever",
        ["2359053554001967"] = "Sadmin was banned (2026-10-02) - retired",
    };

    public static string? Why(string? uid) => uid is { Length: > 0 } && Blocked.TryGetValue(uid, out var w) ? w : null;
}
