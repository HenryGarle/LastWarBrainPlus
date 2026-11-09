using System.Text.Json;
using LastWar.Core.Model;

namespace LastWar.Client.Chat;

/// <summary>Turns chat frames into <see cref="ChatMessage"/>. Live pushes and history
/// entries share one body shape, so both routes go through the same reader.</summary>
public static class ChatMessageParser
{
    /// <summary>Push commands that carry a single message body.</summary>
    public static readonly string[] MessagePushes =
        ["push.chat.room", "push.chat.up.room", "push.chat.p2p.room"];

    public static ChatMessage? FromBody(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("roomId", out var room) || room.ValueKind != JsonValueKind.String) return null;
        if (!e.TryGetProperty("seqId", out var seq)) return null;

        var info = e.TryGetProperty("senderInfo", out var si) && si.ValueKind == JsonValueKind.Object
            ? si : default;

        return new ChatMessage
        {
            RoomId = room.GetString() ?? "",
            SeqId = seq.ValueKind == JsonValueKind.Number ? seq.GetInt64() : 0,
            Group = Str(e, "group"),
            SenderUid = Str(e, "sender"),
            SenderName = info.ValueKind == JsonValueKind.Object ? Str(info, "userName") : "",
            SenderAlliance = info.ValueKind == JsonValueKind.Object ? Str(info, "abbr") : "",
            Text = Str(e, "msg"),
            // sendTime is unix seconds - except some messages (seen 2026-09-28 paging back 1963 and
            // 1972) carry milliseconds; anything past year 5000 in seconds is read as ms.
            SentAt = e.TryGetProperty("sendTime", out var t) && t.ValueKind == JsonValueKind.Number
                ? (t.GetInt64() is var st && st > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(st) : DateTimeOffset.FromUnixTimeSeconds(st))
                : DateTimeOffset.UtcNow,
            Type = e.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.Number
                ? ty.GetInt32() : 0,
            Raw = e.GetRawText(),
        };
    }

    public static ChatMessage? FromPush(ChatFrame f) =>
        MessagePushes.Contains(f.Command) && f.Body is { } b ? FromBody(b) : null;

    /// <summary>
    /// Reads history.roomsv2 / history.roomv2 replies: result.rooms[] each with msgs[].
    /// Also reports each room's first/last seqId, which is what paging backwards needs.
    /// </summary>
    public static IReadOnlyList<(string RoomId, long FirstSeqId, long LastSeqId, List<ChatMessage> Messages)>
        FromHistory(ChatFrame f)
    {
        var outp = new List<(string, long, long, List<ChatMessage>)>();
        if (f.Body is not { } body || !body.TryGetProperty("rooms", out var rooms)) return outp;
        if (rooms.ValueKind != JsonValueKind.Array) return outp;

        foreach (var room in rooms.EnumerateArray())
        {
            var msgs = new List<ChatMessage>();
            if (room.TryGetProperty("msgs", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray())
                    if (FromBody(m) is { } parsed) msgs.Add(parsed);

            outp.Add((
                Str(room, "roomId") is { Length: > 0 } id ? id : msgs.FirstOrDefault()?.RoomId ?? "",
                Num(room, "firstSeqId"),
                Num(room, "lastSeqId"),
                msgs));
        }
        return outp;
    }

    /// <summary>
    /// history.roomgoto answers with result.msg[] (singular) rather than the
    /// result.rooms[].msgs[] shape the other history commands use.
    /// </summary>
    public static List<ChatMessage> FromGoto(ChatFrame f)
    {
        var outp = new List<ChatMessage>();
        if (f.Body is not { } body) return outp;
        foreach (var key in new[] { "msg", "msgs" })
        {
            if (!body.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var m in arr.EnumerateArray())
                if (FromBody(m) is { } parsed) outp.Add(parsed);
            if (outp.Count > 0) break;
        }
        return outp;
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static long Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
