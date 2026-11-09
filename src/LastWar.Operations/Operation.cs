using LastWar.Client;
using LastWar.Client.Game;

namespace LastWar.Operations;

/// <summary>
/// The kinds of account a session can be, least to most precious.
///   Guest       throwaway, single login, auto-created. Cross-server reads (boards, profiles,
///               kingdom positions, rosters) work; views gated by level (other servers' maps) do not.
///   Service     a real but generic account kept for work (Sadmin today): cross-server map view,
///               reads that need an account ON a server (applicants, the ministry queue), chores.
///   Privileged  a player's own main account (alliance access). Used ONLY by operations that list it
///               explicitly - never by a generic scan just because it happens to work there.
/// </summary>
[Flags]
public enum AccountTier { None = 0, Guest = 1, Service = 2, Privileged = 4 }

public static class AccountTiers
{
    public static AccountTier Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "guest" => AccountTier.Guest,
        "privileged" => AccountTier.Privileged,
        _ => AccountTier.Service,
    };
    public static string Name(AccountTier t) => t.ToString().ToLowerInvariant();
    public static string[] Names(AccountTier set) =>
        [.. new[] { AccountTier.Guest, AccountTier.Service, AccountTier.Privileged }.Where(t => set.HasFlag(t)).Select(Name)];
}

/// <summary>How one parameter is asked for. Type drives the widget, nothing more.</summary>
public sealed record OperationParam(
    string Name, string Label, string Type = "text", string? Default = null,
    bool Required = false, string? Help = null);

/// <summary>
/// One thing the tooling can do, described well enough that a UI can render a form for it and a
/// CLI can parse flags for it, without either knowing what the operation actually is.
/// </summary>
public sealed record OperationInfo(
    string Id, string Group, string Title, string Description,
    bool NeedsSession, IReadOnlyList<OperationParam> Params)
{
    /// <summary>True when running it changes something on the account rather than reading it.</summary>
    public bool Writes { get; init; }
    /// <summary>Which kinds of account may run it (session operations only).</summary>
    public AccountTier Accounts { get; init; }
    public string[] AccountNames => AccountTiers.Names(Accounts);

    /// <summary>Null when this account may run it; otherwise why not, in words for the UI.</summary>
    public string? Refuses(AccountTier tier) =>
        !NeedsSession || Accounts.HasFlag(tier) ? null
        : tier == AccountTier.Privileged
            ? $"{Title} is not cleared for privileged accounts - they only run operations marked for them ({string.Join(", ", AccountNames)} only)"
            : $"{Title} needs a {string.Join(" or ", AccountNames)} account, not a {AccountTiers.Name(tier)} one";
}

/// <summary>
/// Every operation answers in this shape - a sentence, and optionally a table. That is what lets
/// one page render all of them, and one CLI verb print all of them, with no per-feature code.
/// </summary>
public sealed record OperationResult(
    string Summary,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows)
{
    /// <summary>
    /// Extra named tables, for results that are naturally more than one list (leavers and
    /// joiners). Renderers show these after the main table, which may then be empty.
    /// </summary>
    public IReadOnlyList<OperationTable> Tables { get; init; } = [];

    public static OperationResult Text(string summary) => new(summary, [], []);

    public static OperationResult From(GameActionResult r) => new(r.Summary, r.Columns, r.Rows);

    public static OperationResult Table(
        string summary, IEnumerable<string> columns, IEnumerable<IEnumerable<object?>> rows) =>
        new(summary,
            [.. columns],
            [.. rows.Select(r => (IReadOnlyList<string>)r.Select(v => v?.ToString() ?? "").ToArray())]);
}

/// <summary>
/// One titled table with display hints, so a renderer needs no per-operation code:
///   Brief - the columns shown until the reader asks for all of them (empty = all).
///   Links - column -> "kind:idColumn", e.g. "name" -> "player:uid" links each name to that
///           row's player page; an empty idColumn ("server:") links the cell's own value.
/// </summary>
public sealed record OperationTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public IReadOnlyList<string> Brief { get; init; } = [];
    /// <summary>Columns to sum into a total row under the table (numeric cells only).</summary>
    public IReadOnlyList<string> Totals { get; init; } = [];
    public IReadOnlyDictionary<string, string> Links { get; init; } = new Dictionary<string, string>();

    public static OperationTable Of(string title, IEnumerable<string> columns, IEnumerable<IEnumerable<object?>> rows) =>
        new(title, [.. columns], [.. rows.Select(r => (IReadOnlyList<string>)r.Select(v => v?.ToString() ?? "").ToArray())]);
}
/// <summary>
/// What an operation is handed when it runs: the arguments, a session if it needs one, the
/// database, and somewhere to report progress from a long sweep.
/// </summary>
public sealed class OperationContext
{
    public required IReadOnlyDictionary<string, string> Args { get; set; }
    /// <summary>The Postgres connection string (Db / PgConfig).</summary>
    public required string Database { get; init; }
    /// <summary>The data folder: guest sessions, game assets, raw captures, runner files.</summary>
    public required string DataDir { get; init; }
    public LastWarClient? Session { get; set; }

    /// <summary>
    /// How to get a fresh session when the current one drops, for front ends that can. A real
    /// account reconnects fine; a guest cannot log in twice, so for a guest this just fails and
    /// the operation stops cleanly.
    /// </summary>
    public Func<CancellationToken, Task<LastWarClient>>? Reconnect { get; init; }
    public IProgress<string>? Progress { get; init; }
    public CancellationToken Cancellation { get; init; }

    public LastWarClient Require() =>
        Session ?? throw new InvalidOperationException("this operation needs a running session");

    public string Text(string name, string fallback = "") =>
        Args.TryGetValue(name, out var v) && v.Trim().Length > 0 ? v.Trim() : fallback;

    public int Int(string name, int fallback) =>
        int.TryParse(Text(name), out var n) ? n : fallback;

    public bool Flag(string name, bool fallback = false) =>
        bool.TryParse(Text(name), out var b) ? b : fallback;

    public void Report(string message) => Progress?.Report(message);
}
