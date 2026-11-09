using LastWar.Client.Game;

namespace LastWar.Tests;

public class BattleReportTests
{
    static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // Two real reports Henry shared by DM (mail types 185 and 72), downloaded from the game's report CDN.
    [Fact]
    public void DecodesRealReports()
    {
        foreach (var f in new[] { "report_185.bin", "report_72.bin" })
        {
            var r = BattleReports.Decode(Fixture(f));
            Assert.NotEqual(0, r.Uuid);
            var json = BattleReports.ToJson(r);
            if (Environment.GetEnvironmentVariable("LASTWAR_DUMP") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, f + ".json"), json);
            Assert.True(r.Player.Count + r.CombinePlayer.Count > 0, f);
        }
    }

    // 185: Ghost (defender) wiped the attacker; 72: the attacker (SMoB) wiped Ghost. Five heroes and a drone a side.
    [Fact]
    public void FightsKnowTheWinnerAndTheSides()
    {
        var a = BattleReports.Fights(BattleReports.Decode(Fixture("report_185.bin")));
        Assert.Equal(2, a.Count);
        Assert.Equal("Ghosⱦ", a.Single(s => s.Won).Name);
        Assert.Equal(1, a.Single(s => s.Won).Side);
        Assert.All(a, s => Assert.Equal(6, s.Heroes.Count));
        Assert.Equal(2270, a[0].Factors["troops"]);
        Assert.Equal(a[0].Factors["troops"], a[0].Heroes.Where(h => h.Unit != 2).Sum(h => h.Troops));   // the side split adds up
        Assert.True(a[1].Factors["sciencePower"] > 0);

        var b = BattleReports.Fights(BattleReports.Decode(Fixture("report_72.bin")));
        Assert.Equal(0, b.Single(s => s.Won).Side);
        Assert.Equal("SMoB", b.Single(s => s.Won).Tag);
    }

    // Item-level detail: gear by slot and wearer, every resolved hero stat, the player's bonuses by effect.
    [Fact]
    public void FightsGoDownToGearStatsAndBonuses()
    {
        Assert.Equal((4, 5), (BattleReports.GearSlot(410400), BattleReports.GearQuality(410400)));   // M5-A "Predator" Radar
        var s = BattleReports.Fights(BattleReports.Decode(Fixture("report_185.bin")));
        Assert.All(s, x => Assert.Contains(x.Factors.Keys, k => k.StartsWith("gearRadarLv.")));
        Assert.All(s, x => Assert.True(x.Factors["gearRadarQuality"] >= 1));
        Assert.All(s, x => Assert.Contains("eff50014", x.Factors.Keys));   // max HP, resolved per hero
        Assert.Contains(s, x => x.Factors.Keys.Any(k => k.StartsWith("bonus")));
    }

    // Stored reports come back with their sides, and the analysis pairs each fight's winner with its loser.
    [Fact]
    public void StoresReportsAndComparesWinnersWithLosers()
    {
        var path = TestDb.Create();
        try
        {
            var store = new LastWar.Storage.BattleReportStore(path);
            var at = DateTimeOffset.UtcNow;
            Assert.Equal(2, store.AddShares([
                new("r185", "1001690072001967", "custom_1_2_v2", 7, "1001690072001967", "Ghost", at, 185, 0, "lastwar_1"),
                new("r72", "1001690072001967", "custom_1_2_v2", 12, "1001690072001967", "Ghost", at, 72, 1, "lastwar_1"),
            ]));
            Assert.Equal(2, store.Pending().Count);
            foreach (var (uid, file) in new[] { ("r185", "report_185.bin"), ("r72", "report_72.bin") })
            {
                var bytes = Fixture(file);
                var r = BattleReports.Decode(bytes);
                store.SaveReport(uid, "1001690072001967", 0, r.Uuid.ToString(), r.Type, null, r.Address, bytes, BattleReports.ToJson(r), BattleReports.Fights(r));
            }
            Assert.Empty(store.Pending());
            Assert.Equal(2, ((List<Dictionary<string, object?>>)store.List()[0]["sides"]!).Count);
            var (factors, upsets, fights) = store.Analysis();
            Assert.Equal(2, fights);
            var hero = factors.Single(f => f.Factor == "heroPower");
            Assert.Equal(2, hero.Fights);
            Assert.Equal(2, hero.WinnerHigher);   // both winners had more hero power
            Assert.Equal(1, hero.WinnerHigherShare);
            Assert.Empty(upsets);
        }
        finally { TestDb.Drop(path); }
    }

    [Fact]
    public void PrivateRoomsAndTheOtherMember()
    {
        Assert.True(LastWar.Storage.ChatStore.IsPrivate("custom_2093679368000647_1001690072001967_v2"));
        Assert.False(LastWar.Storage.ChatStore.IsPrivate("custom_season_sustain_1322"));
        Assert.False(LastWar.Storage.ChatStore.IsPrivate("country_1967"));
        Assert.Equal("1001690072001967", LastWar.Server.Inbox.Other("custom_2093679368000647_1001690072001967_v2", "2093679368000647"));
        Assert.Equal("2093679368000647", LastWar.Server.Inbox.Other("custom_2093679368000647_1001690072001967_v2", "1001690072001967"));
    }

    // Research helper: LASTWAR_DECODE=<file.bin> writes <file>.json (does nothing otherwise).
    [Fact]
    public void DecodeAFileOnRequest()
    {
        if (Environment.GetEnvironmentVariable("LASTWAR_DECODE") is not { Length: > 0 } f) return;
        File.WriteAllText(f + ".json", BattleReports.ToJson(BattleReports.Decode(File.ReadAllBytes(f))));
    }

    // 185: Ghost defends with five Missile heroes against five Aircraft. Missile Vehicles counter Aircraft;
    // both sides get the 20% five-of-a-type bonus; Ghost's front row is McGregor (guardian) and Tesla
    // (damage dealer), both shielded by his Missile shield chip.
    [Fact]
    public void LineUpRulesFromTheGameTables()
    {
        var heroes = new Dictionary<int, (int, int)>
        {
            [50017] = (3, 2), [50018] = (3, 2), [50019] = (3, 1), [50020] = (3, 2), [50021] = (3, 1),
            [50013] = (2, 1), [50014] = (2, 2), [50015] = (2, 2), [50016] = (2, 2), [50022] = (2, 1),
        };
        var chips = new Dictionary<int, (int, int, int)> { [1502] = (3, 1, 5), [2502] = (3, 2, 5), [1503] = (2, 1, 5), [2503] = (2, 2, 5) };
        var s = BattleReports.Fights(BattleReports.Decode(Fixture("report_185.bin")), new LastWar.Core.Model.HeroCatalog(heroes, chips));
        var (atk, def) = (s.Single(x => x.Side == 0), s.Single(x => x.Side == 1));
        Assert.Equal(1, def.Factors["typeAdvantage"]);
        Assert.Equal(-1, atk.Factors["typeAdvantage"]);
        Assert.Equal(0.2, def.Factors["formationBonus"]);
        Assert.Equal(new[] { 50013, 50016 }, def.Heroes.Where(h => h.Front).Select(h => h.HeroId));
        Assert.Equal(1, def.Factors["frontDamageDealers"]);
        Assert.Equal(2, def.Factors["frontShieldedByChip"]);
        Assert.Equal(5, def.Factors["heroesCounteringThem"]);
        Assert.Equal(0.10, LastWar.Core.Model.HeroCatalog.FormationBonus([1, 1, 1, 2, 2]));
        Assert.Equal(0.05, LastWar.Core.Model.HeroCatalog.FormationBonus([1, 1, 1, 2, 3]));
        Assert.Equal(0, LastWar.Core.Model.HeroCatalog.FormationBonus([1, 1, 2, 2, 3]));
    }

    [Fact]
    public void AddressesResolveLikeTheGame()
    {
        Assert.Equal("https://lastwar-fight-report-s3.akamaized.net/reportFrame/bf/1.bin", BattleReports.Url("aws://reportFrame/bf/1.bin"));
        Assert.Equal("https://lastwar-fight-report.akamaized.net/reportFrame/91/2.bin", BattleReports.Url("ali://reportFrame/91/2.bin"));
        Assert.Null(BattleReports.Url("ali_local://x"));
    }
}
