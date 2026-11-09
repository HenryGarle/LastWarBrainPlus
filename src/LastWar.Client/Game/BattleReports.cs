using LastWar.Protocol.Sfs;

namespace LastWar.Client.Game;

/// <summary>
/// Battle reports people share into chat (post 4, attachment {reportUid, toUser, mailType, resultState}).
/// Opening one is what the chat UI does (MailReportShare -> MailDataManager.OpenShareMail): mail.read
/// {uid = reportUid, type = "", toUser = the report's owner}. The reply is only {success}; the mail
/// itself arrives as the push push.mail.read, so this listens for it.
/// </summary>
public static class BattleReports
{
    /// <summary>
    /// A report address ("aws://reportFrame/bf/1420011599860817799.bin") as the game resolves it
    /// (apk ConstURLConfig: BattleReportAddressConfigDic / BattleReportAddressSubMap).
    /// </summary>
    public static string? Url(string address) =>
        address.StartsWith("aws://") ? "https://lastwar-fight-report-s3.akamaized.net/" + address["aws://".Length..]
        : address.StartsWith("ali://") ? "https://lastwar-fight-report.akamaized.net/" + address["ali://".Length..]
        : null;

    public static LastWar.Client.Proto.LwBattleReport Decode(byte[] bytes) => LastWar.Client.Proto.LwBattleReport.Parser.ParseFrom(bytes);

    static readonly Google.Protobuf.JsonFormatter Json = new(Google.Protobuf.JsonFormatter.Settings.Default.WithFormatDefaultValues(false));
    public static string ToJson(Google.Protobuf.IMessage m) => Json.Format(m);

    /// <summary>
    /// The whole report behind a shared mail. The mail's battleContent is a base64 LwBattleReport that
    /// usually holds only its uuid and the address of the full report (a public CDN file, the one the
    /// game downloads); a report with no address is already whole.
    /// </summary>
    public static async Task<(LastWar.Client.Proto.LwBattleReport Report, byte[] Bytes)> FullAsync(string battleContentBase64, HttpClient http, CancellationToken ct = default)
    {
        var head = Convert.FromBase64String(battleContentBase64);
        var shell = Decode(head);
        if (shell.Address is not { Length: > 0 } a || Url(a) is not { } url) return (shell, head);
        var bytes = await http.GetByteArrayAsync(url, ct);
        return (Decode(bytes), bytes);
    }

    /// <summary>
    /// Every fight in a report, both sides. A combined report (a rally, a garrison, several squads) holds
    /// its fights in round[].battle[] - one squad against one squad each; a single fight is the report
    /// itself. player[0] attacks, player[1] defends; fightResult 1 = the attacker won (seen on the shared
    /// reports: the loser's troops are wiped). The catalog adds troop types and roles from the game tables.
    /// </summary>
    public static List<LastWar.Core.Model.FightSide> Fights(LastWar.Client.Proto.LwBattleReport r, LastWar.Core.Model.HeroCatalog? cat = null)
    {
        cat ??= LastWar.Core.Model.HeroCatalog.Empty;
        var fights = r.Round.Count > 0 ? r.Round.SelectMany(x => x.Battle).ToList() : [r];
        var outp = new List<LastWar.Core.Model.FightSide>();
        for (var i = 0; i < fights.Count; i++)
        {
            var f = fights[i];
            var sides = new List<LastWar.Core.Model.FightSide>();
            for (var side = 0; side < Math.Min(2, f.Player.Count); side++)
            {
                var p = f.Player[side];
                var heroes = f.Units.Where(u => SideOf(u) == side).Select(u =>
                {
                    var (type, job) = cat.Heroes.TryGetValue(u.HeroId, out var h) ? h : (0, 0);
                    return new LastWar.Core.Model.FightHero(
                        u.HeroId, u.UnitType, u.HeroLevel, u.RankLv, u.AwakenLv, u.WeaponLevel,
                        u.EquipInfos.Sum(e => e.EquipLv), u.EquipInfos.Sum(e => e.Promote), u.SkillInfos.Sum(s => s.SkillLv),
                        u.MaxSoldierCount, u.SoldierCount, u.Stat?.Damage ?? 0, u.Stat?.Injured ?? 0,
                        u.UnitType == 2 ? 0 : type, job, u.UnitType != 2 && IsFront(u),
                        u.UnitType == 2 ? [.. u.SkillChipInfos.Select(c => c.CfgId)] : null, u.HpBeforeStart);
                }).ToList();
                var squad = heroes.Where(h => h.Unit != 2).ToList();
                var drone = f.Units.FirstOrDefault(u => u.UnitType == 2 && SideOf(u) == side);
                var factors = new Dictionary<string, double>
                {
                    ["heroPower"] = p.TotalHeroPower, ["armyStartPower"] = p.ArmyStartPower,
                    ["troopPower"] = p.SoldierPowerBeforeStart, ["troops"] = p.SoldierCountBeforeStart,
                    ["chipLevel"] = p.ChipTotalLevel, ["weaponStrengthLevel"] = p.WeaponStrengTotalLv,
                    ["heroLevelAvg"] = squad.Count > 0 ? squad.Average(h => h.Level) : 0,
                    ["heroRankAvg"] = squad.Count > 0 ? squad.Average(h => h.Rank) : 0,
                    ["heroEquipLevels"] = squad.Sum(h => h.EquipLevels), ["heroEquipPromote"] = squad.Sum(h => h.EquipPromote),
                    ["heroSkillLevels"] = squad.Sum(h => h.SkillLevels), ["heroWeaponLevels"] = squad.Sum(h => h.WeaponLevel),
                    ["heroAwaken"] = squad.Sum(h => h.Awaken),
                    ["droneLevel"] = drone?.HeroLevel ?? 0,
                    ["injuredRate"] = p.InjuredRate,
                };
                // Line-up (game rules: camp_effect, skill_desc_kezhi, uav_chip_skill_*).
                if (squad.Any(h => h.Type > 0))
                {
                    var main = squad.Where(h => h.Type > 0).GroupBy(h => h.Type).OrderByDescending(g => g.Count()).First();
                    factors["mainType"] = main.Key;   // categorical: shown, not compared
                    factors["formationBonus"] = LastWar.Core.Model.HeroCatalog.FormationBonus([.. squad.Select(h => h.Type)]);
                    factors["sameTypeHeroes"] = main.Count();
                    var front = squad.Where(h => h.Front).ToList();
                    var totalHp = squad.Sum(h => (double)h.Hp);
                    factors["frontDamageDealers"] = front.Count(h => h.Job == 2);
                    factors["frontGuardians"] = front.Count(h => h.Job == 1);
                    factors["frontHpShare"] = totalHp > 0 ? Math.Round(front.Sum(h => (double)h.Hp) / totalHp, 3) : 0;
                    if (drone is not null)
                    {
                        var chips = drone.SkillChipInfos.Select(c => (c, info: cat.Chips.TryGetValue(c.CfgId, out var x) ? x : (0, 0, 0))).ToList();
                        factors["droneChipStars"] = drone.SkillChipInfos.Sum(c => c.Star);
                        factors["droneChipQuality"] = chips.Sum(c => c.info.Item3);
                        factors["droneChipsMatchingType"] = chips.Count(c => c.info.Item1 == main.Key);
                        // The shield chip (skill type 1) shields front-row heroes of ITS type (or all, for a squad chip).
                        var shield = chips.FirstOrDefault(c => c.info.Item2 == 1);
                        if (shield.c is not null) factors["frontShieldedByChip"] = front.Count(h => shield.info.Item1 == 0 || h.Type == shield.info.Item1);
                    }
                }
                if (p.Progress is { } pr)
                {
                    factors["formationEquipPower"] = pr.FormationEquipPower;
                    factors["sciencePower"] = pr.SciencePower;
                    factors["decoPower"] = pr.DecoPower;
                    factors["honorPower"] = pr.HonorPower;
                    foreach (var s in pr.Science) factors[$"science{s.ScienceTabId}"] = s.Progress;
                }
                // Down to the item: which gear sits on whom, each drone chip, every stat and bonus.
                var worn = f.Units.Where(u => SideOf(u) == side && u.UnitType != 2).Zip(squad).ToList();
                foreach (var slot in new[] { 1, 2, 3, 4 })
                {
                    var on = worn.Select(x => (h: x.Second, e: x.First.EquipInfos.FirstOrDefault(e => GearSlot(e.EquipId) == slot))).Where(x => x.e is not null).ToList();
                    if (on.Count == 0) continue;
                    var g = GearSlotName(slot);
                    factors[$"gear{g}Quality"] = on.Average(x => GearQuality(x.e!.EquipId));
                    foreach (var (role, pick) in Roles)
                        if (on.Where(x => pick(x.h)).ToList() is { Count: > 0 } w) factors[$"gear{g}Lv.{role}"] = w.Average(x => x.e!.EquipLv + x.e.Promote);
                }
                if (drone is not null)
                    foreach (var c in drone.SkillChipInfos)
                        if (cat.Chips.TryGetValue(c.CfgId, out var ci) && ChipKind(ci.SkillType) is { } kind)
                        {
                            factors[$"chip{kind}Lv"] = c.Lv;
                            factors[$"chip{kind}Star"] = c.Star;
                            factors[$"chip{kind}Quality"] = ci.Quality;
                        }
                // Every stat the game resolved for each hero (unit.effects, ~150 per hero): averaged over the squad.
                foreach (var e in worn.SelectMany(x => x.First.Effects).GroupBy(e => e.EffectId))
                    factors[$"eff{e.Key}"] = Math.Round(e.Average(x => (double)x.Value), 4);
                // The player's own bonuses by source (research, decorations, skins, other) - summed per effect.
                if (p.Progress is { } pp)
                {
                    var bonus = pp.ScienceEffects.Concat(pp.DecoEffects).Concat(pp.ExtraEffects).Concat(pp.OtherEffects.SelectMany(t => t.Effects))
                        .Concat(pp.SkinProgress?.SkinEffects ?? []);
                    foreach (var e in bonus.GroupBy(e => e.Id)) factors[$"bonus{e.Key}"] = Math.Round(e.Sum(x => (double)x.Val), 4);
                }
                foreach (var (k, v) in p.PowerTabMap) factors[$"powerTab{k}"] = v;
                foreach (var e in p.ExtraPowers) factors[$"extraPower{e.ViewType}"] = e.Value;
                var outcome = new Dictionary<string, double>
                {
                    ["damage"] = heroes.Sum(h => h.Damage), ["damageTaken"] = heroes.Sum(h => h.Injured),
                    ["troopsLost"] = p.SoldierCountBeforeStart - p.SoldierCount,
                    ["troopsLeftShare"] = p.SoldierCountBeforeStart > 0 ? (double)p.SoldierCount / p.SoldierCountBeforeStart : 0,
                };
                sides.Add(new(i, side, (f.FightResult == 1) == (side == 0), p.User?.Uid ?? "", p.User?.Name ?? "",
                    p.User?.AllianceInfo?.AlAbbr ?? "", p.User?.SrcServerId ?? 0, factors, outcome, heroes));
            }
            // Type counters need both sides: +1 my main type counters theirs (they deal 20% less to me), -1 the reverse.
            if (sides.Count == 2 && sides[0].Factors.TryGetValue("mainType", out var t0) && sides[1].Factors.TryGetValue("mainType", out var t1))
                foreach (var (me, them) in new[] { (sides[0], (int)t1), (sides[1], (int)t0) })
                {
                    var mine = (int)me.Factors["mainType"];
                    me.Factors["typeAdvantage"] = LastWar.Core.Model.HeroCatalog.Counters(mine) == them ? 1 : LastWar.Core.Model.HeroCatalog.Counters(them) == mine ? -1 : 0;
                    me.Factors["heroesCounteringThem"] = me.Heroes.Count(h => h.Unit != 2 && LastWar.Core.Model.HeroCatalog.Counters(h.Type) == them);
                }
            outp.AddRange(sides);
        }
        return outp;
    }

    // Hero gear ids carry their slot and quality (lw_equip, all 84 rows checked 2026-10-01): 410400 = slot 4
    // (radar), quality 5 (id / 100000 + 1). Slots: 1 weapon (cannon/railgun), 2 armor, 3 data chip, 4 radar.
    public static int GearSlot(int equipId) => equipId / 100 % 10;
    public static int GearQuality(int equipId) => equipId / 100000 + 1;
    public static string GearSlotName(int slot) => slot switch { 1 => "Weapon", 2 => "Armor", 3 => "Chip", 4 => "Radar", _ => $"Slot{slot}" };
    // lw_drone_skillchip skill_type: 1 front-row shield, 2 damage, 3 interference, 4 defense.
    static string? ChipKind(int skillType) => skillType switch { 1 => "Shield", 2 => "Damage", 3 => "Interference", 4 => "Defense", _ => null };
    static readonly (string Role, Func<LastWar.Core.Model.FightHero, bool> Pick)[] Roles =
        [("dd", h => h.Job == 2), ("guard", h => h.Job == 1), ("front", h => h.Front), ("back", h => !h.Front)];

    // Front row = slots 1-2 (attacker) and 6-7 (defender): the drone's front-row shield chip hits exactly
    // those in the round details (aws://report/... per fight, detail.actions), 2026-10-01.
    static bool IsFront(LastWar.Client.Proto.LwBattleUnit u) => u.Index is 1 or 2 or 6 or 7;
    // ponytail: unit index 1-5 attack, 6-10 defend, drones 11 (attack) / 12 (defend) - true of every 1 v 1
    // report seen. Rallies with several players per side may number differently; check when one arrives.
    static int SideOf(LastWar.Client.Proto.LwBattleUnit u) => u.UnitType == 2 ? (u.Index % 2 == 1 ? 0 : 1) : u.Index <= 5 ? 0 : 1;

    public static async Task<SfsObject?> FetchSharedAsync(LastWarClient client, string reportUid, string toUser, CancellationToken ct = default)
    {
        var got = new TaskCompletionSource<SfsObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Seen(FrameRecord f) { if (!f.Outbound && f.Command == "push.mail.read") got.TrySetResult(f.Params); }
        client.Connection.Frame += Seen;
        try
        {
            var ps = new SfsObject();
            ps.PutUtfString("uid", reportUid);
            ps.PutUtfString("type", "");
            ps.PutUtfString("toUser", toUser);
            await client.TryCallAsync("mail.read", ps, TimeSpan.FromSeconds(10), ct);
            return await got.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
        catch (TimeoutException) { return null; }
        finally { client.Connection.Frame -= Seen; }
    }
}
