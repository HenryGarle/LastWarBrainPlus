using System.Text.Json;
using System.Text.Json.Nodes;
using LastWar.Client.Game;
using LastWar.Protocol.Sfs;
using LastWar.Storage;

namespace LastWar.Operations;

/// <summary>
/// One mail made readable. System mail carries no text of its own: its title and body are JSON
/// naming dialog ids ({"h":{"title":{"dialog":{"id":"mail_10107"}}}}, {"b":{"content":{"dialog":
/// {"id":..,"params":[{"text":"6"}]}},"reward":{"rewardInfo":[{type,id,num}]},"mailId":10110}}),
/// resolved here through the game's own strings. Mail written by players (president, alliance,
/// personal) carries plain text and is shown as is.
/// </summary>
public sealed record MailView(string Uid, int Type, string TypeName, string Title, string From, string Content,
    IReadOnlyList<string> Rewards, string RewardState, bool Read, DateTimeOffset? Sent, DateTimeOffset? Expires, JsonNode Raw);

public static class MailFormat
{
    public static MailView View(MailEntry m, GameText text)
    {
        var raw = SfsJson.Write(m.Raw);
        string S(string k) => raw[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : raw[k]?.ToJsonString() ?? "";
        long L(string k) => raw[k] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;

        var (title, _, _) = Parse(S("title"), text);
        var (content, rewards, mailId) = Parse(S("contentsLocal") is { Length: > 0 } cl ? cl : S("contents"), text);
        if (mailId > 0 && (title.Length == 0 || content.Length == 0))
        {
            var (t, msg) = text.MailTemplate(mailId);
            if (title.Length == 0) title = t;
            if (content.Length == 0) content = msg;
        }
        var state = raw["rewardStatus"] is null ? "none" : L("rewardStatus") == 0 ? "unclaimed" : "claimed";
        DateTimeOffset? Ms(long v) => v > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;
        return new MailView(m.Uid, m.Type, text.MailTypeName(m.Type), title.Length > 0 ? title : text.MailTypeName(m.Type),
            S("fromName") is { Length: > 0 } f ? f : S("fromUser"), content, rewards, rewards.Count == 0 && state == "unclaimed" ? "none" : state,
            L("status") != 0, Ms(L("sendTime") > 0 ? L("sendTime") : L("createTime")), Ms(L("expireTime")), raw);
    }

    /// <summary>Plain text stays plain; the JSON forms become text, reward lines and the mail template id.</summary>
    static (string Text, List<string> Rewards, long MailId) Parse(string s, GameText text)
    {
        var rewards = new List<string>();
        if (!s.StartsWith('{')) return (s, rewards, 0);
        try
        {
            using var doc = JsonDocument.Parse(s);
            var parts = new List<string>();
            long mailId = 0;
            void Walk(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Walk(x); return; }
                if (e.ValueKind != JsonValueKind.Object) return;
                foreach (var p in e.EnumerateObject())
                {
                    if (p.NameEquals("dialog") && p.Value.ValueKind == JsonValueKind.Object) parts.Add(Dialog(p.Value, text));
                    else if (p.NameEquals("rewardInfo") && p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var r in p.Value.EnumerateArray())
                            rewards.Add(text.Reward(Int(r, "type"), Str(r, "id"), long.TryParse(Str(r, "num"), out var n) ? n : 1));
                    else if (p.NameEquals("mailId") && p.Value.TryGetInt64(out var id)) mailId = id;
                    else if (p.NameEquals("text") && p.Value.ValueKind == JsonValueKind.String) parts.Add(p.Value.GetString()!);
                    else Walk(p.Value);
                }
            }
            Walk(doc.RootElement);
            return (string.Join("\n", parts.Where(x => x.Length > 0)), rewards, mailId);
        }
        catch (JsonException) { return (s, rewards, 0); }
    }

    static string Dialog(JsonElement d, GameText text)
    {
        var args = d.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array
            ? ps.EnumerateArray().Select(p => p.TryGetProperty("dialog", out var inner) ? Dialog(inner, text)
                : p.TryGetProperty("text", out var t) ? t.ToString() : p.ToString()).ToList()
            : [];
        return text.T(Str(d, "id"), args);
    }

    static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ToString() : "";
    static int Int(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : int.TryParse(Str(e, k), out var m) ? m : -1;
}
