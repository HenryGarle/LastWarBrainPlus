namespace LastWar.Cli;

/// <summary>
/// Tiny --flag=value / --flag value parser.
/// ponytail: deliberately not System.CommandLine. A handful of verbs does not justify
/// a prerelease dependency whose API keeps moving; swap it in if the surface grows.
/// </summary>
public sealed class Args
{
    readonly Dictionary<string, string> _flags = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _positional = [];

    public Args(IEnumerable<string> argv)
    {
        var queue = new Queue<string>(argv);
        while (queue.Count > 0)
        {
            var a = queue.Dequeue();
            if (!a.StartsWith("--", StringComparison.Ordinal)) { _positional.Add(a); continue; }
            var body = a[2..];
            var eq = body.IndexOf('=');
            if (eq >= 0) { _flags[body[..eq]] = body[(eq + 1)..]; continue; }
            var next = queue.Count > 0 ? queue.Peek() : null;
            _flags[body] = next is not null && !next.StartsWith("--", StringComparison.Ordinal)
                ? queue.Dequeue()
                : "true";
        }
    }

    public IReadOnlyList<string> Positional => _positional;
    public string? Get(string name) => _flags.GetValueOrDefault(name);
    public bool Has(string name) => _flags.ContainsKey(name);

    public string Require(string name) =>
        Get(name) ?? throw new ArgumentException($"missing required --{name}");

    public int Int(string name, int fallback) =>
        int.TryParse(Get(name), out var v) ? v : fallback;
}
