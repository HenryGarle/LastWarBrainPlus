using LastWar.Protocol.Sfs;

namespace LastWar.Client.Game;

/// <summary>One owned building, as the login pushes describe it.</summary>
public sealed record BuildingInfo(long Uuid, int BId, int Level, int PointId);

/// <summary>
/// One city visitor. StartTime has no caller yet but is kept: it is the field that separates a
/// visitor still arriving (the server answers a greet with visitor_err_coming) from one that can
/// actually be greeted, so a future caller can skip a doomed call instead of learning it from the
/// error reply.
/// </summary>
public sealed record VisitorInfo(long Uid, int EventId, int VisitorId, long StartTime);

/// <summary>
/// Buildings and visitors are never requested - no "list my buildings" command exists. They are
/// pushed once, during login, and this reads them off the inbound stream.
///
/// It reads through WaitForCommandAsync, which discards envelopes it was not asked for and leaves
/// command replies alone. Draining GameConnection.Pushes() instead would work exactly once and
/// then steal every subsequent command reply, because Pushes() is the same channel
/// WaitForCommandAsync reads from.
///
/// Every connection made through LastWarClient has a LoginPushRecorder attached at connect, and
/// that is the normal path - it taps the frames instead of consuming them, so the lists survive
/// for the life of the connection and any number of operations can read them. The live read below
/// is the fallback for a connection built some other way, and it is destructive: it works once.
/// </summary>
internal static class LoginPushes
{
    /// <summary>
    /// Defensive ceiling on raw entries examined in one push array. There is no server-sent
    /// "expected building count" to clamp against, so this is a flat bound on scan cost, not a
    /// protocol limit.
    /// </summary>
    const int MaxItemsPerPush = 2000;

    /// <summary>
    /// Ceiling on what one read accumulates across every push in its window. Dedup is keyed on
    /// uuid, a value the peer chooses, so many small pushes carrying distinct fake uuids would
    /// otherwise grow these lists without bound. Set far above any real account's building count.
    /// </summary>
    const int MaxBuildings = 300;

    /// <summary>
    /// The same ceiling for visitors, and it also bounds GreetAllAsync: that sends one call per
    /// visitor, each able to cost a full command timeout against a peer that stops answering.
    /// </summary>
    const int MaxVisitors = 300;

    /// <summary>Applied when the init push's own maxNum is absent or not sane.</summary>
    const int DefaultVisitorCeiling = 25;

    public static async Task<(List<BuildingInfo> Buildings, List<VisitorInfo> Visitors)> ReadAsync(
        GameConnection conn, TimeSpan wait, CancellationToken ct = default)
    {
        if (LoginPushRecorder.For(conn) is { } recorder)
            return await recorder.WaitAsync(wait, ct).ConfigureAwait(false);

        var buildings = new List<BuildingInfo>();
        var visitors = new List<VisitorInfo>();
        var seenBuildings = new HashSet<long>();
        var seenVisitors = new HashSet<long>();

        // original is the caller's real budget and is never moved. deadline can be pulled in by a
        // qualifying push, so a slow multi-push bootstrap still gets a short trailing window, but
        // a peer that keeps re-sending pushes can never hold the read open past what was asked for.
        var original = DateTimeOffset.UtcNow + wait;
        var deadline = original;
        var gotInit = false;

        void AddBuilding(SfsObject b)
        {
            if (buildings.Count >= MaxBuildings) return;
            var uuid = b.GetLong("uuid");
            if (!seenBuildings.Add(uuid)) return;
            buildings.Add(new BuildingInfo(uuid, b.GetInt("bId"), b.GetInt("lv"), b.GetInt("pId")));
        }

        while (buildings.Count < MaxBuildings && visitors.Count < MaxVisitors)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            Envelope e;
            try
            {
                e = await conn.WaitForCommandAsync(remaining, ct,
                    "init", "push.init.build", "push.add.building",
                    "push.queue.add", "push.build.queue.info").ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                break;  // waited long enough for this window; go with what arrived
            }

            switch (e.Command)
            {
                case "init":
                    // The real bootstrap. building_new is what the game's own BuildManager reads;
                    // push.init.build below is a rarely-fired secondary push despite its name.
                    gotInit = true;
                    if (e.Params.TryGetArray("building_new", out var newBuilds))
                        foreach (var b in newBuilds.Objects().Take(MaxItemsPerPush)) AddBuilding(b);
                    foreach (var v in ParseVisitors(e.Params))
                        if (visitors.Count < MaxVisitors && seenVisitors.Add(v.Uid)) visitors.Add(v);
                    deadline = Earlier(DateTimeOffset.UtcNow.AddSeconds(3), original);
                    break;

                case "push.init.build":
                    if (e.Params.TryGetArray("defaultBuilds", out var defaults))
                        foreach (var wrapper in defaults.Objects().Take(MaxItemsPerPush))
                            if (wrapper.TryGetObject("buildInfo", out var bi)) AddBuilding(bi);
                    // Only shorten the window once the authoritative init push has landed. Doing it
                    // earlier lets this inferior source cut off the wait for init entirely.
                    if (gotInit) deadline = Earlier(DateTimeOffset.UtcNow.AddSeconds(3), original);
                    break;

                case "push.add.building":
                    if (e.Params.TryGetArray("buildings", out var added))
                        foreach (var b in added.Objects().Take(MaxItemsPerPush)) AddBuilding(b);
                    break;

                // push.queue.add and push.build.queue.info describe production-queue items, which
                // are separate entities from buildings. Matched so they are consumed in step with
                // the rest of the burst, but they contribute nothing.
            }
        }
        return (buildings, visitors);
    }

    static DateTimeOffset Earlier(DateTimeOffset candidate, DateTimeOffset original) =>
        candidate > original ? original : candidate;

    /// <summary>
    /// Visitors ride in the same init push as building_new, under a sibling visitor={addNum,
    /// maxNum, list} field. maxNum is the server's own capacity (5 on a live account), but it is
    /// server-supplied and so cannot be the only bound on itself - it is clamped to MaxVisitors.
    /// </summary>
    internal static IEnumerable<VisitorInfo> ParseVisitors(SfsObject initParams)
    {
        if (!initParams.TryGetObject("visitor", out var visitor)) yield break;
        if (!visitor.TryGetArray("list", out var list)) yield break;

        var limit = DefaultVisitorCeiling;
        if (visitor.HasNumber("maxNum") && visitor.GetInt("maxNum") > 0)
            limit = Math.Min(visitor.GetInt("maxNum"), MaxVisitors);

        foreach (var v in list.Objects().Take(limit))
        {
            // A wrong-typed uid would coerce to 0 and collide with another one in the dedup set,
            // silently dropping a real visitor as a duplicate.
            if (!v.HasNumber("uid")) continue;
            yield return new VisitorInfo(
                v.GetLong("uid"), v.GetInt("eventId"), v.GetInt("visitorId"), v.GetLong("startTime"));
        }
    }
}
