using LastWar.Client.Auth;
using LastWar.Storage;

namespace LastWar.Operations;

/// <summary>
/// Throwaway guests land on whatever server the gateway picks (seen: 14, 367, 894, 1959...). One that lands on a
/// server we track (server_track level basic/full) is a foothold we cannot make on purpose - a guest asked for a
/// chosen server gets E002 (no character there) - so it is KEPT, never deleted: moved to data/sessions/kept/
/// with its device id and session (access + refresh token). Henry, 2026-10-08.
/// Guests elsewhere are deleted as before. Re-using a kept guest does not work yet: a second login with its uid
/// is refused (E005) and without it E011 - see AGENTS.md.
/// </summary>
public static class GuestKeeper
{
    /// <summary>Keeps or deletes a finished guest home; returns the kept folder, or null. Never throws.</summary>
    public static string? Retire(string dataDir, string database, string home, string label = "")
    {
        try
        {
            if (!Directory.Exists(home)) return null;
            var file = Path.Combine(home, ".lastwar_goclient_session.json");
            var zone = File.Exists(file) ? SessionConfig.Load(file).Zone : "";
            if (zone.StartsWith("APS") && int.TryParse(zone[3..], out var server)
                && new ServerRegistry(database).Tracking().TryGetValue(server, out var t) && t.Level != "off")
            {
                var kept = Path.Combine(dataDir, "sessions", "kept", $"{server}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{label}{Path.GetFileName(home)}");
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                Directory.Move(home, kept);
                return kept;
            }
            Directory.Delete(home, recursive: true);
        }
        catch { /* a locked file or an unreadable registry: leave it for the cleanup */ }
        return null;
    }
}
