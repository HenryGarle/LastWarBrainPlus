using System.Text.Json;
using System.Text.RegularExpressions;

namespace LastWar.Storage;

/// <summary>
/// The game's own words, from what assets.extract pulled out of the APK (data/assets): UI strings
/// by dialog id, item names, kingdom position titles, mail and reward type names. Loaded once,
/// lazily; everything answers with a readable fallback when the assets are missing.
/// </summary>
public sealed partial class GameText(string assetsDir)
{
    readonly Lazy<Dictionary<string, string>> _strings = new(() => Load<Dictionary<string, string>>(Path.Combine(assetsDir, "locale", "en.json")) ?? []);
    readonly Lazy<Dictionary<long, JsonElement>> _goods = new(() => Rows(assetsDir, "goods"));
    readonly Lazy<Dictionary<long, JsonElement>> _government = new(() => Rows(assetsDir, "government"));
    readonly Lazy<Dictionary<long, JsonElement>> _mail = new(() => Rows(assetsDir, "mail"));
    readonly Lazy<Dictionary<long, JsonElement>> _stickers = new(() => Rows(assetsDir, "sticker"));
    readonly Lazy<Dictionary<long, JsonElement>> _heroes = new(() => Rows(assetsDir, "lw_hero"));
    readonly Lazy<Dictionary<int, string>> _mailTypes = new(() => Enum(assetsDir, "mail_types"));
    readonly Lazy<Dictionary<int, string>> _rewardTypes = new(() => Enum(assetsDir, "reward_types"));

    public bool Available => _strings.Value.Count > 0;

    /// <summary>A dialog id's text with its {0}.. placeholders filled; the id itself when unknown.</summary>
    public string T(string? id, IReadOnlyList<string>? args = null)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (!_strings.Value.TryGetValue(id, out var s)) return id;
        s = Markup().Replace(s, "");   // <color=..>, </color>, <b> - Unity rich text
        if (args is { Count: > 0 })
            s = Placeholder().Replace(s, m => int.TryParse(m.Groups[1].Value, out var i) && i < args.Count ? args[i] : m.Value);
        return s;
    }

    public string ItemName(long goodsId) =>
        _goods.Value.TryGetValue(goodsId, out var g) && Str(g, "name") is { Length: > 0 } n ? T(n)
        // Unnamed type-110 goods (600001-3) are alliance contribution packs worth para2 (their text: "Gain alliance contributions upon use").
        : g.ValueKind == JsonValueKind.Object && Str(g, "type") == "110" ? $"{T("390261")} {Str(g, "para2")}"
        // Unnamed type-2 goods are speed-ups: para3 = seconds, type2 = which queue (its description says; 2026-10-30).
        : g.ValueKind == JsonValueKind.Object && Str(g, "type") == "2" && long.TryParse(Str(g, "para3"), out var secs) && secs > 0
            ? $"{Duration(secs)} {Str(g, "type2") switch { "1" => "Universal", "3" => "Training", "4" => "Healing", "6" => "Research", "7" => "Construction", var x => $"type {x}" }} Speedup"
        : T(goodsId.ToString()) is var t && t != goodsId.ToString() ? t : $"item {goodsId}"; // most goods rows have no name column: the id is the dialog key

    static string Duration(long s) => s % 86400 == 0 ? $"{s / 86400}d" : s % 3600 == 0 ? $"{s / 3600}h" : s % 60 == 0 ? $"{s / 60}m" : $"{s}s";

    public string PositionName(string positionId) =>
        long.TryParse(positionId, out var id) && _government.Value.TryGetValue(id, out var g) ? T(Str(g, "name")) : $"position {positionId}";

    public (string Title, string Message) MailTemplate(long mailId) =>
        _mail.Value.TryGetValue(mailId, out var m) ? (T(Str(m, "title")), T(Str(m, "message"))) : ("", "");

    public string StickerName(long id) =>
        _stickers.Value.TryGetValue(id, out var s) && Str(s, "sticker_name") is { Length: > 0 } n ? T(n) : $"sticker {id}";

    /// <summary>A hero's name (lw_hero first_name: "McGregor"); unit type 2 in a report is the drone.</summary>
    public string HeroName(long id) =>
        _heroes.Value.TryGetValue(id, out var h) && Str(h, "first_name") is { Length: > 0 } n ? T(n) : $"hero {id}";

    readonly Lazy<LastWar.Core.Model.HeroCatalog> _catalog = new(() =>
    {
        static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.TryParse(Str(e, k), out var n) ? n : 0;
        return new(
            Rows(assetsDir, "lw_hero").ToDictionary(r => (int)r.Key, r => (I(r.Value, "army_type"), I(r.Value, "army_job"))),
            Rows(assetsDir, "lw_drone_skillchip").ToDictionary(r => (int)r.Key, r => (I(r.Value, "heroType"), I(r.Value, "skill_type"), I(r.Value, "quality"))));
    });

    /// <summary>Troop type and role of every hero, and what every drone chip does - for reading battle reports.</summary>
    public LastWar.Core.Model.HeroCatalog Catalog() => _catalog.Value;

    readonly Lazy<Dictionary<long, JsonElement>> _chips = new(() => Rows(assetsDir, "lw_drone_skillchip"));
    public string ChipName(long id) => _chips.Value.TryGetValue(id, out var c) ? T(Str(c, "name")) : $"chip {id}";

    // ---- Battle report vocabulary: gear, skills, research tabs, stats and bonuses ----
    readonly Lazy<Dictionary<long, JsonElement>> _equip = new(() => Rows(assetsDir, "lw_equip"));
    readonly Lazy<Dictionary<long, JsonElement>> _squadEquip = new(() => Rows(assetsDir, "lw_squad_equip"));
    readonly Lazy<Dictionary<long, JsonElement>> _skills = new(() => Rows(assetsDir, "lw_hero_skill"));
    readonly Lazy<Dictionary<long, JsonElement>> _scienceTabs = new(() => Rows(assetsDir, "aps_science_tab"));
    readonly Lazy<Dictionary<long, JsonElement>> _effects = new(() => Rows(assetsDir, "lw_effect_number"));
    // Effects lw_effect_number leaves unnamed ("HP Boost while Garrisoning" = 75058) are named by the
    // report screens' own tables: lw_other_report_new (bonus breakdown), lw_technical_report, battle_card_report.
    readonly Lazy<Dictionary<long, string>> _effectAlt = new(() =>
        new[] { ("lw_other_report_new", "effectName"), ("lw_technical_report", "desc"), ("battle_card_report", "desc") }
            .SelectMany(t => Rows(assetsDir, t.Item1).Values.Select(r => (id: long.TryParse(Str(r, "effectId"), out var e) ? e : 0, key: Str(r, t.Item2))))
            .Where(x => x.id > 0 && x.key.Length > 0).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().key));

    string Named(Dictionary<long, JsonElement> rows, long id, string col, string fallback) =>
        rows.TryGetValue(id, out var r) && Str(r, col) is { Length: > 0 } k && T(k) is var t && t != k ? t : fallback;

    public string GearName(long id) => Named(_equip.Value, id, "name", $"gear {id}");
    public string SquadGearName(long id) => Named(_squadEquip.Value, id, "name", $"squad gear {id}");
    public string SkillName(long id) => Named(_skills.Value, id, "name", $"skill {id}");
    /// <summary>aps_science_tab: 3 Hero, 4 Units, 5-8 Squad 1-4, 9 Alliance Duel, 10 Siege to Seize, 11 Defense Fortifications...</summary>
    public string ScienceTabName(long id) => Named(_scienceTabs.Value, id, "name", $"research tab {id}");
    public string EffectName(long id) =>
        Named(_effects.Value, id, "name", "") is { Length: > 0 } n ? n
        : _effectAlt.Value.TryGetValue(id, out var k) && T(k) is var t && t != k ? t : $"effect {id}";
    /// <summary>The value is a fraction (0.05 = +5%): lw_effect_number type 1 or 2 (type 5 = flat unit stats).</summary>
    public bool EffectIsPercent(long id) => _effects.Value.TryGetValue(id, out var r) && Str(r, "type") is "1" or "2";

    /// <summary>
    /// Names for every id a decoded report mentions, keyed by kind, so the page can show "M5-A Predator Radar"
    /// rather than 410400. Walks the report JSON once: heroId, equipId (hero gear), progress.equipId (squad
    /// gear), skillId, cfgId (drone chips), effectId and {id, val} effects, scienceTabId.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ReportNames(JsonElement report)
    {
        var ids = new Dictionary<string, HashSet<long>>();
        void Add(string kind, JsonElement v) { if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) || v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out n)) (ids.TryGetValue(kind, out var s) ? s : ids[kind] = []).Add(n); }
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Walk(x); return; }
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("id", out var id) && e.TryGetProperty("val", out _)) Add("effect", id);
            foreach (var p in e.EnumerateObject())
                switch (p.Name)
                {
                    case "heroId": Add("hero", p.Value); break;
                    case "equipId" when p.Value.ValueKind == JsonValueKind.Array: foreach (var x in p.Value.EnumerateArray()) Add("squadGear", x); break;
                    case "equipId": Add("gear", p.Value); break;
                    case "skillId": Add("skill", p.Value); break;
                    case "cfgId": Add("chip", p.Value); break;
                    case "effectId": Add("effect", p.Value); break;
                    case "scienceTabId": Add("scienceTab", p.Value); break;
                    case "skillDamageMap" or "skillInjuredMap": foreach (var k in p.Value.EnumerateObject()) if (long.TryParse(k.Name, out var sk)) (ids.TryGetValue("skill", out var s) ? s : ids["skill"] = []).Add(sk); break;
                    default: Walk(p.Value); break;
                }
        }
        Walk(report);
        Func<long, string> Namer(string kind) => kind switch
        {
            "hero" => HeroName, "gear" => GearName, "squadGear" => SquadGearName, "skill" => SkillName,
            "chip" => ChipName, "effect" => EffectName, "scienceTab" => ScienceTabName, _ => i => i.ToString(),
        };
        var outp = ids.ToDictionary(k => k.Key, k => k.Value.ToDictionary(i => i.ToString(), Namer(k.Key)));
        outp["otherTab"] = OtherEffectTabs.ToDictionary(k => k.Key.ToString(), k => k.Value);
        outp["powerTab"] = PowerTabs.ToDictionary(k => k.Key.ToString(), k => k.Value);
        outp["extraPower"] = ExtraPowers.ToDictionary(k => k.Key.ToString(), k => k.Value);
        if (ids.TryGetValue("effect", out var eff)) outp["percent"] = eff.Where(EffectIsPercent).ToDictionary(i => i.ToString(), _ => "1");
        return outp;
    }

    /// <summary>Readable names for analysis factor keys built from ids (eff{id}, bonus{id}, science{tab}).</summary>
    public string? FactorName(string key) =>
        key.StartsWith("eff") && long.TryParse(key[3..], out var e) ? $"Hero stat: {EffectName(e)}"
        : key.StartsWith("bonus") && long.TryParse(key[5..], out var b) ? $"Bonus: {EffectName(b)}"
        : key.StartsWith("science") && long.TryParse(key[7..], out var s) ? $"Research: {ScienceTabName(s)} %"
        : key.StartsWith("powerTab") && int.TryParse(key[8..], out var t) ? PowerTabs.GetValueOrDefault(t, $"Power tab {t}")
        : key.StartsWith("extraPower") && int.TryParse(key[10..], out var x) ? ExtraPowers.GetValueOrDefault(x, $"Extra power {x}")
        : null;

    // The report's power breakdown (MailArmyPage / HeroOverview read powerTabMap[1..5]; Global EnumType
    // ExtraPowerInfoType 11-15 and NewOtherPowerInfoType 1-5 = otherEffects tabType, lw_other_report_new othertabtype).
    public static readonly Dictionary<int, string> PowerTabs = new() { [1] = "Power: heroes", [2] = "Power: units", [3] = "Power: squad gear", [4] = "Power: other", [5] = "Power: dominator" };
    public static readonly Dictionary<int, string> ExtraPowers = new() { [11] = "Power: research", [12] = "Power: alliance research", [13] = "Power: mastery", [14] = "Power: faction", [15] = "Power: battlefield" };
    public static readonly Dictionary<int, string> OtherEffectTabs = new() { [1] = "Alliance research", [2] = "Mastery", [3] = "Battlefield", [4] = "Faction research", [5] = "Military" };

    // lw_dispatch_tasks (Secret Tasks): color 3 SR / 4 SSR / 5 UR, level = stars (1-7), is_special = red stars
    // (never refreshed, best rewards), times = seconds to complete, steal_maxtimes = plunders allowed.
    readonly Lazy<Dictionary<long, JsonElement>> _dispatch = new(() => Rows(assetsDir, "lw_dispatch_tasks"));
    public (string Name, string Rarity, int Stars, bool Red, int Seconds, int MaxSteals) DispatchTask(long cfgId)
    {
        if (!_dispatch.Value.TryGetValue(cfgId, out var r)) return ($"task {cfgId}", "?", 0, false, 0, 3);
        static int I(JsonElement e, string k) => int.TryParse(Str(e, k), out var n) ? n : 0;
        var rarity = I(r, "color") switch { 3 => "SR", 4 => "SSR", 5 => "UR", var x => $"q{x}" };
        return (T(Str(r, "name")), rarity, I(r, "level"), I(r, "is_special") == 1, I(r, "times"), I(r, "steal_maxtimes") is > 0 and var m ? m : 3);
    }

    /// <summary>The raw lw_dispatch_tasks row (debugging the rank / rarity read).</summary>
    public string DispatchRow(long cfgId) => _dispatch.Value.TryGetValue(cfgId, out var r) ? r.GetRawText() : "";

    // train_property (trade trucks): quality 1-5, 10 = the top tier; HQLevel = the sender's HQ level band.
    readonly Lazy<Dictionary<long, JsonElement>> _trucks = new(() => Rows(assetsDir, "train_property"));
    public (int Quality, int HqLevel) TruckConfig(long cfgId) =>
        _trucks.Value.TryGetValue(cfgId, out var r) ? (int.TryParse(Str(r, "quality"), out var q) ? q : 0, int.TryParse(Str(r, "HQLevel"), out var h) ? h : 0) : (0, 0);

    public Dictionary<long, string> StickerNames() => _stickers.Value.Keys.ToDictionary(k => k, StickerName);

    /// <summary>MAIL_PRESIDENT_SEND -> "President send".</summary>
    public string MailTypeName(int type) => _mailTypes.Value.TryGetValue(type, out var n) ? Pretty(n) : $"type {type}";

    readonly Lazy<Dictionary<long, JsonElement>> _resourceItems = new(() => Rows(assetsDir, "aps_resource_item"));

    /// <summary>A reward line: "UR Hero Universal Shard x10", "Oil x5,000".</summary>
    public string Reward(int type, string id, long num)
    {
        var what = type == 7 && long.TryParse(id, out var gid) ? ItemName(gid)
            : type == 27 && long.TryParse(id, out var rid) && _resourceItems.Value.TryGetValue(rid, out var ri) && Str(ri, "name") is { Length: > 0 } rn ? T(rn)   // aps_resource_item: "Upgrade Ore", "Hero EXP"
            : _rewardTypes.Value.TryGetValue(type, out var n) ? Pretty(n) : $"reward {type}:{id}";
        return num > 1 ? $"{what} x{num:N0}" : what;
    }

    static string Pretty(string enumName)
    {
        var s = enumName.Replace("MAIL_", "").Replace("LW_", "").Replace('_', ' ').Trim().ToLowerInvariant();
        return s.Length == 0 ? enumName : char.ToUpperInvariant(s[0]) + s[1..];
    }

    static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";

    static T? Load<T>(string path) where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;

    static Dictionary<long, JsonElement> Rows(string dir, string table)
    {
        var path = Path.Combine(dir, "tables", $"{table}.json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray()
            .Where(r => r.TryGetProperty("id", out var id) && id.TryGetInt64(out _))
            .GroupBy(r => r.GetProperty("id").GetInt64()).ToDictionary(g => g.Key, g => g.First().Clone());
    }

    static Dictionary<int, string> Enum(string dir, string name) =>
        (Load<Dictionary<string, double>>(Path.Combine(dir, "tables", $"{name}.json")) ?? [])
            .GroupBy(kv => (int)kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    [GeneratedRegex(@"</?(color|b|i|size|link)[^>]*>")] private static partial Regex Markup();
    [GeneratedRegex(@"\{(\d+)\}")] private static partial Regex Placeholder();
}
