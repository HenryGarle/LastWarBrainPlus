using Google.Protobuf;
using LastWar.Core.Model;
using LastWar.Protocol.Sfs;

namespace LastWar.Client.Scanning;

public sealed record MapScanOptions
{
    /// <summary>Tiles per side. 1000 normally; a season big map is 3000.</summary>
    public int WorldSize { get; init; } = 1000;
    /// <summary>
    /// Tiles per AOI block side - and only the CLIENT's own request shapes (2026-10-07): lod 0 = 10, lod 1 = 20
    /// (LWAoiBlockSizeArray {10, 20, 1000}); block index = y * (3000 / blockSize) + x; leftBottom/rightTop =
    /// x + y * 3000 + 1; x/y = the view centre (WorldPointManager.SendAoiRequest, RecalculateBlockArg(3000, ..)).
    /// The old 50-tile blocks in 1000-wide rows were never a request a real client makes; probe.map.aoi showed
    /// the client layout returns more points, and the trucks (type 15 marches) with them.
    /// </summary>
    public int BlockSize { get; init; } = 10;
    /// <summary>The client always lays blocks out over 3000 tiles, whatever the server's own size.</summary>
    public int ClientTiles { get; init; } = 3000;
    /// <summary>The pre-2026-10-07 layout (rows of WorldSize / BlockSize), kept for scan.map.bench comparisons only.</summary>
    public bool LegacyLayout { get; init; }
    public int RowBlocks => LegacyLayout ? BlocksPerSide : ClientTiles / BlockSize;
    /// <summary>The real client never sends more than 160 block indices in one request.</summary>
    public int BatchSize { get; init; } = 160;
    /// <summary>Zoom level sent as viewLvl: the client pairs lod 0 with blocks of 10 and lod 1 with 20.</summary>
    public int Lod { get; init; } = 0;   // lod 1 thins the bases to about a third (2026-10-08)
    public int WorldId { get; init; }
    /// <summary>1 normally, 2 in single-server mode.</summary>
    public int BigMap { get; init; } = 1;
    public TimeSpan Gap { get; init; } = TimeSpan.FromMilliseconds(250);   // 70 ms was ~14 requests a second (2026-11-02)
    /// <summary>
    /// Requests kept outstanding at once. Replies echo the request "_id", so several can be on
    /// the wire together; 1 is the old strict request/response walk. The gap applies between
    /// sends either way, so this hides round-trip latency without raising the send rate.
    /// </summary>
    public int InFlight { get; init; } = 1;
    /// <summary>Times a batch that got no reply is sent again before it is given up on.</summary>
    public int Retries { get; init; } = 2;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(6);
    /// <summary>Consecutive timeouts tolerated before declaring the session dead.</summary>
    public int DeadAfterConsecutiveTimeouts { get; init; } = 8;

    public int BlocksPerSide => WorldSize / BlockSize;
    public int TotalBlocks => BlocksPerSide * BlocksPerSide;
    public int RequestCount => ((BlocksPerSide + 15) / 16) * ((BlocksPerSide + 9) / 10) + (LegacyLayout ? 0 : 9);
}

public sealed record MapScanProgress(int Request, int TotalRequests, int TilesFound);

/// <summary>
/// Sweeps a server's map with world.get.block and returns every occupied tile.
///
/// The command is C#-side in the game client and absent from the Lua command table,
/// which is why it needs a client-generated "_id", an int ARRAY of AOI block indices,
/// a blockSize and both view corners. Miss any of those and the server simply never
/// answers.
///
/// serverId is a request parameter, so one logged-in account can scan other servers -
/// subject to a level gate: too low an account gets errorCode=cross_server_view_tips.
/// </summary>
public sealed class MapScanner(LastWarClient client, MapScanOptions? options = null)
{
    readonly MapScanOptions _opt = options ?? new MapScanOptions();

    // The world the game lays out depends on whose server it is (2026-10-08): the account's OWN server sits in a
    // 3000 x 3000 world (3 x 3 servers, rows 3000 / size blocks wide), ANOTHER server is shown on its own 1000 x 1000
    // (rows 1000 / size). Same blocks, same views, same lod either way; asking another server in the 3000-wide
    // layout reads every third row of blocks - a third of the bases (#1959 1,388 v 4,230).
    bool _wide;
    int Row => _wide ? _opt.ClientTiles / _opt.BlockSize : _opt.BlocksPerSide;
    int _requestId = 1000;

    public MapScanOptions Options => _opt;

    /// <summary>Cities, alliance buildings and Marshal's Guard traps from the last scan (see ReadFeatures).</summary>
    public IReadOnlyCollection<MapFeature> LastFeatures { get; private set; } = [];

    /// <summary>Greened tiles (point ids) from the last scan's greenPoints - season 3's not-mud. Empty elsewhere.</summary>
    public IReadOnlyCollection<int> LastGreen { get; private set; } = [];

    /// <summary>Sees every raw reply - for probing fields the scanner does not read yet.</summary>
    public Action<SfsObject>? OnReply { get; set; }

    /// <summary>Batches that never got an answer in the last scan, after retries. 0 means complete.</summary>
    public int LastMissedBatches { get; private set; }

    /// <summary>Which cell of the client's 3 x 3 world the last scan found the server in (column, row).</summary>
    public (int X, int Y) LastCell { get; private set; }

    public async Task<IReadOnlyCollection<MapTile>> ScanAsync(
        ServerId server, IProgress<MapScanProgress>? progress = null, CancellationToken ct = default)
    {
        var found = new Dictionary<int, MapTile>();
        var features = new Dictionary<int, MapFeature>();
        var green = new HashSet<int>();
        var todo = new Queue<(int[] Index, int Tries)>();
        var side = _opt.BlocksPerSide;
        // A view of up to 160 blocks (the client's cap) - one camera position.
        int[] View(int x0, int y0, int w, int h) =>
            [.. from yy in Enumerable.Range(y0, h) from xx in Enumerable.Range(x0, w) select yy * Row + xx];
        // The client's world is 3000 x 3000 tiles: a 3 x 3 grid of servers, 1000 x 1000 each, and a server's cell
        // depends on the group (probe.map.overview 2026-10-07: #273 sat at cell 0,2). So the sweep first looks at
        // the middle of each cell (9 views, as a client panning around) to find which one is ours, then pans
        // across that cell only. Legacy layout: the old single pass.
        _wide = !_opt.LegacyLayout && server.Value == client.Server.Value;
        var cellCounts = new int[9];
        var cell = 0;
        if (_wide)
        {
            for (var c9 = 0; c9 < 9; c9++)
                todo.Enqueue((View(c9 % 3 * side + side / 2 - 8, c9 / 3 * side + side / 2 - 5, 16, 10), 0));
        }
        else
            for (var y0 = 0; y0 < side; y0 += 10)
                for (var x0 = 0; x0 < side; x0 += 16)
                    todo.Enqueue((View(x0, y0, Math.Min(16, side - x0), Math.Min(10, side - y0)), 0));
        var locating = _wide;
        // _id -> the batch it asked for and when it went out.
        var pending = new Dictionary<int, (int[] Index, int Tries, DateTimeOffset Sent)>();
        var consecutiveTimeouts = 0;
        var first = true;
        var answered = 0;
        LastMissedBatches = 0;

        while (todo.Count > 0 || pending.Count > 0 || locating)
        {
            if (locating && todo.Count == 0 && pending.Count == 0)
            {
                // Located: pan across our cell in 16 x 10 views.
                locating = false;
                cell = Array.IndexOf(cellCounts, cellCounts.Max());
                LastCell = (cell % 3, cell / 3);
                int cx = cell % 3 * side, cy = cell / 3 * side;
                for (var y0 = 0; y0 < side; y0 += 10)
                    for (var x0 = 0; x0 < side; x0 += 16)
                        todo.Enqueue((View(cx + x0, cy + y0, Math.Min(16, side - x0), Math.Min(10, side - y0)), 0));
                continue;
            }
            ct.ThrowIfCancellationRequested();
            while (todo.Count > 0 && pending.Count < Math.Max(1, _opt.InFlight))
            {
                var (index, tries) = todo.Dequeue();
                var id = Interlocked.Increment(ref _requestId);
                await client.Connection.SendCommandAsync("world.get.block", BuildRequest(server, index, first, id)).ConfigureAwait(false);
                first = false;
                pending[id] = (index, tries, DateTimeOffset.UtcNow);
                if (_opt.Gap > TimeSpan.Zero) await Task.Delay(LastWar.Client.Jitter.Of(_opt.Gap), ct).ConfigureAwait(false);
            }

            SfsObject reply;
            try
            {
                reply = (await client.Connection.WaitForCommandAsync(_opt.Timeout, ct, "world.get.block").ConfigureAwait(false)).Params;
            }
            catch (TimeoutException)
            {
                if (++consecutiveTimeouts >= _opt.DeadAfterConsecutiveTimeouts)
                    throw new IOException(
                        $"no reply to {consecutiveTimeouts} consecutive requests; the session is dead " +
                        "(most often another login on the same account evicted it)");
                // Nothing came back in a whole timeout: everything still out is presumed lost.
                foreach (var (_, p) in pending)
                    if (p.Tries < _opt.Retries) todo.Enqueue((p.Index, p.Tries + 1));
                    else LastMissedBatches++;
                pending.Clear();
                continue;
            }
            consecutiveTimeouts = 0;

            if (reply.GetString("errorCode") is { Length: > 0 } ec)
                throw new InvalidOperationException(
                    $"server {server} refused world.get.block: {ec} {reply.GetString("errorMsg")}");

            // Replies echo "_id"; if one ever does not, retire the oldest request instead.
            var rid = reply.GetInt("_id");
            var asked = pending.TryGetValue(rid, out var pa) ? pa.Index : null;
            if (!pending.Remove(rid) && pending.Count > 0) pending.Remove(pending.MinBy(p => p.Value.Sent).Key);

            OnReply?.Invoke(reply);
            var ours = 0;
            foreach (var tile in ReadTiles(reply, server).Where(t => t.Server == server)) { found[tile.PointId] = tile; ours++; }
            if (locating && asked is not null)
                cellCounts[asked[0] % Row / side + asked[0] / Row / side * 3] += ours;
            foreach (var f in ReadFeatures(reply, server)) features[f.PointId] = f;
            ReadGreen(reply, server, green);
            progress?.Report(new MapScanProgress(++answered, _opt.RequestCount, found.Count));
        }
        LastFeatures = features.Values;
        LastGreen = green;
        return found.Values;
    }

    SfsObject BuildRequest(ServerId server, int[] index, bool first, int id)
    {
        // The rectangle the batch covers, in tiles; rows are RowBlocks wide (3000 / size, as the client lays them).
        var count = Row;
        var row = _wide ? _opt.ClientTiles : _opt.WorldSize;
        var lbX = index.Min(i => i % count) * _opt.BlockSize;
        var lbY = index.Min(i => i / count) * _opt.BlockSize;
        var edge = (_wide ? _opt.ClientTiles : _opt.WorldSize) - 1;
        var rtX = Math.Min((index.Max(i => i % count) + 1) * _opt.BlockSize - 1, edge);
        var rtY = Math.Min((index.Max(i => i / count) + 1) * _opt.BlockSize - 1, edge);

        var o = new SfsObject();
        o.PutInt("_id", id);
        o.PutInt("bigMap", _opt.BigMap);
        o.PutInt("x", _opt.LegacyLayout ? lbX : (lbX + rtX) / 2);   // the client sends its camera centre
        o.PutInt("y", _opt.LegacyLayout ? lbY : (lbY + rtY) / 2);
        o.PutInt("serverId", server.Value);
        o.PutInt("worldId", _opt.WorldId);
        o.PutInt("type", 0);
        o.PutInt("viewLvl", _opt.Lod);
        o.PutInt("timeStamp", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        o.PutIntArray("index", index);
        o.PutInt("blockSize", _opt.BlockSize);
        if (first) { o.PutInt("clearUuidSet", 1); if (!_opt.LegacyLayout) o.PutBool("force", true); }
        o.PutInt("leftBottom", lbX + lbY * row + 1);
        o.PutInt("rightTop", rtX + rtY * row + 1);
        return o;
    }

    /// <summary>
    /// The reply is either one block payload or a serverPointArr of them. Tiles arrive as
    /// byte arrays holding WorldPointInfo protobufs; only those carrying a BuildInfo are
    /// player bases - the rest are land, resources and event points.
    /// </summary>
    static IEnumerable<MapTile> ReadTiles(SfsObject reply, ServerId fallbackServer)
    {
        var blocks = reply.TryGetArray("serverPointArr", out var arr)
            ? arr.Objects().ToList()
            : [reply];

        foreach (var block in blocks)
        {
            foreach (var key in new[] { "points", "alInfos" })
            {
                if (!block.TryGetArray(key, out var list)) continue;
                foreach (var bytes in list.ByteArrays())
                {
                    Protobuf.WorldPointInfo point;
                    try { point = Protobuf.WorldPointInfo.Parser.ParseFrom(bytes); }
                    catch (InvalidProtocolBufferException) { continue; }

                    var b = point.BuildInfo;
                    if (b is null || (b.OwnerUid.Length == 0 && b.Name.Length == 0)) continue;

                    yield return new MapTile
                    {
                        Server = point.ServerId > 0 ? new ServerId(point.ServerId) : fallbackServer,
                        PointId = point.Id,
                        PointType = point.PointType,
                        OwnerUid = new PlayerUid(b.OwnerUid),
                        Name = b.Name,
                        AllianceId = b.AllianceId,
                        AllianceTag = b.AlAbbr,
                        HqLevel = b.Level,
                        BuildId = b.BuildId,
                    };
                }
            }
        }
    }

    /// <summary>
    /// The alliance points in a reply: cities (11), alliance buildings (15), strongholds (25), trade
    /// posts (35), mini-game cities (60) and Marshal's Guard traps (59). Each keeps its detail in
    /// extraInfo, a separate protobuf per type (the client's WorldPointManager switch); the trap
    /// uses WorldPointInfo field 25 instead. Only the scanned server's own points are kept - a
    /// reply can carry a neighbour's.
    /// </summary>
    public static IEnumerable<MapFeature> ReadFeatures(SfsObject reply, ServerId server)
    {
        var blocks = reply.TryGetArray("serverPointArr", out var arr) ? arr.Objects().ToList() : [reply];
        foreach (var block in blocks)
            foreach (var key in new[] { "points", "alInfos" })
            {
                if (!block.TryGetArray(key, out var list)) continue;
                foreach (var bytes in list.ByteArrays())
                {
                    Protobuf.WorldPointInfo p;
                    try { p = Protobuf.WorldPointInfo.Parser.ParseFrom(bytes); }
                    catch (InvalidProtocolBufferException) { continue; }
                    if (p.ServerId > 0 && p.ServerId != server.Value) continue;
                    MapFeature? f;
                    try { f = Feature(p, server); }
                    catch (InvalidProtocolBufferException) { continue; }
                    if (f is not null) yield return f;
                }
            }
    }

    /// <summary>greenPoints: one IndexGreenPoints per 10x10 block, listing its greened tiles.</summary>
    public static void ReadGreen(SfsObject reply, ServerId server, ISet<int> into)
    {
        var blocks = reply.TryGetArray("serverPointArr", out var arr) ? arr.Objects().ToList() : [reply];
        foreach (var block in blocks)
        {
            if (block.GetInt("serverId") is > 0 and var sid && sid != server.Value) continue;
            if (!block.TryGetArray("greenPoints", out var list)) continue;
            foreach (var bytes in list.ByteArrays())
            {
                try { foreach (var p in Protobuf.IndexGreenPoints.Parser.ParseFrom(bytes).Points) into.Add(p); }
                catch (InvalidProtocolBufferException) { }
            }
        }
    }

    static MapFeature? Feature(Protobuf.WorldPointInfo p, ServerId server)
    {
        var x = p.ExtraInfo.IsEmpty ? null : p.ExtraInfo.ToByteArray();
        MapFeature F(int build, int level, string al, string tag, string alName, string label = "", string holder = "") => new()
        {
            Server = server, PointId = p.Id, PointType = p.PointType, BuildId = build, Level = level,
            AllianceId = al, AllianceTag = tag, AllianceName = alName, Label = label, HolderUid = holder,
        };
        switch (p.PointType)
        {
            case 11 when x is not null:
                var c = Protobuf.AllianceCityPointInfo.Parser.ParseFrom(x);
                return F(c.CityId, 0, c.AllianceId, c.AlAbbr, c.AlName, c.CityName);
            case 15 when x is not null:
                var b = Protobuf.AllianceBuildingPointInfo.Parser.ParseFrom(x);
                return F(b.BuildId, b.Level, b.AllianceId, b.AlAbbr, "");
            case 25 when x is not null:
                var s = Protobuf.CityStrongholdPointInfo.Parser.ParseFrom(x);
                return F(s.StrongholdId, 0, s.AllianceId, s.AlAbbr, s.AlName, s.CityName);
            case 35 when x is not null:
                var t = Protobuf.CityTradePointInfo.Parser.ParseFrom(x);
                return F(t.TradeId, 0, t.AllianceId, t.AlAbbr, t.AlName, t.UserName, t.Uid);
            case 60 when x is not null:
                var g = Protobuf.CityMiniGamePointInfo.Parser.ParseFrom(x);
                return F(g.CityId, 0, "", "", "", g.UserName, g.Uid);
            case 59 when p.AllianceBossS0BuildPointInfo is { } m:
                return F(m.CfgId, m.CfgId, m.AllianceId, m.Abbr, "");
            default:
                return null;
        }
    }
}
