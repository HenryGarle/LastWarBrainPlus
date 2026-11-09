namespace LastWar.Core.Model;

/// <summary>
/// One side of one fight in a battle report, flattened for comparing winners with losers.
/// Factors are what a side brought (hero power, science, gear, line-up...); Outcome is what happened to it
/// (damage dealt, troops lost). Both are open maps so new measures can be added without a schema change,
/// and every row can be rebuilt from the stored report.
/// </summary>
public sealed record FightSide(
    int Fight, int Side, bool Won, string Uid, string Name, string Tag, int Server,
    Dictionary<string, double> Factors, Dictionary<string, double> Outcome, List<FightHero> Heroes);

/// <summary>
/// One hero (or the drone, Unit 2) in a fight. Type = troop type (1 Tank, 2 Missile Vehicle, 3 Aircraft,
/// lw_hero.army_type); Job = lw_hero.army_job (1 guardian, 2 damage dealer, 3 support); Front = in the
/// front row (unit slots 1-2 / 6-7). Chips = the drone's skill chip ids.
/// </summary>
public sealed record FightHero(
    int HeroId, int Unit, int Level, int Rank, int Awaken, int WeaponLevel, int EquipLevels, int EquipPromote,
    int SkillLevels, long Troops, long TroopsAfter, double Damage, double Injured,
    int Type = 0, int Job = 0, bool Front = false, List<int>? Chips = null, long Hp = 0);

/// <summary>What the report itself does not say about a hero or a drone chip: from the game tables.</summary>
public sealed record HeroCatalog(
    IReadOnlyDictionary<int, (int Type, int Job)> Heroes,
    IReadOnlyDictionary<int, (int HeroType, int SkillType, int Quality)> Chips)
{
    public static readonly HeroCatalog Empty = new(new Dictionary<int, (int, int)>(), new Dictionary<int, (int, int, int)>());

    /// <summary>Troop type that a type counters: Tanks counter Missile Vehicles, Missile Vehicles counter Aircraft, Aircraft counter Tanks.</summary>
    public static int Counters(int type) => type switch { 1 => 2, 2 => 3, 3 => 1, _ => 0 };

    /// <summary>Line-up bonus to HP, Attack and Defense (camp_effect): 3 of a type 5%, 3+2 10%, 4 15%, 5 20%.</summary>
    public static double FormationBonus(IReadOnlyCollection<int> types)
    {
        var counts = types.Where(t => t > 0).GroupBy(t => t).Select(g => g.Count()).OrderDescending().ToList();
        var top = counts.FirstOrDefault();
        return top >= 5 ? 0.20 : top == 4 ? 0.15 : top == 3 && counts.ElementAtOrDefault(1) == 2 ? 0.10 : top == 3 ? 0.05 : 0;
    }
}
