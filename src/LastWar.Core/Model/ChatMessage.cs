namespace LastWar.Core.Model;

/// <summary>
/// One chat message. seqId is the room's own monotonic counter, so (RoomId, SeqId) is a
/// natural primary key and makes both live capture and history backfill idempotent.
/// </summary>
public sealed record ChatMessage
{
    public required string RoomId { get; init; }
    public required long SeqId { get; init; }
    public string Group { get; init; } = "";
    public string SenderUid { get; init; } = "";
    public string SenderName { get; init; } = "";
    public string SenderAlliance { get; init; } = "";
    public string Text { get; init; } = "";
    public DateTimeOffset SentAt { get; init; }
    public int Type { get; init; }
    public string Raw { get; init; } = "";

    /// <summary>Origin server of the sender, from the uid suffix.</summary>
    public ServerId? SenderOriginServer => new PlayerUid(SenderUid).OriginServer;
}
