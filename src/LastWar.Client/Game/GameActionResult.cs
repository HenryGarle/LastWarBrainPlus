using LastWar.Protocol.Sfs;

namespace LastWar.Client.Game;

/// <summary>
/// The outcome of one game action in a shape any UI can render: a one-line summary and,
/// when the action produced per-item results, a table. Rows are already-formatted strings -
/// a caller displays them, it does not compute with them.
/// </summary>
public sealed record GameActionResult(
    string Summary,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows)
{
    /// <summary>A result with nothing tabular to show.</summary>
    public static GameActionResult Text(string summary) => new(summary, [], []);
}

/// <summary>Reply handling shared by the action classes in this namespace.</summary>
internal static class Replies
{
    /// <summary>
    /// How one command turned out, as a single cell.
    ///
    /// The server reports business-logic refusals - already claimed today, donate cooldown
    /// not finished, visitor still arriving - as a well-formed reply carrying errorCode, not
    /// as a transport failure. Those are the normal shape of "nothing to do right now", which
    /// is why every action here calls unconditionally and lets the server say no.
    ///
    /// errorCode comes back as an int on some commands and a string on others; Scalar renders
    /// either without the caller having to pick.
    /// </summary>
    public static string Outcome(SfsObject? reply)
    {
        if (reply is null) return "no reply";
        var code = reply.Scalar("errorCode");
        if (code.Length == 0 || code == "0") return "ok";
        var msg = reply.Scalar("errorMsg");
        return msg.Length > 0 ? $"{code} {msg}" : code;
    }

    public static bool Ok(SfsObject? reply) => Outcome(reply) == "ok";

    /// <summary>
    /// True when a field is present and actually numeric. GetInt/GetLong coerce a missing or
    /// wrong-typed field to 0, which is indistinguishable from a real 0 - callers check first
    /// wherever that collision would put a wrong value on the wire or drop a real entry.
    /// </summary>
    public static bool HasNumber(this SfsObject o, string key) =>
        o.TryGet(key, out var v) && v.Value is int or long or short or byte;
}
