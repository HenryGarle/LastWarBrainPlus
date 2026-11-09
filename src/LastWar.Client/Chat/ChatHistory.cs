using LastWar.Core.Model;

namespace LastWar.Client.Chat;

/// <summary>
/// Paging backwards through a room's history.
///
/// Two commands, because the first page is a different shape from the rest. history.roomsv2 takes
/// a per-room FLAG ("0" newest, "1" oldest) rather than a cursor, and returns the newest block.
/// Everything older comes from history.roomgoto2 with sort=2 - plain history.roomgoto returns the
/// same window every time and never advances, which is a quiet way to loop forever.
/// </summary>
public static class ChatHistory
{
    /// <summary>The command and parameters for one page; cursor 0 means "the newest page".</summary>
    public static (string Command, object Params) Request(string room, long cursor, int count) =>
        cursor == 0
            ? ("history.roomsv2", new Dictionary<string, object?>
            {
                ["rooms"] = new Dictionary<string, string> { [room] = "0" },
            })
            : ("history.roomgoto2", new Dictionary<string, object?>
            {
                ["roomId"] = room,
                ["seqId"] = cursor,
                ["sort"] = 2,
                ["count"] = count,
            });

    /// <summary>The two commands answer in different shapes: rooms[].msgs[] first, then a flat msg[].</summary>
    public static IReadOnlyList<ChatMessage> Parse(ChatFrame reply)
    {
        var messages = ChatMessageParser.FromHistory(reply).SelectMany(r => r.Messages).ToList();
        return messages.Count > 0 ? messages : ChatMessageParser.FromGoto(reply);
    }

    /// <summary>
    /// One page on a connection nobody else is reading. Signed, then waited for separately:
    /// CallAsync sends UNSIGNED, and an unsigned history request comes back 1003 unexplained.
    /// When a <see cref="ChatListener"/> owns the connection, use its RequestAsync instead.
    /// </summary>
    public static async Task<IReadOnlyList<ChatMessage>> PageAsync(
        ChatConnection chat, string room, long cursor, int count, CancellationToken ct = default)
    {
        var (command, ps) = Request(room, cursor, count);
        await chat.SendSignedAsync(command, ps, ct).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await foreach (var f in chat.Frames(linked.Token).ConfigureAwait(false))
                if (f.Command == command) return Parse(f);
        }
        catch (OperationCanceledException) { }
        return [];
    }
}
