using LastWar.Client.Chat;
using LastWar.Storage;

namespace LastWar.Operations;

public static partial class Operations
{
    /// <summary>
    /// Chat, as operations rather than a supervisor script. The backfill is resumable in exactly
    /// the same way the script was - storage is idempotent on (room, seq) - but it reports through
    /// the same progress channel as everything else and needs nothing outside the app.
    /// </summary>
    static void AddChatOperations()
    {
        Add("chat.rooms", "Chat", "Rooms held",
            "What has been captured so far, and the sequence range each room covers. Reads the "
            + "database only, so it never disturbs a backfill in flight.",
            c =>
            {
                var store = new ChatStore(c.Database);
                store.EnsureSchema();
                var (messages, rooms) = store.Stats();
                var room = c.Text("room");
                if (room.Length > 0)
                {
                    var (n, lo, hi) = store.RoomStats(room);
                    return Task.FromResult(OperationResult.Table(
                        $"{room}: {n} messages, seq {lo}..{hi}",
                        ["room", "messages", "oldest", "newest"], [[room, n, lo, hi]]));
                }
                return Task.FromResult(OperationResult.Text(
                    $"{messages} messages across {rooms} room(s) - name a room for its range"));
            },
            needsSession: false,
            ps: new OperationParam("room", "Room", "text", "country_1967"));

        Add("chat.backfill", "Chat", "Backfill chat history",
            "Pages backwards through a room's history and stores what it finds. Resumes below the "
            + "oldest message already held, so it can be stopped and restarted freely, and paces "
            + "itself so a long pull stays gentle on the gateway.",
            async c =>
            {
                var client = c.Require();
                var creds = client.ChatCredentials
                    ?? throw new InvalidOperationException("no chat credentials yet - the session is still starting");

                var room = c.Text("room", "country_1967");
                // until: keep paging back until the oldest message is at or before this date (ISO);
                // pages is then only a safety cap.
                var until = c.Text("until", "");
                var pages = c.Int("pages", until.Length > 0 ? 5000 : 20);
                var delay = TimeSpan.FromMilliseconds(c.Int("delay", 700));
                var count = c.Int("count", 50);

                var store = new ChatStore(c.Database);
                store.EnsureSchema();

                var (preferred, _) = await new ChatDirectory().FetchAsync(creds.Uid, client.Server.Value);
                if (preferred.Count == 0) throw new InvalidOperationException("no chat gateway offered");

                ChatConnection? chat = null;
                foreach (var ep in preferred)
                {
                    try { chat = await ChatConnection.ConnectAsync(ep, creds.Uid, creds.ChatToken); break; }
                    catch (TimeoutException) { /* try the next gateway */ }
                }
                if (chat is null) throw new InvalidOperationException("no gateway accepted the handshake");
                await using var _ = chat;

                long cursor = c.Flag("resume", true) ? store.OldestSeqId(room) ?? 0 : 0;
                var fetched = 0;
                var added = 0;

                for (var page = 0; page < pages; page++)
                {
                    var messages = await ChatHistory.PageAsync(chat, room, cursor, count, c.Cancellation);
                    if (messages.Count == 0) break;

                    fetched += messages.Count;
                    added += store.Insert(messages);
                    var oldest = messages.Min(m => m.SeqId);

                    var oldestAt = messages.Min(m => m.SentAt).ToUniversalTime().ToString("O");
                    c.Report($"page {page + 1}: back to {oldestAt[..16].Replace('T', ' ')}, {added} new so far");
                    if (oldest <= 1) break;
                    if (until.Length > 0 && string.CompareOrdinal(oldestAt, until) <= 0) break;
                    // A page that does not reach further back than the last one means the room has
                    // given up everything it will; continuing would just re-fetch it forever.
                    if (cursor != 0 && oldest >= cursor) break;
                    cursor = oldest;

                    if (delay > TimeSpan.Zero) await Task.Delay(LastWar.Client.Jitter.Of(delay), c.Cancellation);
                }

                var (held, lo, hi) = store.RoomStats(room);
                return OperationResult.Table(
                    $"fetched {fetched}, {added} new - {room} now holds {held} messages",
                    ["room", "messages", "oldest", "newest"], [[room, held, lo, hi]]);
            },
            ps:
            [
                new OperationParam("room", "Room", "text", "country_1967", Required: true),
                new OperationParam("pages", "Pages this run", "number", "20"),
                new OperationParam("count", "Messages per page", "number", "50"),
                new OperationParam("delay", "Gap between pages (ms)", "number", "700"),
                new OperationParam("resume", "Continue below what is held", "bool", "true"),
                new OperationParam("until", "Back to (date, e.g. 2026-07-07)", "text", ""),
            ]);

        // The chat page's "fetch older from the game": the same backfill, on a throwaway guest,
        // so no runner has to be picked or kept.
        Add("chat.backfill.guest", "Chat", "Backfill chat history (guest)",
            "chat.backfill on a throwaway guest: pages backwards below the oldest message held.",
            async c =>
            {
                OperationResult? result = null;
                await WithGuestAsync(c, "chatback", async g =>
                {
                    await g.WaitForChatCredentialsAsync(TimeSpan.FromSeconds(30), c.Cancellation);
                    result = await RunAsync("chat.backfill", new OperationContext
                    {
                        Args = c.Args, Database = c.Database, DataDir = c.DataDir, Session = g, Progress = c.Progress, Cancellation = c.Cancellation,
                    });
                });
                return result ?? OperationResult.Text("no result");
            },
            needsSession: false,
            ps:
            [
                new OperationParam("room", "Room", "text", "country_1967", Required: true),
                new OperationParam("pages", "Pages this run", "number", "20"),
                new OperationParam("until", "Back to (date, e.g. 2026-07-07)", "text", ""),
            ]);
    }
}
