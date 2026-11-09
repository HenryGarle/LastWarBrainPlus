namespace LastWar.Server;

/// <summary>
/// Starting points for the data explorer. Every one is a plain SELECT the user can edit
/// in place - the presets are a menu, not an API.
/// </summary>
public static class Presets
{
    public sealed record Preset(string Name, string Description, string Sql);

    public static readonly Preset[] All =
    [
        new("Scans", "Every map scan taken, newest first",
            "SELECT scan_id, server_id, taken_at, tile_count FROM map_scan ORDER BY taken_at DESC"),

        new("Players per server", "Tile count from each server's most recent scan",
            """
            SELECT DISTINCT ON (server_id) server_id, taken_at AS scanned, tile_count
            FROM map_scan ORDER BY server_id, taken_at DESC
            """),

        new("Alliances on 1967", "Alliance sizes in the latest scan of server 1967",
            """
            SELECT alliance_tag, alliance_id, COUNT(*) AS members
            FROM map_tile
            WHERE scan_id = (SELECT MAX(scan_id) FROM map_scan WHERE server_id = 1967)
              AND alliance_tag <> ''
            GROUP BY alliance_id, alliance_tag ORDER BY members DESC
            """),

        new("Power leaderboard", "Most recent power sample per account on 1967",
            """
            SELECT uid, name, alliance_tag, power, hq_level, taken_at
            FROM power_sample p
            WHERE server_id = 1967
              AND taken_at = (SELECT MAX(taken_at) FROM power_sample WHERE server_id = p.server_id AND uid = p.uid)
            ORDER BY power DESC
            """),

        new("Active accounts", "Power ROSE against the previous sample - the only move that proves a login",
            """
            WITH s AS (
              SELECT uid, name, alliance_tag, power, taken_at,
                     LAG(power)    OVER (PARTITION BY uid ORDER BY taken_at) AS prev,
                     LAG(taken_at) OVER (PARTITION BY uid ORDER BY taken_at) AS prev_at
              FROM power_sample WHERE server_id = 1967
            )
            SELECT uid, name, alliance_tag, prev, power, power - prev AS gain, prev_at, taken_at
            FROM s WHERE prev IS NOT NULL AND power > prev ORDER BY gain DESC
            """),

        new("Snapshot coverage", "How many power samples each snapshot holds",
            """
            SELECT taken_at, server_id, COUNT(*) AS accounts, SUM(power) AS total_power
            FROM power_sample GROUP BY taken_at, server_id ORDER BY taken_at DESC
            """),

        new("Chat rooms", "What has been captured, and the seq range held",
            """
            SELECT room_id, COUNT(*) AS messages, MIN(seq_id) AS oldest, MAX(seq_id) AS newest,
                   MIN(sent_at) AS first_at, MAX(sent_at) AS last_at
            FROM chat_message GROUP BY room_id ORDER BY messages DESC
            """),

        new("Loudest voices", "Who talks most in world chat",
            """
            SELECT MAX(sender_name) AS sender_name, MAX(sender_al) AS sender_al, sender_uid, COUNT(*) AS messages, MAX(sent_at) AS last_seen
            FROM chat_message WHERE room_id = 'country_1967' AND sender_uid <> ''
            GROUP BY sender_uid ORDER BY messages DESC
            """),

        new("Chat search", "Search over captured chat - edit the ILIKE pattern",
            """
            SELECT sent_at, room_id, sender_name, msg
            FROM chat_message WHERE msg ILIKE '%migration%' ORDER BY sent_at DESC
            """),

        new("Chat by hour", "Message volume per hour, for spotting when a server is awake",
            """
            SELECT substr(sent_at, 1, 13) AS hour, COUNT(*) AS messages
            FROM chat_message WHERE room_id = 'country_1967'
            GROUP BY hour ORDER BY hour DESC
            """),
    ];
}
