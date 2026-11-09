namespace LastWar.Storage;

/// <summary>
/// Hive analysis, the same maths as the map page's web/src/explorer/hive.js (kept in step by
/// HiveTests and hive.check.mjs using the same cases): the hive is the biggest group of an
/// alliance's bases chained at most <see cref="Link"/> tiles apart; its score is 45% even spacing,
/// 35% repeated neighbour offsets, 20% no interior holes (the hole nearest the middle is a
/// deliberate gap). Server-side so every server can be scored without shipping every map.
/// </summary>
public static class Hive
{
    public const int Link = 8;
    public readonly record struct Base(int X, int Y);
    public sealed record Score(int N, double Pitch, double Spacing, double Alignment, double Filled, int Holes, bool Gap, int Value);

    public static List<Base> HiveOf(IReadOnlyList<Base> bases)
    {
        var parent = Enumerable.Range(0, bases.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        var grid = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < bases.Count; i++)
        {
            var k = (Floor(bases[i].X, Link), Floor(bases[i].Y, Link));
            if (!grid.TryGetValue(k, out var l)) grid[k] = l = [];
            l.Add(i);
        }
        for (var i = 0; i < bases.Count; i++)
        {
            var (gx, gy) = (Floor(bases[i].X, Link), Floor(bases[i].Y, Link));
            for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++)
                if (grid.TryGetValue((gx + dx, gy + dy), out var l))
                    foreach (var j in l)
                        if (j > i && Dist(bases[j].X - bases[i].X, bases[j].Y - bases[i].Y) <= Link) parent[Find(j)] = Find(i);
        }
        return bases.Select((b, i) => (b, r: Find(i))).GroupBy(x => x.r).Select(g => g.Select(x => x.b).ToList())
            .OrderByDescending(g => g.Count).FirstOrDefault() ?? [];
    }

    public static Score? Rate(IReadOnlyList<Base> hive)
    {
        var n = hive.Count;
        if (n < 5) return null;
        var nn = hive.Select((a, i) => hive.Where((_, j) => j != i).Min(b => Dist(a.X - b.X, a.Y - b.Y))).ToArray();
        var sorted = nn.OrderBy(v => v).ToArray();
        var pitch = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
        var mean = nn.Average();
        var sd = Math.Sqrt(nn.Sum(v => (v - mean) * (v - mean)) / n);
        var spacing = Clamp(1 - sd / mean / 0.5);

        var counts = new Dictionary<(int, int), int>();
        var pairs = 0;
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                int dx = hive[j].X - hive[i].X, dy = hive[j].Y - hive[i].Y;
                if (Dist(dx, dy) > pitch * 1.5) continue;
                if (dx < 0 || (dx == 0 && dy < 0)) { dx = -dx; dy = -dy; }
                counts[(dx, dy)] = counts.GetValueOrDefault((dx, dy)) + 1;
                pairs++;
            }
        var top = counts.OrderByDescending(kv => kv.Value).Take(4).ToList();
        var alignment = pairs > 0 ? (double)top.Sum(kv => kv.Value) / pairs : 0;

        int Near(double x, double y, double r) => hive.Count(b => Dist(b.X - x, b.Y - y) <= r);
        var holes = new List<(int X, int Y)>();
        var seen = new HashSet<(int, int)>();
        foreach (var b in hive)
            foreach (var ((dx, dy), _) in top)
                foreach (var s in new[] { 1, -1 })
                {
                    var p = (b.X + s * dx, b.Y + s * dy);
                    if (seen.Contains(p) || Near(p.Item1, p.Item2, 1.5) > 0 || Near(p.Item1, p.Item2, pitch * 1.2) < 3) continue;
                    seen.Add(p);
                    holes.Add(p);
                }
        var list = new List<(int X, int Y)>();
        foreach (var h in holes) if (!list.Any(o => Dist(o.X - h.X, o.Y - h.Y) < pitch * 0.75)) list.Add(h);
        double cx = hive.Average(b => b.X), cy = hive.Average(b => b.Y);
        list = [.. list.OrderBy(h => Dist(h.X - cx, h.Y - cy))];
        var gap = list.Count > 0 && Dist(list[0].X - cx, list[0].Y - cy) <= pitch * 2;
        if (gap) list.RemoveAt(0);
        var filled = Clamp(1 - list.Count / Math.Max(3, n * 0.1));
        return new Score(n, Math.Round(pitch * 10) / 10, spacing, alignment, filled, list.Count, gap,
            (int)Math.Round(100 * (0.45 * spacing + 0.35 * alignment + 0.2 * filled), MidpointRounding.AwayFromZero));
    }

    static int Floor(int v, int d) => (int)Math.Floor(v / (double)d);
    static double Dist(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);
    static double Clamp(double v) => Math.Max(0, Math.Min(1, v));
}