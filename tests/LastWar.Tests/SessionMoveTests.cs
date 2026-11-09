using System.Text.Json.Nodes;
using LastWar.Server;

namespace LastWar.Tests;

public class SessionMoveTests
{
    // Export -> Import round-trips the login files, and importing over an existing session sets the
    // old files aside as .bak instead of overwriting them.
    [Fact]
    public void ExportImportKeepsWhatWasThere()
    {
        var root = Path.Combine(Path.GetTempPath(), "lw_move_" + Guid.NewGuid().ToString("n"));
        try
        {
            var store = new SessionStore(Path.Combine(root, "runners"), Path.Combine(root, "sessions"));
            var home = store.Home("acct");
            Directory.CreateDirectory(home);
            File.WriteAllText(store.SessionPath("acct"), """{"gameUid":"1","accessToken":"t1"}""");
            File.WriteAllText(Path.Combine(home, ".lastwar_goclient_device_id"), "dev-1");
            var dump = store.Export("acct", new RunnerSpec { Id = "acct", Name = "acct", Kind = "account", Tier = "service" });

            File.WriteAllText(store.SessionPath("acct"), """{"gameUid":"1","accessToken":"t2"}""");   // a newer login here
            var spec = store.Import(JsonNode.Parse(dump.ToJsonString())!.AsObject());
            Assert.Equal("service", spec.Tier);
            Assert.Contains("t1", File.ReadAllText(store.SessionPath("acct")));
            Assert.Contains(Directory.GetFiles(home), f => f.Contains(".bak-import-") && File.ReadAllText(f).Contains("t2"));
            Assert.Throws<ArgumentException>(() => store.Import(new JsonObject { ["format"] = "nope" }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LoginErrorsSayWhy()
    {
        var e = new LastWar.Client.LoginFailedException(28, "{ep=[E011], ec=28}");
        Assert.Equal("E011", e.Reason);
        Assert.Contains("no longer accepts", e.Message);
        Assert.Contains("E011", e.Message);
    }
}
