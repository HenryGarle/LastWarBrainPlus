using System.Text.Json;
using System.Text.Json.Nodes;
using LastWar.Client.Game;
using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Server;

/// <summary>
/// Admin chat client on one of our accounts (Henry, 2026-10-01): its private rooms and their messages,
/// read live from the game through the runner's chat link, replies sent as that account - and the
/// battle reports people share into those rooms, collected for BattleReportStore.
///
/// Private rooms are never stored with the public chat (ChatHub.Accept drops them): viewers can read
/// chat, DMs are admin only. What IS kept is each battle report share and the report behind it.
/// </summary>
public sealed class Inbox(RunnerRegistry fleet, ChatHub hub, BattleReportStore reports, GameText text)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    const int ReportShare = 4;   // PostType of a shared battle report (attachment {reportUid, toUser, mailType, resultState})

    public static readonly TimeSpan CollectEvery = TimeSpan.FromMinutes(30);
    public string? LastError { get; private set; }
    public DateTimeOffset? LastCollectAt { get; private set; }

    /// <summary>The account's private rooms (the list the game's own chat window shows), newest first.</summary>
    public async Task<List<Dictionary<string, object?>>> RoomsAsync(string runnerId, CancellationToken ct)
    {
        var me = hub.UidOf(runnerId);
        var raw = await hub.RawAsync(runnerId, "room.getCustomP2PRoomListV2", new Dictionary<string, object?>
        {
            ["p2pIndex"] = 0, ["cgIndex"] = 0, ["kgIndex"] = 0, ["pageSize"] = 100, ["group"] = "custom_group",
        }, ct) ?? throw new TimeoutException("the chat server did not answer the room list");
        var rooms = JsonNode.Parse(raw)?["result"]?["rooms"]?.AsObject() ?? [];
        var outp = new List<Dictionary<string, object?>>();
        foreach (var (id, v) in rooms)
        {
            var r = v is JsonValue s && s.TryGetValue<string>(out var str) ? JsonNode.Parse(str) : v;
            var msgs = r?["msgs"]?["msgs"]?.AsArray() ?? [];
            var other = Other(id, me);
            var otherMsg = msgs.LastOrDefault(m => S(m?["sender"]) == other);
            var last = msgs.LastOrDefault();
            outp.Add(new()
            {
                ["room"] = id, ["name"] = S(r?["name"]), ["other"] = other,
                ["otherName"] = S(otherMsg?["senderInfo"]?["userName"]), ["otherTag"] = S(otherMsg?["senderInfo"]?["abbr"]),
                ["lastSeq"] = (long.TryParse(S(r?["msgs"]?["lastSeqId"]), out var ls) ? ls : (long?)null), ["readSeq"] = long.TryParse(S(r?["readSeqId"]), out var rs) ? rs : null,
                ["lastAt"] = long.TryParse(S(r?["msgs"]?["lastMsgTime"]), out var t) ? DateTimeOffset.FromUnixTimeMilliseconds(t).ToString("O") : null,
                ["lastText"] = Preview(last),
            });
        }
        return [.. outp.OrderByDescending(r => (string?)r["lastAt"])];
    }

    /// <summary>The other member of a DM room: custom_&lt;a&gt;_&lt;b&gt;_v2 holds both uids.</summary>
    public static string? Other(string room, string me)
    {
        var parts = room.Split('_');
        return parts.Length >= 3 && parts[0] == "custom" ? parts.Skip(1).Take(2).FirstOrDefault(p => p != me && p.All(char.IsAsciiDigit)) : null;
    }

    static string? S(JsonNode? n) => n is null ? null : n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();

    static string? Preview(JsonNode? m)
    {
        if (m is null) return null;
        var post = (int?)m["extra"]?["post"] ?? 0;
        return post switch { ReportShare => "[battle report]", 13 => "[map position]", _ => S(m["msg"]) };
    }

    /// <summary>One page of a room (before = a seqId for older, null = newest), oldest first; shares found are recorded.</summary>
    public async Task<List<Dictionary<string, object?>>> MessagesAsync(string runnerId, string room, long? before, CancellationToken ct)
    {
        var me = hub.UidOf(runnerId);
        var page = await hub.PageAsync(runnerId, room, before ?? 0, ct);
        Record(runnerId, page);
        var fetched = reports.List().ToDictionary(r => (string)r["reportUid"]!, r => r);
        return [.. page.OrderBy(m => m.SeqId).Select(m =>
        {
            var (post, att) = Extra(m.Raw);
            var reportUid = post == ReportShare ? (string?)att?["reportUid"] : null;
            var target = post == ReportShare ? Custom(m.Raw)?["target"] : null;
            return new Dictionary<string, object?>
            {
                ["seq"] = m.SeqId, ["sender"] = m.SenderUid, ["name"] = m.SenderName, ["tag"] = m.SenderAlliance, ["at"] = SentAt(m).ToString("O"),
                ["text"] = m.Text, ["post"] = post, ["attachment"] = att, ["mine"] = m.SenderUid == me,
                ["opponent"] = target is null ? null : new { name = S(target["userName"]), tag = S(target["allianceAbbr"]) },
                ["report"] = reportUid is null ? null : fetched.GetValueOrDefault(reportUid) is { } r
                    ? new { reportUid, fetched = r["fetched"], error = r["error"], mailType = text.MailTypeName(Convert.ToInt32(r["mailType"])) }
                    : new { reportUid, fetched = (object?)false, error = (object?)null, mailType = text.MailTypeName((int?)att?["mailType"] ?? 0) },
            };
        })];
    }

    /// <summary>
    /// When the chat server took the message (serverTime, ms). sendTime is the SENDER's clock: seen two
    /// hours ahead on a DM on 2026-10-01, which made fresh messages read "just now" hours later.
    /// </summary>
    static DateTimeOffset SentAt(ChatMessage m)
    {
        try { return JsonNode.Parse(m.Raw)?["serverTime"] is JsonValue v && v.TryGetValue<long>(out var ms) && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : m.SentAt; }
        catch (JsonException) { return m.SentAt; }
    }

    /// <summary>A report share's extra.customJsonParam: {isDef, reportResult, target {userName, allianceAbbr}}.</summary>
    static JsonNode? Custom(string raw)
    {
        try { return JsonNode.Parse(raw)?["extra"]?["customJsonParam"] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('{') ? JsonNode.Parse(s) : null; }
        catch (JsonException) { return null; }
    }

    public static (int Post, JsonNode? Attachment) Extra(string raw)
    {
        try
        {
            var m = JsonNode.Parse(raw);
            var post = (int?)m?["extra"]?["post"] ?? 0;
            var att = m?["extra"]?["attachmentId"] is JsonValue a && a.TryGetValue<string>(out var s) && s.StartsWith('{') ? JsonNode.Parse(s) : null;
            return (post, att);
        }
        catch (JsonException) { return (0, null); }
    }

    int Record(string runnerId, IEnumerable<ChatMessage> page) => reports.AddShares(page.Select(m => (m, x: Extra(m.Raw)))
        .Where(p => p.x.Post == ReportShare && p.x.Attachment?["reportUid"] is not null)
        .Select(p => new BattleReportStore.Share((string)p.x.Attachment!["reportUid"]!, (string?)p.x.Attachment["toUser"] ?? p.m.SenderUid,
            p.m.RoomId, p.m.SeqId, p.m.SenderUid, p.m.SenderName, SentAt(p.m), (int?)p.x.Attachment["mailType"] ?? 0,
            (int?)p.x.Attachment["resultState"] ?? 0, runnerId)));

    /// <summary>A reply as the runner's account, to the other member of a DM room.</summary>
    public async Task<GameActionResult> SendAsync(string runnerId, string room, string message, CancellationToken ct)
    {
        var to = Other(room, hub.UidOf(runnerId)) ?? throw new ArgumentException("only direct-message rooms can be replied to here");
        var client = fleet.Get(runnerId).Client ?? throw new InvalidOperationException("runner is not running");
        return await DirectMessage.SendAsync(client, to, message, "en", ct);
    }

    /// <summary>
    /// Walks every private room back to its start (at most <paramref name="pages"/> pages each) for report
    /// shares, then fetches every report not held yet. Returns (shares new, reports fetched, failed).
    /// </summary>
    public async Task<(int Shares, int Fetched, int Failed)> CollectAsync(string runnerId, int pages, CancellationToken ct)
    {
        var shares = 0;
        foreach (var room in await RoomsAsync(runnerId, ct))
        {
            long cursor = 0;
            for (var i = 0; i < pages; i++)
            {
                var page = await hub.PageAsync(runnerId, (string)room["room"]!, cursor, ct);
                if (page.Count == 0) break;
                shares += Record(runnerId, page);
                var oldest = page.Min(m => m.SeqId);
                if (oldest <= 1 || (cursor != 0 && oldest >= cursor)) break;
                cursor = oldest;
                await Task.Delay(LastWar.Client.Jitter.Of(TimeSpan.FromMilliseconds(300)), ct);
            }
        }
        var (fetched, failed) = await FetchPendingAsync(runnerId, ct);
        LastCollectAt = DateTimeOffset.UtcNow;
        return (shares, fetched, failed);
    }

    /// <summary>Opens each pending report the way the game does (mail.read, then the report CDN) and stores it.</summary>
    public async Task<(int Fetched, int Failed)> FetchPendingAsync(string runnerId, CancellationToken ct)
    {
        var client = fleet.Get(runnerId).Client ?? throw new InvalidOperationException("runner is not running");
        int ok = 0, bad = 0;
        foreach (var (uid, owner) in reports.Pending())
        {
            try
            {
                var mail = await BattleReports.FetchSharedAsync(client, uid, owner, ct) ?? throw new TimeoutException("the game sent no mail for it");
                var m = LastWar.Protocol.Sfs.SfsJson.Write(mail)?["msg"]?[0] ?? throw new InvalidOperationException("no mail in the reply (expired?)");
                var contents = JsonNode.Parse((string?)m["contentsLocal"] ?? "{}");
                var content = (string?)contents?["obj"]?["battleContent"] ?? throw new InvalidOperationException("the mail holds no battle report");
                var (report, bytes) = await BattleReports.FullAsync(content, Http, ct);
                var fights = BattleReports.Fights(report, text.Catalog());
                reports.SaveReport(uid, owner, (int?)m["type"] ?? 0, report.Uuid.ToString(), report.Type,
                    report.BattleTime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(report.BattleTime) : null, report.Address, bytes,
                    BattleReports.ToJson(report), fights);
                ok++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { reports.SaveReport(uid, owner, 0, "", 0, null, "", null, "", [], ex.Message); bad++; }
            await Task.Delay(LastWar.Client.Jitter.Of(TimeSpan.FromMilliseconds(500)), ct);
        }
        return (ok, bad);
    }

    /// <summary>Every <see cref="CollectEvery"/>: each runner with chat open collects its shared reports.</summary>
    public async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(LastWar.Client.Jitter.Of(CollectEvery), ct); } catch (OperationCanceledException) { return; }
            foreach (var r in fleet.All.Where(r => hub.Listening(r.Spec.Id)).ToList())
            {
                try { await CollectAsync(r.Spec.Id, 5, ct); LastError = null; }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { LastError = $"{r.Spec.Id}: {ex.Message}"; }
            }
        }
    }

    /// <summary>Re-reads every stored report into battle_fight (after the reading of a report changes).</summary>
    public int Rebuild()
    {
        var n = 0;
        foreach (var (uid, raw) in reports.Raws()) { reports.ReplaceFights(uid, BattleReports.Fights(BattleReports.Decode(raw), text.Catalog())); n++; }
        return n;
    }
}
