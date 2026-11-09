using System.Text.Json;
using System.Text.Json.Nodes;
using LastWar.Client;
using LastWar.Client.Auth;

namespace LastWar.Server;

/// <summary>
/// Creating and storing sessions: a fresh guest, or a real account signed in by email.
///
/// Every session owns a FOLDER, not just a file. The device identity sits beside the
/// session in it, because the account is bound to that device - which is also what keeps
/// a guest from ever touching the real account's saved credentials.
///
/// These files hold live access tokens in plain text. They are no more exposed than the
/// session the Go client already wrote next to them, but they are credentials: the store
/// stays inside the data folder and nothing here logs a token.
/// </summary>
public sealed class SessionStore(string runnersDir, string sessionsDir)
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public string Home(string id) => Path.Combine(sessionsDir, id);

    public string SessionPath(string id) =>
        Path.Combine(Home(id), ".lastwar_goclient_session.json");

    /// <summary>Writes the session and the runner spec that makes it show up in the fleet.</summary>
    public RunnerSpec Save(string id, SessionConfig config, string kind, string note)
    {
        id = Slug(id);
        Directory.CreateDirectory(Home(id));
        File.WriteAllText(SessionPath(id), JsonSerializer.Serialize(config, Pretty));

        var spec = new RunnerSpec
        {
            Id = id,
            Name = id,
            ConfigPath = SessionPath(id),
            Kind = kind,
            Note = note,
        };
        Directory.CreateDirectory(runnersDir);
        File.WriteAllText(Path.Combine(runnersDir, id + ".json"), JsonSerializer.Serialize(spec, Pretty));
        return spec;
    }

    public async Task<RunnerSpec> CreateGuestAsync(string id, string? zone, CancellationToken ct = default)
    {
        id = Slug(id);
        var config = await GuestBootstrap.CreateAsync(Home(id), zone, ct);
        return Save(id, config, "guest", $"guest on {config.Zone}");
    }

    const string DeviceFile = ".lastwar_goclient_device_id";

    /// <summary>A fresh folder for the guest that carries an email sign-in (sessions/auto/...), swept like any unused guest.</summary>
    public string TempHome(string id) => Path.Combine(sessionsDir, "auto", $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_email_{Slug(id)}");

    /// <summary>
    /// Anything already in the session's folder is renamed to *.bak-&lt;why&gt;-&lt;time&gt; first, so an
    /// import or a new sign-in never destroys a working login.
    /// </summary>
    void SetAside(string id, string why)
    {
        var home = Home(id);
        if (!Directory.Exists(home)) return;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        foreach (var f in Directory.GetFiles(home).Where(f => !Path.GetFileName(f).Contains(".bak")))
            File.Move(f, $"{f}.bak-{why}-{stamp}");
    }

    /// <summary>Stores an email sign-in: the account is bound to the device the sign-in used, so that device id moves in with it.</summary>
    public RunnerSpec SaveEmail(string id, SessionConfig config, string note, string deviceHome)
    {
        id = Slug(id);
        SetAside(id, "signin");
        Directory.CreateDirectory(Home(id));
        File.Copy(Path.Combine(deviceHome, DeviceFile), Path.Combine(Home(id), DeviceFile));
        return Save(id, config, "account", note);
    }

    /// <summary>A session as one JSON object: the files that make the login, plus how the runner is set up.</summary>
    public JsonObject Export(string id, RunnerSpec spec)
    {
        id = Slug(id);
        var files = new JsonObject();
        foreach (var name in new[] { ".lastwar_goclient_session.json", DeviceFile })
            if (File.Exists(Path.Combine(Home(id), name))) files[name] = File.ReadAllText(Path.Combine(Home(id), name));
        if (!files.ContainsKey(".lastwar_goclient_session.json")) throw new FileNotFoundException($"no stored session file for {id}");
        return new JsonObject
        {
            ["format"] = "lastwar-session/1", ["id"] = id, ["name"] = spec.Name, ["kind"] = spec.Kind, ["tier"] = spec.Tier, ["note"] = spec.Note,
            ["exportedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["files"] = files,
        };
    }

    /// <summary>The other side of Export. Existing files for the id are set aside, not overwritten.</summary>
    public RunnerSpec Import(JsonObject o)
    {
        if ((string?)o["format"] != "lastwar-session/1") throw new ArgumentException("not a session export (format lastwar-session/1)");
        var id = Slug((string?)o["id"] ?? "");
        if (id.Length == 0) throw new ArgumentException("the export has no id");
        var files = o["files"] as JsonObject ?? throw new ArgumentException("the export has no files");
        var session = (string?)files[".lastwar_goclient_session.json"] ?? throw new ArgumentException("the export has no session file");
        JsonDocument.Parse(session).Dispose();   // must be JSON before anything is touched
        SetAside(id, "import");
        Directory.CreateDirectory(Home(id));
        File.WriteAllText(SessionPath(id), session);
        if ((string?)files[DeviceFile] is { Length: > 0 } device) File.WriteAllText(Path.Combine(Home(id), DeviceFile), device);
        var spec = new RunnerSpec
        {
            Id = id, Name = (string?)o["name"] is { Length: > 0 } n ? n : id, ConfigPath = SessionPath(id),
            Kind = (string?)o["kind"] ?? "account", Tier = (string?)o["tier"] ?? "", Note = $"{o["note"]} (imported {DateTime.UtcNow:yyyy-MM-dd})".Trim(),
        };
        Directory.CreateDirectory(runnersDir);
        File.WriteAllText(Path.Combine(runnersDir, id + ".json"), JsonSerializer.Serialize(spec, Pretty));
        return spec;
    }

    /// <summary>Forgets a session. The folder goes only when asked - tokens are not silently destroyed.</summary>
    public void Delete(string id, bool withFiles)
    {
        id = Slug(id);
        var spec = Path.Combine(runnersDir, id + ".json");
        if (File.Exists(spec)) File.Delete(spec);
        if (withFiles && Directory.Exists(Home(id))) Directory.Delete(Home(id), recursive: true);
    }

    public sealed record CleanupItem(string Id, string Folder, string Reason);

    /// <summary>
    /// Throwaway guest sessions that nothing is using: runner specs of kind "guest" plus orphaned
    /// guest folders (old wave/probe homes, and leftovers under sessions\auto a failed delete left).
    ///
    /// A session is only ever removed when EVERY one of these holds - anything doubtful is kept:
    ///   - its folder is inside the sessions folder (the default Sadmin session lives in the user
    ///     profile, so it can never qualify);
    ///   - its spec, if it has one, says kind "guest" - "account" and unlabelled specs are kept;
    ///   - its session file has no gameUid, or there is no session file. A real account always has
    ///     one; a guest gets none until the server assigns it at login;
    ///   - it is not in <paramref name="inUse"/> (running, connecting, chatting, mid email sign-in);
    ///   - its folder is older than <paramref name="minAge"/> (the background sweep passes an hour so a
    ///     guest created a moment ago is still there to be started); sessions\auto needs 2 hours at
    ///     least - a running operation may still be using it.
    /// A guest can log in only once (a second login is ec=28 E011, verified on every stored guest
    /// 2026-09-25), so a used guest session is dead weight.
    /// </summary>
    public List<CleanupItem> Cleanup(ISet<string> inUse, bool apply, TimeSpan minAge = default, Action<string>? retire = null)
    {
        // retire: keeps a guest that sits on a server we track (GuestKeeper) and deletes the rest.
        void Drop(string folder) { if (retire is not null) retire(folder); else Directory.Delete(folder, recursive: true); }
        bool Young(string folder, TimeSpan age) =>
            Directory.Exists(folder) && Directory.GetLastWriteTimeUtc(folder) > DateTime.UtcNow - age;
        var items = new List<CleanupItem>();
        var root = Path.GetFullPath(sessionsDir);
        bool Inside(string path) =>
            Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        bool HasAccount(string folder)
        {
            var file = Path.Combine(folder, ".lastwar_goclient_session.json");
            if (!File.Exists(file)) return false;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                return !doc.RootElement.TryGetProperty("gameUid", out var uid) || uid.GetString() is not ("" or null);
            }
            catch { return true; }                    // unreadable = unknown = keep
        }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.Exists(runnersDir) ? Directory.EnumerateFiles(runnersDir, "*.json") : [])
        {
            RunnerSpec? spec;
            try { spec = JsonSerializer.Deserialize<RunnerSpec>(File.ReadAllText(file)); } catch { continue; }
            if (spec is null) continue;
            var id = string.IsNullOrWhiteSpace(spec.Id) ? Path.GetFileNameWithoutExtension(file) : spec.Id;
            var folder = Path.GetDirectoryName(spec.ConfigPath ?? "") ?? "";
            claimed.Add(Path.GetFullPath(folder.Length > 0 ? folder : Home(id)));
            if (spec.Kind != "guest" || inUse.Contains(id) || folder.Length == 0 || !Inside(folder) || HasAccount(folder) || Young(folder, minAge)) continue;
            items.Add(new CleanupItem(id, folder, spec.Note ?? "guest"));
            if (apply)
            {
                File.Delete(file);
                if (Directory.Exists(folder)) Drop(folder);
            }
        }

        // Folders no spec points at. sessions\auto itself stays; its children are judged one by one.
        var auto = Path.Combine(root, "auto");
        var kept = Path.Combine(root, "kept");   // guests on tracked servers (GuestKeeper): never cleaned up
        var orphans = Directory.Exists(root) ? Directory.EnumerateDirectories(root).Where(d => !d.Equals(auto, StringComparison.OrdinalIgnoreCase) && !d.Equals(kept, StringComparison.OrdinalIgnoreCase)) : [];
        var autoKids = Directory.Exists(auto) ? Directory.EnumerateDirectories(auto) : [];
        foreach (var folder in orphans.Concat(autoKids))
        {
            var id = Path.GetFileName(folder);
            if (claimed.Contains(Path.GetFullPath(folder)) || inUse.Contains(id) || HasAccount(folder)) continue;
            var inAuto = folder.StartsWith(auto + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (Young(folder, inAuto && minAge < TimeSpan.FromHours(2) ? TimeSpan.FromHours(2) : minAge)) continue;
            items.Add(new CleanupItem(id, folder, inAuto ? "leftover operation guest" : "guest folder with no runner"));
            if (apply) Drop(folder);
        }
        return items;
    }

    /// <summary>Ids become folder names, so they are restricted rather than escaped.</summary>
    public static string Slug(string id)
    {
        var clean = new string((id ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (clean.Length == 0) throw new ArgumentException("a session needs a name");
        return clean.Length > 40 ? clean[..40] : clean;
    }
}

/// <summary>
/// An email sign-in waiting on the code from the user's inbox.
///
/// It holds a live guest connection open between the two steps, because the exchange
/// runs over the game socket and both halves must happen on the same one.
/// </summary>
public sealed class PendingEmailLogin : IAsyncDisposable
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public required string SessionId { get; init; }
    /// <summary>Where the throwaway guest that carries the sign-in lives - never the account's own folder.</summary>
    public required string TempHome { get; init; }
    public required GameIdentity Identity { get; init; }
    public required LastWarClient Client { get; init; }
    public required EmailLogin Login { get; init; }
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<Role> Roles { get; set; } = [];

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
