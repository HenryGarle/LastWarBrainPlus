using LastWar.Core.Model;
using LastWar.Storage;

namespace LastWar.Tests;

public class AllianceMembershipTests
{
    // Seen in X from the start (join date unknown), then in Y: X is a past stint that ended when
    // Y was first seen; Y is current and its join is known. A tag with no id is ignored.
    [Fact]
    public void StintsCloseWhenSeenElsewhere()
    {
        var path = TestDb.Create();
        try
        {
            var db = new LastWarDb(path);
            db.EnsureSchema();
            new WaveStore(path).EnsureSchema();
            var profiles = new ProfileStore(path);
            profiles.EnsureSchema();
            var t0 = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
            const string uid = "1234567890001967";
            db.InsertPowerSamples([new PowerSample { TakenAt = t0, Server = new ServerId(1967), Uid = new PlayerUid(uid), Power = 1, AllianceId = "X", AllianceTag = "XX" }]);
            profiles.Insert([
                new PlayerProfile { Uid = uid, TakenAt = t0.AddHours(1), AllianceId = "", AllianceTag = "XX", Source = "chat" },
                new PlayerProfile { Uid = uid, TakenAt = t0.AddHours(2), AllianceId = "Y", AllianceTag = "YY", Source = "sweep" },
            ]);
            Assert.Equal(2, new AllianceMembership(path).Rebuild());

            using var c = Db.Open(path);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT alliance_id, left_at, joined_known, next_tag FROM alliance_stint ORDER BY seq;";
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(("X", t0.AddHours(2).ToString("O"), 0L, "YY"), (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)));
            Assert.True(r.Read());
            Assert.Equal(("Y", "", 1L), (r.GetString(0), r.GetString(1), r.GetInt64(2)));
        }
        finally { TestDb.Drop(path); }
    }
}
