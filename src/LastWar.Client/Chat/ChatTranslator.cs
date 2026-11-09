using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LastWar.Core.Model;

namespace LastWar.Client.Chat;

/// <summary>One translated message: the text, the language it was put into, and what it came from.</summary>
public sealed record ChatTranslation(string RoomId, long SeqId, string TargetLang, string Text, string SourceLang);

/// <summary>
/// The game's own chat translation (Chat/Controller/TranslateManager, the "context" route).
///
///     POST {base}api/translate/context          base = ContextUrlList, first that answers
///     { tf, ch:"lastwar", t:<unix s>, uid, sig, sf, tk, ul, ign_bad:0, sid, messages:[...] }
///     sig = lower(md5(sf + tf + ch + t + tk + uid))
///     messages[] = { id:"<seqId>", text, translate, sender:<uid>, role:<name>, time }
///
/// Only messages marked translate=true come back translated; the rest are context the service
/// reads so a reply makes sense. The answer is { code:0, translations:[{ id, translateMsg, ... }] }.
///
/// Plain HTTPS, not the game socket: a translation never touches the session it borrows
/// credentials from.
/// </summary>
public sealed class ChatTranslator(HttpClient http)
{
    public static readonly string[] Bases =
        ["https://lastwar-us-translate.lastwargame.com/", "https://lastwar-api-translation.lastwargame.com/"];
    const string Channel = "lastwar";
    const string Path = "api/translate/context";
    const int MaxContext = 20, MaxChars = 4000;     // the client's own limits

    /// <summary>
    /// Translates <paramref name="targets"/> into <paramref name="lang"/>, sending the messages
    /// before them in <paramref name="context"/> along as context, as the game does.
    /// </summary>
    public async Task<(IReadOnlyList<ChatTranslation> Done, string Raw)> TranslateAsync(
        ChatCredentials creds, int serverId, string lang,
        IReadOnlyList<ChatMessage> targets, IReadOnlyList<ChatMessage> context, string sourceLang = "", CancellationToken ct = default)
    {
        var want = targets.Select(m => m.SeqId).ToHashSet();
        var messages = new JsonArray();
        var chars = 0;
        foreach (var m in context.Where(m => !want.Contains(m.SeqId)).OrderByDescending(m => m.SeqId))
        {
            if (messages.Count >= MaxContext - targets.Count || chars + m.Text.Length > MaxChars / 2) break;
            if (m.Text.Length == 0 || m.Text.StartsWith("<lwSticker")) continue;
            messages.Insert(0, Entry(m, false));
            chars += m.Text.Length;
        }
        foreach (var m in targets.OrderBy(m => m.SeqId)) messages.Add(Entry(m, true));

        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sf = sourceLang;   // the sender's language (extra.srcLang), as the client sends; "" lets the service guess
        // tk is player.translateKey in the client. It is "" for every session seen (not in the init
        // push nor any early frame) and the service answers normally with it empty.
        const string tk = "";
        var sig = Md5($"{sf}{lang}{Channel}{t}{tk}{creds.Uid}");
        var body = new JsonObject
        {
            ["tf"] = lang, ["ch"] = Channel, ["t"] = t, ["uid"] = creds.Uid, ["sig"] = sig,
            ["sf"] = sf, ["tk"] = tk, ["ul"] = "", ["ign_bad"] = 0,
            ["sid"] = serverId.ToString(), ["messages"] = messages,
        };

        Exception? last = null;
        foreach (var b in Bases)
        {
            try
            {
                using var res = await http.PostAsJsonAsync(b + Path, body, ct).ConfigureAwait(false);
                var raw = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) { last = new HttpRequestException($"{(int)res.StatusCode} from {b}: {Trim(raw)}"); continue; }
                return (Parse(raw, targets, lang, sf), raw);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { last = ex; }
        }
        throw new InvalidOperationException($"translation failed: {last?.Message}", last);
    }

    static JsonObject Entry(ChatMessage m, bool translate) => new()
    {
        ["id"] = m.SeqId.ToString(),
        ["text"] = m.Text,
        ["translate"] = translate,
        ["sender"] = m.SenderUid,
        ["role"] = m.SenderName,
        ["time"] = m.SentAt.ToUnixTimeSeconds(),
    };

    static IReadOnlyList<ChatTranslation> Parse(string raw, IReadOnlyList<ChatMessage> targets, string lang, string sf)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) ? c.ToString() : "?";
        if (code != "0" || !root.TryGetProperty("translations", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"translation service said code={code}: {Trim(raw)}");

        var byId = targets.ToDictionary(m => m.SeqId.ToString());
        var outp = new List<ChatTranslation>();
        foreach (var e in arr.EnumerateArray())
        {
            var id = S(e, "id");
            // One target and one answer without an id: the client takes translations[1] then too.
            var m = byId.GetValueOrDefault(id) ?? (targets.Count == 1 ? targets[0] : null);
            if (m is null) continue;
            var text = (S(e, "translateMsg") is { Length: > 0 } tm ? tm : S(e, "text")).Replace("&#39;", "'");
            if (text.Length == 0) continue;
            outp.Add(new ChatTranslation(m.RoomId, m.SeqId, S(e, "targetLang") is { Length: > 0 } tl ? tl : lang, text,
                S(e, "originalLang") is { Length: > 0 } ol ? ol : sf));
        }
        return outp;
    }

    static string S(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    static string Trim(string s) => s.Length > 300 ? s[..300] + "..." : s;

    public static string Md5(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
