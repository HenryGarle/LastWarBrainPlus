namespace LastWar.Storage;

public sealed partial class Insights
{
    // Per-uid lookups used inside larger queries. ix_power_uid makes each one an index seek.
    const string LatestPower =
        "(SELECT p.power FROM power_sample p WHERE p.uid = {0} ORDER BY p.taken_at DESC LIMIT 1)";
    const string PreviousPower =
        "(SELECT p.power FROM power_sample p WHERE p.uid = {0} ORDER BY p.taken_at DESC LIMIT 1 OFFSET 1)";
    const string LatestHero =
        "(SELECT b.value FROM board_sample b WHERE b.uid = {0} AND b.board_type = 13 ORDER BY b.taken_at DESC LIMIT 1)";

    static string F(string template, string col) => string.Format(template, col);

    // Activity over the window (see PlayerActivity): 1/0, or NULL when the player has no readings.
    // "Compared" = could have been caught: two readings, one of them inside the window.
    static string Active7(string uid, string iso) =>
        $"(SELECT pa.last_active_at >= {PlayerActivity.WindowStart(iso)} FROM player_activity pa WHERE pa.uid = {uid})";
    static string Compared7(string uid, string iso) =>
        $"(SELECT true FROM player_activity pa WHERE pa.uid = {uid})";   // iso kept for callers; every profiled player can be judged
    static string LastRise(string uid) =>
        $"(SELECT NULLIF(pa.last_rise_at, '') FROM player_activity pa WHERE pa.uid = {uid})";

    /// <summary>When the game last refreshed them (profile lastUpdateTime) - the "last seen" column.</summary>
    static string LastSeen(string uid) =>
        $"(SELECT NULLIF(pa.last_seen_at, '') FROM player_activity pa WHERE pa.uid = {uid})";

    /// <summary>
    /// An alliance as of its newest sighting: members with power, change and hero power; its size
    /// over every scan; who joined and who left since the scan before (and where the leavers are
    /// now); who speaks for it in chat; and which of its members have applied to transfer.
    /// </summary>
    public Dictionary<string, object?> Alliance(string allianceId)
    {
        using var c = Open();
        new AllianceMembership(path).EnsureSchema();
        var at = Q(c, """
            SELECT t.scan_id AS scan, s.server_id AS server, s.taken_at AS at, t.alliance_tag AS tag
            FROM map_tile t JOIN map_scan s ON s.scan_id = t.scan_id
            WHERE t.alliance_id = @a ORDER BY s.taken_at DESC LIMIT 1;
            """, ("@a", allianceId)).FirstOrDefault();
        var anyStint = Q(c, "SELECT tag FROM alliance_stint WHERE alliance_id = @a ORDER BY last_seen DESC LIMIT 1;", ("@a", allianceId)).FirstOrDefault();
        if (at is null && anyStint is null) return new() { ["allianceId"] = allianceId, ["found"] = false };

        // Name and home from a player's newest profile, falling back to their newest power row.
        const string Name = "COALESCE((SELECT p.name FROM player_profile p WHERE p.uid = st.uid AND p.name <> '' ORDER BY p.taken_at DESC LIMIT 1), (SELECT w.name FROM power_sample w WHERE w.uid = st.uid ORDER BY w.taken_at DESC LIMIT 1), st.uid)";
        const string Home = "(SELECT p.home_server FROM player_profile p WHERE p.uid = st.uid ORDER BY p.taken_at DESC LIMIT 1)";

        // Current members: the players whose newest alliance sighting is this alliance. Anyone who
        // has since been seen elsewhere (or in no alliance) is a past member, not on the roster.
        var members = Q(c, $"""
            SELECT st.uid, {Name} AS name,
                   COALESCE((SELECT p.level FROM player_profile p WHERE p.uid = st.uid AND p.level > 0 ORDER BY p.taken_at DESC LIMIT 1),
                            (SELECT t.hq_level FROM map_tile t WHERE t.owner_uid = st.uid ORDER BY t.scan_id DESC LIMIT 1)) AS hq,
                   {F(LatestPower, "st.uid")} AS power,
                   {F(PreviousPower, "st.uid")} AS prev,
                   {Active7("st.uid", Db.NowIso)} AS active7,
                   {Compared7("st.uid", Db.NowIso)} AS compared7,
                   {LastRise("st.uid")} AS "lastRise", {LastSeen("st.uid")} AS "lastSeen",
                   {F(LatestHero, "st.uid")} AS "heroPower",
                   (SELECT pl.hero FROM player_latest pl WHERE pl.uid = st.uid) AS "heroEst",
                   {Origin("st.uid")} AS origin, {Home} AS home,
                   st.first_seen AS since, st.joined_known AS "joinedKnown"
            FROM alliance_stint st WHERE st.alliance_id = @a AND st.left_at = ''
            ORDER BY power DESC NULLS LAST;
            """, ("@a", allianceId));

        // Past members: every closed stint - when they joined (if we saw it), when they left, and
        // where they went next.
        var past = Q(c, $"""
            SELECT st.uid, {Name} AS name, {Home} AS home, st.first_seen AS since, st.joined_known AS "joinedKnown",
                   st.last_seen AS "lastSeen", st.left_at AS "leftAt", st.next_tag AS "nextTag", st.next_alliance_id AS "nextAllianceId",
                   {F(LatestPower, "st.uid")} AS power
            FROM alliance_stint st WHERE st.alliance_id = @a AND st.left_at <> ''
              -- someone who left and came back is a current member; show only their older stints if still gone
              AND NOT EXISTS (SELECT 1 FROM alliance_stint cur WHERE cur.uid = st.uid AND cur.alliance_id = @a AND cur.left_at = '')
            ORDER BY st.left_at DESC;
            """, ("@a", allianceId));

        // The alliance's server: where most current members are from; the map's otherwise.
        var server = members.Select(m => m["home"]).Where(h => h is not null).GroupBy(h => Convert.ToInt32(h))
            .OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault()
            ?? (at is null ? 0 : Convert.ToInt32(at["server"]));
        var tag = (anyStint?["tag"] as string) is { Length: > 0 } t0 ? t0 : at?["tag"] as string ?? "";
        var history = Q(c, """
            SELECT s.taken_at AS at, s.server_id AS server, COUNT(*) AS members
            FROM map_tile t JOIN map_scan s ON s.scan_id = t.scan_id
            WHERE t.alliance_id = @a GROUP BY s.scan_id, s.taken_at, s.server_id ORDER BY s.taken_at;
            """, ("@a", allianceId));

        var voices = tag.Length == 0 ? [] : Q(c, $"""
            SELECT sender_uid AS uid, MAX(sender_name) AS name, COUNT(*) AS messages, MAX(sent_at) AS last
            FROM chat_message WHERE sender_al = @t AND sender_uid <> '' AND {ChatStore.InUse}
            GROUP BY sender_uid ORDER BY messages DESC LIMIT 15;
            """, ("@t", tag));
        var applicants = tag.Length == 0 ? [] : Q(c, """
            SELECT * FROM (
                SELECT DISTINCT ON (uid, to_server) uid, name, from_server AS "fromServer", to_server AS "toServer", power,
                       hero_power AS "heroPower", captured_at AS at
                FROM migration_applicant WHERE abbr = @t ORDER BY uid, to_server, captured_at DESC) newest
            ORDER BY power DESC;
            """, ("@t", tag));

        long Sum(string k) => members.Sum(m => m[k] is null ? 0 : Convert.ToInt64(m[k]));
        bool Is(Dictionary<string, object?> m, string k) => m[k] is not null && Convert.ToInt64(m[k]) == 1;
        return new()
        {
            ["found"] = true,
            ["allianceId"] = allianceId,
            ["tag"] = tag,
            ["nameChanges"] = new NameHistory(path).For("alliance", allianceId),
            ["server"] = server,
            ["seen"] = at?["at"],
            ["memberCount"] = members.Count,
            ["totalPower"] = Sum("power"),
            ["totalHeroPower"] = Sum("heroPower"),
            ["heroTotal"] = Sum("heroEst"),
            ["measured"] = members.Count(m => Is(m, "compared7")),
            ["active"] = members.Count(m => Is(m, "active7")),
            ["windowDays"] = PlayerActivity.WindowDays,
            ["migrants"] = members.Count(m => !Equals(Convert.ToInt32(m["origin"] ?? 0), server)),
            ["members"] = members,
            ["history"] = history,
            ["past"] = past,
            ["voices"] = voices,
            ["applicants"] = applicants,
        };
    }
}
