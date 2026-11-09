using LastWar.Protocol.Sfs;

namespace LastWar.Client.Game;

/// <summary>
/// A private chat message to one player, over the GAME socket: chat.room.send.or.create.send is the
/// command the chat UI's share flow uses (Chat.NetMessage.ChatShareCommand); it sends into the
/// private room with that player, creating the room when there is none. Fields as ChatShareCommand
/// writes them: CurServerId (the sender's server), post (PostType: 0 text, 13 a map point share),
/// lang, msg, chatType 2 (ChatShareChannel.TO_PERSON), toUser (the recipient uid).
/// </summary>
public static class DirectMessage
{
    public const int Text = 0;
    public const int PointShare = 13;   // PostType.Text_PointShare - for the location sharing to come
    const int ToPerson = 2;

    public static Task<GameActionResult> SendAsync(LastWarClient client, string toUid, string text, string lang = "en", CancellationToken ct = default) =>
        SendAsync(client, toUid, Text, text, null, lang, ct);

    /// <summary>
    /// A map point the recipient can tap to jump to, as real clients share it (seen in stored chat):
    /// post 13, msg "?", attachmentId = JSON {x, y, sid, worldId 0, worldType 0, oname = label} plus,
    /// for a player's base, posType 3 and the player's uid. Without posType it is a plain point.
    /// </summary>
    public static Task<GameActionResult> SharePointAsync(LastWarClient client, string toUid, int server, int x, int y, string label,
        string? playerUid = null, CancellationToken ct = default)
    {
        var point = new System.Text.Json.Nodes.JsonObject { ["x"] = x, ["y"] = y, ["sid"] = server, ["worldId"] = 0, ["worldType"] = 0, ["oname"] = label };
        if (playerUid is { Length: > 0 }) { point["posType"] = 3; point["uid"] = playerUid; }
        return SendAsync(client, toUid, PointShare, "?", point.ToJsonString(), "en", ct);
    }

    public const int MarchShare = 46;   // what players post for a truck: tapping it follows the truck, wherever it is now

    /// <summary>
    /// A truck the recipient can tap to jump to - and it follows the truck (the game resolves the march's current
    /// position from marchUuid). Shape copied from 3,491 real shares in stored chat (2026-10-07): post 46, msg "?",
    /// attachment {postType 46, marchType 14, worldId 0, x, y (where it was), sid, serverId, marchUuid,
    /// quality, oname 457592 ("{0}'s {1} Truck"), onameParam1 "#server [tag]name", onameParam2 "UR"/"SSR"/...}.
    /// </summary>
    public static Task<GameActionResult> ShareTruckAsync(LastWarClient client, string toUid, int server, int x, int y, long marchUuid,
        int quality, string ownerLabel, CancellationToken ct = default)
    {
        var a = new System.Text.Json.Nodes.JsonObject
        {
            ["postType"] = MarchShare, ["marchType"] = 14, ["worldId"] = 0, ["x"] = x, ["y"] = y, ["sid"] = server, ["serverId"] = server,
            ["marchUuid"] = marchUuid, ["quality"] = quality, ["oname"] = 457592, ["onameParam1"] = ownerLabel,
            ["onameParam2"] = quality switch { 1 => "N", 2 => "R", 3 => "SR", 4 => "SSR", _ => "UR" },
        };
        return SendAsync(client, toUid, MarchShare, "?", a.ToJsonString(), "en", ct);
    }

    static async Task<GameActionResult> SendAsync(LastWarClient client, string toUid, int post, string text, string? attachment, string lang, CancellationToken ct)
    {
        var p = new SfsObject();
        p.PutInt("CurServerId", client.Server.Value);
        p.PutLong("post", post);
        p.PutUtfString("lang", lang);
        p.PutUtfString("msg", text);
        if (attachment is not null) p.PutUtfString("attachmentId", attachment);
        p.PutInt("chatType", ToPerson);
        p.PutUtfString("toUser", toUid);
        var reply = await client.TryCallAsync("chat.room.send.or.create.send", p, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return GameActionResult.Text($"message to {toUid}: {Replies.Outcome(reply)}");
    }
}
