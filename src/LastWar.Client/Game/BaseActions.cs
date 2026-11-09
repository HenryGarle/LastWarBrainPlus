using LastWar.Protocol.Sfs;

namespace LastWar.Client.Game;

/// <summary>
/// Base building types, resolved from the game's own BuildingTypes enum cross-referenced against
/// the shipped English display strings. A building instance's bId is its base type id whatever its
/// level; level is a separate field.
/// </summary>
public static class BuildingCatalog
{
    public const int Farmland = 10201000;           // LW_BUILD_FARMLAND
    public const int IronMine = 10202000;           // LW_BUILD_QUARRY
    public const int GoldMine = 10207000;           // LW_BUILD_GOLD_MILL
    public const int Smelter = 10209000;            // LW_BUILD_SMELTERY
    public const int MaterialWorkshop = 10211000;   // LW_BUILD_MATERIALS_WORKERSHOP
    public const int TrainingBase = 10210000;       // LW_BUILD_TRAINING_CENTER
    public const int OilWell = 10221000;            // LW_BUILD_PETROLEUM
    public const int DronePartsShop = 10233000;     // LW_BUILD_TACTICAL_COMPONENT
    public const int TacticalCenter = 10143000;     // LW_BUILD_TACTICAL_CENTER
    public const int ArmamentInstitute = 10227000;  // "Armament Institute"
    public const int SporeFactory1 = 842000;        // LW_BUILD_SEASON6_QUARTZ_FACTORY1
    public const int SporeFactory2 = 843000;
    public const int SporeFactory3 = 844000;
    public const int SporeFactory4 = 845000;
    public const int TruckStation1 = 10138000;      // LW_BUILD_TRUCK_STATION_1
    public const int TruckStation2 = 10139000;
    public const int TruckStation3 = 10140000;
    public const int TruckStation4 = 10141000;
    public const int ComponentFactory = 10214000;   // LW_BUILD_SQUAD_EQUIP_FACTORY
    public const int TacticalInstitute = 10235000;  // LW_BUILD_DOMINATOR_TRAIN

    /// <summary>
    /// Every building type that accepts building.production.collect, and whether a real collection
    /// has actually been seen from it.
    ///
    /// The value is the distinction that matters. A true entry has returned status=1 with real
    /// resources on a live account. A false entry has only ever answered errorCode=602026 "in
    /// production, please be patient" - which still tells us the pairing is real, because a wrong
    /// pairing answers E000001 "Building type error" instead (confirmed against Training Base on
    /// the separate building.camp.collect family, which does get E000001). Five of the six
    /// unconfirmed types are also missing the prodST/prodStatus/prodET timestamps every collecting
    /// building carries, and sit at level 1, which reads as a production cycle never started rather
    /// than one merely not due. No "start production" command was found for any of them.
    ///
    /// Do not promote a false to true on a clean run alone; promote it when a run returns status=1.
    /// </summary>
    static readonly Dictionary<int, bool> CollectConfirmed = new()
    {
        [Farmland] = true,
        [IronMine] = true,
        [GoldMine] = true,
        [Smelter] = true,
        [MaterialWorkshop] = true,
        [TrainingBase] = true,
        [OilWell] = true,            // collects petroleum, resId=23
        [DronePartsShop] = true,     // collects item 7038, Drone Parts
        [ComponentFactory] = true,   // the real "Component Factory", resId=630011
        [SporeFactory1] = true,      // Season 6 only; collects obsidian, resId=10
        [SporeFactory2] = true,
        [SporeFactory3] = true,
        [SporeFactory4] = true,
        [TacticalCenter] = false,
        [ArmamentInstitute] = false,
        // Best positioned of the unconfirmed group: unlike the other five it does carry real
        // production timestamps, so it reads as genuinely not due rather than dormant.
        [TacticalInstitute] = false,
        // Truck Stations are NOT "Armed Truck" - that turned out to be an account-level idle
        // reward, not a building (see BaseActions.IdleRewardAsync). Kept because the pairing still
        // looks real, but no collection has ever succeeded on them.
        [TruckStation1] = false,
        [TruckStation2] = false,
        [TruckStation3] = false,
        [TruckStation4] = false,
    };

    static readonly Dictionary<int, string> Names = new()
    {
        [10100000] = "Headquarters",
        [10107000] = "Wall",
        [10119000] = "Worker's Hut",
        [GoldMine] = "Gold Mine",
        [Farmland] = "Farmland",
        [IronMine] = "Iron Mine",
        [Smelter] = "Smelter",
        [MaterialWorkshop] = "Material Workshop",
        [TrainingBase] = "Training Base",
        [OilWell] = "Oil Well",
        [DronePartsShop] = "Drone Parts Workshop",
        [TacticalCenter] = "Tactical Center",
        [ArmamentInstitute] = "Armament Institute",
        [SporeFactory1] = "Spore Factory I",
        [SporeFactory2] = "Spore Factory II",
        [SporeFactory3] = "Spore Factory III",
        [SporeFactory4] = "Spore Factory IV",
        [TruckStation1] = "Truck Station I (not Armed Truck)",
        [TruckStation2] = "Truck Station II (not Armed Truck)",
        [TruckStation3] = "Truck Station III (not Armed Truck)",
        [TruckStation4] = "Truck Station IV (not Armed Truck)",
        [ComponentFactory] = "Component Factory",
        [TacticalInstitute] = "Tactical Institute",
        [10208000] = "Gold Warehouse",
        [10203000] = "Food Warehouse (Bakery)",
        [10206000] = "Iron Warehouse (Steel Mill)",
        [10220000] = "Talent Hall",
        [10120000] = "Tavern",
        [10103000] = "Barracks (Military Camp)",
        [10104000] = "Drill Ground (Army Yard)",
        [10124000] = "Hospital",
        [10106000] = "Alliance Center",
        [10114000] = "Radar",
    };

    public static string NameOf(int bId) =>
        Names.TryGetValue(bId, out var n) ? n : $"(unknown type {bId})";

    /// <summary>
    /// The command that collects a building type's accumulated output, or null for a type that has
    /// none. Only building.production.collect appears here; the separate building.camp.collect
    /// family is real but covers Military Camp / Smith Shop / Tactical Chip Factory, and answers
    /// E000001 for every type in this list.
    /// </summary>
    public static string? CollectCommand(int bId) =>
        CollectConfirmed.ContainsKey(bId) ? "building.production.collect" : null;

    /// <summary>False for a type kept on the strength of the pairing alone. See CollectConfirmed.</summary>
    public static bool IsCollectConfirmed(int bId) =>
        CollectConfirmed.TryGetValue(bId, out var confirmed) && confirmed;
}

/// <summary>Everything that acts on the player's own base.</summary>
public sealed class BaseActions(LastWarClient client)
{
    /// <summary>How long to wait for the login push burst. See LoginPushes.</summary>
    public TimeSpan PushWait { get; init; } = TimeSpan.FromSeconds(12);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Sanity ceiling on collect calls in one run. Each can cost a full command timeout against a
    /// peer that stops answering, so this bounds the worst case at minutes rather than open-ended.
    /// </summary>
    const int MaxCollectsPerRun = 300;

    public async Task<GameActionResult> ListBuildingsAsync(CancellationToken ct = default)
    {
        var (buildings, _) = await LoginPushes
            .ReadAsync(client.Connection, PushWait, ct).ConfigureAwait(false);

        var rows = buildings
            .OrderBy(b => b.BId).ThenBy(b => b.Uuid)
            .Select(b => (IReadOnlyList<string>)new[]
            {
                b.BId.ToString(),
                BuildingCatalog.NameOf(b.BId),
                b.Level.ToString(),
                b.Uuid.ToString(),
                b.PointId.ToString(),
                BuildingCatalog.CollectCommand(b.BId) is null ? "-"
                    : BuildingCatalog.IsCollectConfirmed(b.BId) ? "yes" : "unconfirmed",
            })
            .ToList();

        var types = buildings.Select(b => b.BId).Distinct().Count();
        return new GameActionResult(
            $"{buildings.Count} buildings across {types} types",
            ["bId", "Name", "Level", "Uuid", "Point", "Collect"], rows);
    }

    /// <summary>
    /// Collects every collectible building's accumulated output: one
    /// building.production.collect {uuid} per instance.
    /// </summary>
    public async Task<GameActionResult> CollectResourcesAsync(CancellationToken ct = default)
    {
        var (buildings, _) = await LoginPushes
            .ReadAsync(client.Connection, PushWait, ct).ConfigureAwait(false);

        var toCollect = buildings
            .Where(b => BuildingCatalog.CollectCommand(b.BId) is not null)
            .Take(MaxCollectsPerRun)
            .ToList();

        var rows = new List<IReadOnlyList<string>>();
        var collected = 0;
        foreach (var b in toCollect)
        {
            ct.ThrowIfCancellationRequested();
            var p = new SfsObject();
            p.PutLong("uuid", b.Uuid);      // long: a building uuid sent as an int is rejected
            var reply = await client
                .TryCallAsync(BuildingCatalog.CollectCommand(b.BId)!, p, Timeout, ct)
                .ConfigureAwait(false);
            if (Replies.Ok(reply)) collected++;
            rows.Add([BuildingCatalog.NameOf(b.BId), b.Uuid.ToString(), Replies.Outcome(reply)]);
        }

        return new GameActionResult(
            $"collected {collected} of {toCollect.Count} buildings",
            ["Building", "Uuid", "Result"], rows);
    }

    /// <summary>
    /// The "Truck Rewards" panel: two idle tracks, Armed Truck and Overlord, both drained by one
    /// account-level command. This is what "Armed Truck" actually is - it is not a building, and
    /// not the Truck Stations in BuildingCatalog.
    ///
    /// action=0 peeks at the accumulated pool without taking it, action=1 claims it. The peek is
    /// always sent because it is the only way to report what was there; claiming was confirmed by
    /// a follow-up peek showing reward=[], so the pool really drains rather than being echoed back.
    /// </summary>
    public async Task<GameActionResult> IdleRewardAsync(bool claim, CancellationToken ct = default)
    {
        const string cmd = "lw.pve.idle.reward";
        var peekParams = new SfsObject();
        peekParams.PutInt("action", 0);
        var peek = await client.TryCallAsync(cmd, peekParams, Timeout, ct).ConfigureAwait(false);

        // Explicit element type: a collection initializer reads a bare [..] as an indexer.
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "peek", RewardCount(peek), Replies.Outcome(peek) },
        };
        if (!claim)
            return new GameActionResult("idle reward peeked, not claimed",
                ["Step", "Rewards", "Result"], rows);

        var claimParams = new SfsObject();
        claimParams.PutInt("action", 1);
        var claimed = await client.TryCallAsync(cmd, claimParams, Timeout, ct).ConfigureAwait(false);
        rows.Add(["claim", RewardCount(claimed), Replies.Outcome(claimed)]);

        return new GameActionResult(
            Replies.Ok(claimed) ? "idle reward claimed" : "idle reward claim failed",
            ["Step", "Rewards", "Result"], rows);
    }

    static string RewardCount(SfsObject? reply) =>
        reply is not null && reply.TryGetArray("reward", out var arr) ? arr.Count.ToString() : "-";
}
