using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LastWar.Storage;

public sealed partial class Insights
{
    static readonly Regex StickerRx = new(@"^<lwSticker:(\d+):(\d*):?>$", RegexOptions.Compiled);

    /// <summary>
    /// Chat across every stored room, for the chat Stats tab: volume per room and per day, the busiest
    /// hours (UTC), the chattiest players and alliances, and emoji use. Three kinds of emoji are told
    /// apart: the game's own (Private Use Area characters, drawn with lwemoji.ttf), ordinary Unicode
    /// emoji, and animated stickers (a whole message of &lt;lwSticker:id:param:&gt;; sticker 5 is a dice
    /// roll whose param is the number thrown). days = 0 means everything held.
    /// Rooms are captured over different spans, so each room also carries a per-day rate.
    /// </summary>
    public Dictionary<string, object?> ChatStats(int days = 30)
    {
        using var c = Open();
        var since = days > 0 ? DateTimeOffset.UtcNow.AddDays(-days).ToString("O") : "";
        var rows = Q(c, $"""
            SELECT room_id AS room, sender_uid AS uid, sender_name AS name, sender_al AS tag, msg, sent_at AS at
            FROM chat_message WHERE sent_at >= @since AND {ChatStore.InUse} ORDER BY sent_at;
            """, ("@since", since));

        var rooms = new Dictionary<string, RoomAcc>();
        var people = new Dictionary<string, PersonAcc>();
        var alliances = new Dictionary<(string Tag, int? Server), int>();
        var daily = new SortedDictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var hours = new int[24];
        var gameEmoji = new Dictionary<string, int>();
        var unicodeEmoji = new Dictionary<string, int>();
        var stickers = new Dictionary<string, int>();
        var dice = new int[7];
        long gameTotal = 0, unicodeTotal = 0, stickerTotal = 0;

        foreach (var r in rows)
        {
            var room = (string)r["room"]!;
            var uid = r["uid"] as string ?? "";
            var msg = r["msg"] as string ?? "";
            if (!DateTimeOffset.TryParse(r["at"] as string, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) continue;
            at = at.ToUniversalTime();
            var day = at.ToString("yyyy-MM-dd");

            var ra = rooms.TryGetValue(room, out var x) ? x : rooms[room] = new RoomAcc(room, day);
            ra.Messages++; ra.Last = day; ra.Senders.Add(uid);
            if (!daily.TryGetValue(day, out var d)) daily[day] = d = [];
            d[room] = d.GetValueOrDefault(room) + 1;
            hours[at.Hour]++;

            var pa = people.TryGetValue(uid, out var p) ? p : people[uid] = new PersonAcc(uid);
            pa.Messages++; pa.Name = r["name"] as string ?? pa.Name; pa.Tag = r["tag"] as string ?? pa.Tag; pa.Rooms[room] = pa.Rooms.GetValueOrDefault(room) + 1;
            if (pa.Tag.Length > 0) alliances[(pa.Tag, ServerOfRoom(room))] = alliances.GetValueOrDefault((pa.Tag, ServerOfRoom(room))) + 1;

            if (StickerRx.Match(msg.Trim()) is { Success: true } m)
            {
                var id = m.Groups[1].Value;
                stickers[id] = stickers.GetValueOrDefault(id) + 1;
                stickerTotal++; pa.Stickers++;
                if (id == "5" && int.TryParse(m.Groups[2].Value, out var roll) && roll is >= 1 and <= 6) dice[roll]++;
                continue;
            }
            var runes = msg.EnumerateRunes().ToList();
            for (var i = 0; i < runes.Count; i++)
            {
                var rune = runes[i];
                if (rune.Value is >= 0xE000 and <= 0xF8FF)
                {
                    var s = rune.ToString();
                    gameEmoji[s] = gameEmoji.GetValueOrDefault(s) + 1;
                    gameTotal++; pa.GameEmoji++;
                    continue;
                }
                // A flag is two regional-indicator letters (F+R = France); a lone letter is not an emoji.
                // Skin tones (U+1F3FB..1F3FF) only modify the emoji before them.
                string? e = null;
                if (rune.Value is >= 0x1F1E6 and <= 0x1F1FF)
                {
                    if (i + 1 < runes.Count && runes[i + 1].Value is >= 0x1F1E6 and <= 0x1F1FF) e = rune.ToString() + runes[++i];
                }
                else if (rune.Value is < 0x1F3FB or > 0x1F3FF && IsUnicodeEmoji(rune)) e = rune.ToString();
                if (e is null) continue;
                unicodeEmoji[e] = unicodeEmoji.GetValueOrDefault(e) + 1;
                unicodeTotal++; pa.UnicodeEmoji++;
            }
        }

        static List<Dictionary<string, object?>> Top(Dictionary<string, int> counts, int n) =>
            counts.OrderByDescending(k => k.Value).Take(n)
                .Select(k => new Dictionary<string, object?> { ["key"] = k.Key, ["count"] = k.Value }).ToList();
        Dictionary<string, object?> Person(PersonAcc p) => new()
        {
            ["uid"] = p.Uid, ["name"] = p.Name, ["tag"] = p.Tag, ["room"] = p.Room, ["server"] = ServerOfRoom(p.Room),
            ["messages"] = p.Messages, ["gameEmoji"] = p.GameEmoji, ["unicodeEmoji"] = p.UnicodeEmoji, ["stickers"] = p.Stickers,
            ["emoji"] = p.GameEmoji + p.UnicodeEmoji + p.Stickers,
        };
        var rolls = dice.Skip(1).Sum();
        // "system" posts gift notices; it is chat volume but not a person.
        bool Real(PersonAcc p) => p.Uid.Length > 0 && p.Uid != "system";
        var talkers = people.Values.Where(Real).OrderByDescending(p => p.Messages).Take(50).ToList();
        var emojiUsers = people.Values.Where(p => Real(p) && p.GameEmoji + p.UnicodeEmoji + p.Stickers > 0)
            .OrderByDescending(p => p.GameEmoji + p.UnicodeEmoji + p.Stickers).Take(30).ToList();
        // Many chat rows carry no name or tag; the newest known ones fill the gaps.
        foreach (var p in talkers.Concat(emojiUsers).Where(p => p.Name.Length == 0 || p.Tag.Length == 0).Distinct())
            if (Q(c, "SELECT name, tag FROM player_latest WHERE uid = @u;", ("@u", p.Uid)).FirstOrDefault() is { } known)
            {
                if (p.Name.Length == 0) p.Name = known["name"] as string ?? "";
                if (p.Tag.Length == 0) p.Tag = known["tag"] as string ?? "";
            }

        return new()
        {
            ["days"] = days,
            ["from"] = rows.FirstOrDefault()?["at"],
            ["to"] = rows.LastOrDefault()?["at"],
            ["totals"] = new Dictionary<string, object?>
            {
                ["messages"] = rows.Count, ["senders"] = people.Count, ["rooms"] = rooms.Count,
                ["gameEmoji"] = gameTotal, ["unicodeEmoji"] = unicodeTotal, ["stickers"] = stickerTotal,
                ["diceRolls"] = rolls, ["diceAverage"] = rolls > 0 ? Math.Round(dice.Select((n, i) => (double)n * i).Sum() / rolls, 2) : null,
            },
            ["rooms"] = rooms.Values.OrderByDescending(a => a.Messages).Select(a =>
            {
                var span = Math.Max(1, (DateTime.Parse(a.Last, CultureInfo.InvariantCulture) - DateTime.Parse(a.First, CultureInfo.InvariantCulture)).Days + 1);
                return new Dictionary<string, object?>
                {
                    ["room"] = a.Room, ["server"] = ServerOfRoom(a.Room), ["messages"] = a.Messages, ["senders"] = a.Senders.Count,
                    ["first"] = a.First, ["last"] = a.Last, ["daysHeld"] = span, ["perDay"] = Math.Round((double)a.Messages / span, 1),
                };
            }).ToList(),
            ["daily"] = daily.Select(k => new Dictionary<string, object?> { ["day"] = k.Key, ["total"] = k.Value.Values.Sum(), ["rooms"] = k.Value }).ToList(),
            ["hours"] = hours,
            ["talkers"] = talkers.Select(Person).ToList(),
            ["emojiUsers"] = emojiUsers.Select(Person).ToList(),
            ["alliances"] = alliances.OrderByDescending(k => k.Value).Take(25).Select(k => new Dictionary<string, object?>
                { ["tag"] = k.Key.Tag, ["server"] = k.Key.Server, ["messages"] = k.Value }).ToList(),
            ["gameEmoji"] = Top(gameEmoji, 40),
            ["unicodeEmoji"] = Top(unicodeEmoji, 25),
            ["stickers"] = Top(stickers, 25),
            ["dice"] = dice.Skip(1).ToArray(),
        };
    }

    /// <summary>country_1967 -> 1967; other rooms (season, custom) belong to no one server.</summary>
    static int? ServerOfRoom(string room) =>
        room.StartsWith("country_", StringComparison.Ordinal) && int.TryParse(room[8..], out var s) ? s : null;

    // ponytail: block ranges, not the full Unicode emoji property list - covers pictographs, symbols
    // and dingbats; add ranges if a common emoji turns up uncounted.
    static bool IsUnicodeEmoji(Rune r) => r.Value is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or >= 0x1F000 and <= 0x1F2FF;

    sealed class RoomAcc(string room, string first)
    {
        public string Room = room, First = first, Last = first;
        public int Messages;
        public HashSet<string> Senders = [];
    }

    sealed class PersonAcc(string uid)
    {
        public string Uid = uid, Name = "", Tag = "";
        public Dictionary<string, int> Rooms = [];
        public string Room => Rooms.MaxBy(k => k.Value).Key;   // the room they talk in most
        public int Messages, GameEmoji, UnicodeEmoji, Stickers;
    }
}
