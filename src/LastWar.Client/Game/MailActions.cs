using System.Text;
using LastWar.Protocol.Sfs;

namespace LastWar.Client.Game;

/// <summary>
/// One inbox entry. Two things here contradict a first read of the command catalog: mail uids are
/// string GUIDs, unlike every other uuid in this protocol, which are longs; and reward claiming is
/// scoped per category, while marking as read is not.
/// </summary>
public sealed record MailEntry(string Uid, SfsObject Raw)
{
    public int Type => Raw.GetInt("type");

    /// <summary>
    /// rewardStatus=0 is the confirmed "unclaimed" value, but notification-only mail - alliance
    /// markers, battle reports - carries no reward and omits the field entirely. Since a missing
    /// or wrong-typed field also reads as 0, an absent field must be checked for first, or every
    /// battle report gets bucketed into a reward claim for a reward it does not have.
    /// </summary>
    public bool HasUnclaimedReward => Raw.HasNumber("rewardStatus") && Raw.GetInt("rewardStatus") == 0;
}

public sealed class MailActions(LastWarClient client)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>The page-size hint sent to the server, and the batch size used when claiming.</summary>
    const int PageSize = 100;

    /// <summary>Raw entries examined in one page's response, whatever page size the server honours.</summary>
    const int RawItemsPerPage = 1000;

    /// <summary>Total entries kept across all pages of one list.</summary>
    const int MaxMailPerFetch = 2000;

    /// <summary>
    /// A uid longer than this cannot be re-encoded into a claim request at all - the wire format
    /// caps a string at 65535 bytes - so it is dropped at read time. Left in, it would become its
    /// own single-item batch and fail locally at encode, which is easy to mistake for a dead
    /// connection.
    /// </summary>
    const int MaxUidBytes = 65535;

    /// <summary>Sanity ceilings on how much sequential claiming one run will do.</summary>
    const int MaxRewardTypes = 300;
    const int MaxBatchesPerLoop = 300;
    const int MaxRewardBatchesPerRun = 300;

    /// <summary>
    /// Lists the account's mail, paginating on the reply's more/lastUid/lastMailTime cursor.
    ///
    /// The first request sends time=0, clientseq="" and firstCmd="YES", which is the cold-start
    /// shape. The captured request came from a client mid-session with a warm local mail cache, so
    /// the cold-start values were never directly observed; this returns real mail, but whether it
    /// returns the account's full history or a recent window has not been verified.
    /// </summary>
    public async Task<GameActionResult> ListAsync(int maxPages = 10, CancellationToken ct = default)
    {
        var (mail, truncated) = await FetchAsync(maxPages, ct).ConfigureAwait(false);
        var rows = mail
            .Select(m => (IReadOnlyList<string>)new[]
            {
                m.Uid, m.Type.ToString(), m.HasUnclaimedReward ? "unclaimed" : "-",
            })
            .ToList();
        var unclaimed = mail.Count(m => m.HasUnclaimedReward);
        var summary = $"{mail.Count} mail, {unclaimed} with an unclaimed reward";
        if (truncated) summary += " (more available, page limit reached)";
        return new GameActionResult(summary, ["Uid", "Type", "Reward"], rows);
    }

    /// <summary>
    /// Marks every mail read and separately claims whatever rewards are outstanding - two
    /// genuinely independent actions.
    ///
    /// Mark-as-read is unconditional and not scoped by category: its request takes only uids. A
    /// live capture of the game's own "Claim All" on a tab of battle reports sent
    /// mail.read.status.betch for all 21 visible uids and no reward call at all. An earlier
    /// version of this only marked reward-bearing mail read, which left every notification badge
    /// showing forever.
    ///
    /// Reward claiming is scoped per category: one mail.reward.batch per distinct type, carrying
    /// only that type's uids. Types 3, 4 and 9 were seen live; the type-to-tab-name mapping was
    /// never worked out and is not needed, since this just groups by whatever type each mail
    /// already reports.
    /// </summary>
    public async Task<GameActionResult> ClaimAllAsync(CancellationToken ct = default)
    {
        var (mail, _) = await FetchAsync(20, ct).ConfigureAwait(false);
        if (mail.Count == 0) return GameActionResult.Text("no mail found");

        var rows = new List<IReadOnlyList<string>>();
        var read = 0;
        foreach (var batch in Batch(mail.Select(m => m.Uid).ToList()).Take(MaxBatchesPerLoop))
        {
            ct.ThrowIfCancellationRequested();
            var p = new SfsObject();
            p.PutUtfString("uids", string.Join(",", batch));
            var reply = await client
                .TryCallAsync("mail.read.status.betch", p, Timeout, ct).ConfigureAwait(false);
            if (Replies.Ok(reply)) read += batch.Count;
            rows.Add(["read", "-", batch.Count.ToString(), Replies.Outcome(reply)]);
        }

        // Types are claimed in sorted order so two runs over the same inbox attempt them in the
        // same order, which matters when one of them fails.
        var byType = new SortedDictionary<int, List<string>>();
        foreach (var m in mail)
        {
            if (!m.HasUnclaimedReward) continue;
            // A missing or wrong-typed type would coerce to 0 and merge this mail into a type=0
            // batch it does not belong to, so it is skipped rather than defaulted.
            if (!m.Raw.HasNumber("type")) continue;
            if (!byType.TryGetValue(m.Type, out var uids)) byType[m.Type] = uids = [];
            uids.Add(m.Uid);
        }

        var claimed = 0;
        var rewardBatches = 0;
        foreach (var (type, uids) in byType.Take(MaxRewardTypes))
        {
            foreach (var batch in Batch(uids).Take(MaxBatchesPerLoop))
            {
                ct.ThrowIfCancellationRequested();
                if (rewardBatches++ >= MaxRewardBatchesPerRun) break;
                var p = new SfsObject();
                p.PutUtfString("uids", string.Join(",", batch));
                p.PutInt("type", type);
                var reply = await client
                    .TryCallAsync("mail.reward.batch", p, Timeout, ct).ConfigureAwait(false);
                if (Replies.Ok(reply)) claimed += batch.Count;
                rows.Add(["reward", type.ToString(), batch.Count.ToString(), Replies.Outcome(reply)]);
            }
        }

        return new GameActionResult(
            $"marked {read} mail read, claimed rewards on {claimed} of {byType.Values.Sum(u => u.Count)}",
            ["Step", "Type", "Count", "Result"], rows);
    }

    /// <summary>
    /// Claims one mail's attachment with the client's own single-mail command (MailRewardMessage:
    /// mail.reward {uid}) and marks it read. Unlike the batch form it says when the reward was
    /// already taken (E100147), which matters because the mail list's rewardStatus lags a claim.
    /// </summary>
    public async Task<string> ClaimOneAsync(string uid, CancellationToken ct = default)
    {
        var p = new SfsObject();
        p.PutUtfString("uid", uid);
        var reply = await client.TryCallAsync("mail.reward", p, Timeout, ct).ConfigureAwait(false);
        await MarkReadAsync([uid], ct).ConfigureAwait(false);
        return Replies.Outcome(reply);
    }

    public async Task<string> MarkReadAsync(IReadOnlyList<string> uids, CancellationToken ct = default)
    {
        var p = new SfsObject();
        p.PutUtfString("uids", string.Join(",", uids));
        return Replies.Outcome(await client.TryCallAsync("mail.read.status.betch", p, Timeout, ct).ConfigureAwait(false));
    }

    public Task<(List<MailEntry> Mail, bool Truncated)> FetchAllAsync(int maxPages, CancellationToken ct = default) => FetchAsync(maxPages, ct);

    async Task<(List<MailEntry> Mail, bool Truncated)> FetchAsync(int maxPages, CancellationToken ct)
    {
        const string cmd = "chat.get.system.mails";
        var all = new List<MailEntry>();
        var seen = new HashSet<string>();
        var clientseq = "";
        var reqTime = 0L;
        var truncated = false;

        for (var page = 0; page < maxPages && all.Count < MaxMailPerFetch; page++)
        {
            truncated = false;
            var p = new SfsObject();
            p.PutUtfString("clientseq", clientseq);
            p.PutLong("time", reqTime);     // long: the mail cursor is a millisecond timestamp
            p.PutInt("count", PageSize);
            p.PutBool("isAll", true);
            if (page == 0) p.PutUtfString("firstCmd", "YES");

            // The page itself arrives as a push, not as the reply - the reply is a bare ack. The
            // watch is opened BEFORE the request so a push left over from an earlier, timed-out
            // page can never be read as this one's answer.
            using var watch = PushWatch.On(client.Connection, "push.chat.get.system.mails");
            await client.Connection.SendCommandAsync(cmd, p).ConfigureAwait(false);

            var reply = await watch.NextAsync(Timeout, ct).ConfigureAwait(false);
            if (reply is null) break;   // keep whatever earlier pages produced rather than losing the lot

            if (reply.TryGetArray("msg", out var arr))
            {
                foreach (var mo in arr.Objects().Take(RawItemsPerPage))
                {
                    if (all.Count >= MaxMailPerFetch) break;
                    var uid = mo.GetString("uid");
                    if (uid.Length == 0 || Encoding.UTF8.GetByteCount(uid) > MaxUidBytes) continue;
                    if (!seen.Add(uid)) continue;
                    all.Add(new MailEntry(uid, mo));
                }
            }

            if (!(reply.TryGet("more", out var mv) && mv.Value is true)) break;

            // A reply claiming more pages must also hand back a real cursor. An empty lastUid is
            // the same value the cold-start request carries, so following it re-sends the identical
            // request and spins on a stale cursor instead of advancing.
            var lastUid = reply.GetString("lastUid");
            if (lastUid.Length == 0 || Encoding.UTF8.GetByteCount(lastUid) > MaxUidBytes) break;
            clientseq = lastUid;
            reqTime = reply.GetLong("lastMailTime");
            truncated = true;
        }
        return (all, truncated);
    }

    /// <summary>
    /// Splits uids into batches capped both by item count and by the byte length of the string they
    /// will be joined into, the +1 per uid being the joining comma.
    ///
    /// Count alone is not enough. A mail uid is a server-supplied string that can itself run to
    /// 65535 bytes, so a handful of large ones - or a backlog of same-type rewards - blows the
    /// joined length well under 100 items, and the wire format refuses a string past 65535 bytes.
    ///
    /// A batch always takes at least one uid even when that uid alone exceeds the byte budget, so
    /// no single oversized uid can stall the walk; ListAsync drops those at read time anyway.
    /// </summary>
    static List<List<string>> Batch(
        List<string> uids, int maxCount = PageSize, int maxBytes = 60000)
    {
        var batches = new List<List<string>>();
        for (var i = 0; i < uids.Count;)
        {
            var end = i;
            var bytes = 0;
            while (end < uids.Count && end - i < maxCount &&
                   (end == i || bytes + Encoding.UTF8.GetByteCount(uids[end]) + 1 <= maxBytes))
            {
                bytes += Encoding.UTF8.GetByteCount(uids[end]) + 1;
                end++;
            }
            batches.Add(uids[i..end]);
            i = end;
        }
        return batches;
    }
}
