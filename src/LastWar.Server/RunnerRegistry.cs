using System.Collections.Concurrent;
using System.Text.Json;

namespace LastWar.Server;

/// <summary>
/// The fleet. Runners are declared as one JSON file each under data/runners/, so adding
/// a machine to the fleet is dropping a file in - no code change, no restart beyond a
/// reload.
///
/// The hard rule this enforces: ONE session per account. A second login on the same
/// account silently evicts the first, which looks like a hang rather than an error, so
/// two runners pointing at the same session file can never both be running.
/// </summary>
public sealed class RunnerRegistry
{
    readonly ConcurrentDictionary<string, Runner> _runners = new();
    readonly string _dir;

    public RunnerRegistry(string runnersDir, string defaultSessionPath)
    {
        _dir = runnersDir;
        Directory.CreateDirectory(_dir);
        if (!Directory.EnumerateFiles(_dir, "*.json").Any() && File.Exists(defaultSessionPath))
            File.WriteAllText(Path.Combine(_dir, "default.json"), JsonSerializer.Serialize(
                new RunnerSpec
                {
                    Id = "default",
                    Name = "default",
                    ConfigPath = defaultSessionPath,
                    Note = "the saved session in the user profile",
                },
                new JsonSerializerOptions { WriteIndented = true }));
        Reload();
    }

    public void Reload()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            RunnerSpec? spec;
            try { spec = JsonSerializer.Deserialize<RunnerSpec>(File.ReadAllText(file)); }
            catch { continue; }                       // a half-written file is not fatal
            if (spec is null) continue;

            var id = string.IsNullOrWhiteSpace(spec.Id) ? Path.GetFileNameWithoutExtension(file) : spec.Id;
            spec = spec with { Id = id, Name = string.IsNullOrWhiteSpace(spec.Name) ? id : spec.Name };
            _runners.GetOrAdd(id, _ => new Runner(spec));
        }

        // A spec file deleted on disk should leave the fleet, unless it is running.
        foreach (var known in _runners.Values.ToList())
        {
            if (known.State is RunnerState.Ready or RunnerState.Connecting) continue;
            if (!File.Exists(Path.Combine(_dir, known.Spec.Id + ".json"))) _runners.TryRemove(known.Spec.Id, out _);
        }
    }

    public IEnumerable<Runner> All => _runners.Values.OrderBy(r => r.Spec.Id);

    /// <summary>Changes a runner's account tier and saves it in its spec file.</summary>
    public Runner SetTier(string id, string tier)
    {
        var r = Get(id);
        var spec = r.Spec with { Tier = tier };
        File.WriteAllText(Path.Combine(_dir, id + ".json"),
            JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true }));
        r.Spec = spec;
        return r;
    }

    public void Remove(string id) => _runners.TryRemove(id, out _);

    public Runner Get(string id) =>
        _runners.TryGetValue(id, out var r) ? r : throw new KeyNotFoundException($"no runner '{id}'");

    /// <summary>Which running runner already holds this account, if any.</summary>
    public Runner? HolderOf(Runner candidate) => _runners.Values.FirstOrDefault(r =>
        r != candidate
        && r.State is RunnerState.Ready or RunnerState.Connecting
        && string.Equals(r.Spec.ConfigPath, candidate.Spec.ConfigPath, StringComparison.OrdinalIgnoreCase));
}
