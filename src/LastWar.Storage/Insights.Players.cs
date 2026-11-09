namespace LastWar.Storage;

public sealed partial class Insights
{
    /// <summary>
    /// Every player on a server's newest map, with power, the change against the snapshot before,
    /// hero power where they are on a board, and the server the account was created on. This is
    /// the list behind "who here came from somewhere else" - separate from the server page so the
    /// page itself stays quick.
    /// </summary>
    public List<Dictionary<string, object?>> ServerPlayers(int server)
    {
        using var c = Open();
        var scan = Q(c, "SELECT scan_id AS scan FROM map_scan WHERE server_id = @s ORDER BY taken_at DESC LIMIT 1;",
            ("@s", server)).FirstOrDefault()?["scan"];
        if (scan is null) return [];
        var snaps = FullSnapshots(c, server).OrderByDescending(s => (string)s["at"]!).Take(2).ToList();
        var snap = snaps.Count > 0 ? snaps[0]["at"]! : "";
        var prev = snaps.Count > 1 ? snaps[1]["at"]! : "";

        return Q(c, $"""
            SELECT t.owner_uid AS uid, t.name, t.alliance_tag AS tag, t.alliance_id AS "allianceId",
                   t.hq_level AS hq, p.power, q.power AS prev,
                   {Active7("t.owner_uid", "@snap")} AS active7, {LastRise("t.owner_uid")} AS "lastRise", {LastSeen("t.owner_uid")} AS "lastSeen",
                   {F(LatestHero, "t.owner_uid")} AS "heroPower", pl.hero AS "heroEst",
                   {Origin("t.owner_uid")} AS origin,
                   -- country = the profile country (where the account is from); flag = the flag they choose to show. Newest non-empty of each.
                   (SELECT pp.country FROM player_profile pp WHERE pp.uid = t.owner_uid AND pp.country <> '' ORDER BY pp.taken_at DESC LIMIT 1) AS country,
                   (SELECT pp.flag FROM player_profile pp WHERE pp.uid = t.owner_uid AND pp.flag <> '' ORDER BY pp.taken_at DESC LIMIT 1) AS flag
            FROM map_tile t
            LEFT JOIN player_latest pl ON pl.uid = t.owner_uid
            LEFT JOIN power_sample p ON p.server_id = @s AND p.taken_at = @snap AND p.uid = t.owner_uid
            LEFT JOIN power_sample q ON q.server_id = @s AND q.taken_at = @prev AND q.uid = t.owner_uid
            WHERE t.scan_id = @scan
            ORDER BY pl.hero DESC NULLS LAST;
            """, ("@s", server), ("@scan", scan), ("@snap", snap), ("@prev", prev));
    }
}
