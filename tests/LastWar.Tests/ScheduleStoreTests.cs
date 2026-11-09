using LastWar.Storage;

namespace LastWar.Tests;

public class ScheduleStoreTests
{
    // Seeding again never undoes a pause, and a run left "running" by a server stop is marked
    // interrupted on the next start.
    [Fact]
    public void SeedKeepsEditsAndStartMarksCutOffRuns()
    {
        var path = TestDb.Create();
        try
        {
            var s = new ScheduleStore(path);
            s.EnsureSchema();
            s.Seed("p", "Pulse", "scan.profiles", new() { ["changes"] = "true" }, 60, enabled: true);
            s.SetEnabled("p", false);
            s.Seed("p", "Pulse", "scan.profiles", new() { ["changes"] = "true" }, 60, enabled: true);
            Assert.False(s.Jobs().Single().Enabled);
            Assert.Equal("true", s.Jobs().Single().Args["changes"]);

            var run = s.StartRun("p", "schedule");
            new ScheduleStore(path).EnsureSchema();
            Assert.Equal("interrupted", s.Runs("p", 1).Single()["state"]);
            Assert.Equal(run, (long)s.Runs(null, 1).Single()["id"]!);
        }
        finally { TestDb.Drop(path); }
    }
}
