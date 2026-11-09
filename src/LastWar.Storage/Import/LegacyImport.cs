using System.Globalization;
using System.Text.RegularExpressions;
using LastWar.Core.Model;

namespace LastWar.Storage.Import;

/// <summary>
/// Reads the artefacts the original Go/Python tooling produced, so the migration to
/// C# does not throw away the history already collected.
///
/// ponytail: the al.rank reader parses the Go client's LOG output, because that is the
/// only place those rosters currently exist. It is a one-way bridge for data captured
/// before the port - the C# client reads the SFS objects directly and never needs it.
/// Delete it once the legacy logs have been imported.
/// </summary>
public static partial class LegacyImport
{
    public static IReadOnlyList<MapTile> ReadMapCsv(string path) =>
        Csv.Read(path).Select(r => new MapTile
        {
            Server = new ServerId(Int(r, "serverId")),
            PointId = Int(r, "pointId"),
            PointType = Int(r, "pointType"),
            OwnerUid = new PlayerUid(Str(r, "ownerUid")),
            Name = Str(r, "name"),
            AllianceId = Str(r, "allianceId"),
            AllianceTag = Str(r, "alAbbr"),
            HqLevel = Int(r, "level"),
            BuildId = Int(r, "buildId"),
        }).Where(t => t.OwnerUid.IsValid).ToList();

    public static IReadOnlyList<PowerSample> ReadPowerCsv(string path, DateTimeOffset fallback)
    {
        var outp = new List<PowerSample>();
        foreach (var r in Csv.Read(path))
        {
            var uid = Str(r, "uid");
            if (uid.Length == 0) continue;
            outp.Add(new PowerSample
            {
                TakenAt = DateTimeOffset.TryParse(Str(r, "scanTime"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var t) ? t : fallback,
                Server = new ServerId(Int(r, "serverId")),
                Uid = new PlayerUid(uid),
                Power = Long(r, "power"),
                Name = Str(r, "name"),
                AllianceId = Str(r, "allianceId"),
                AllianceTag = Str(r, "afn"),
                AllianceName = Str(r, "alName"),
                KillArmy = Long(r, "killArmy"),
                CareerLevel = Int(r, "careerLv"),
                HqLevel = Int(r, "hqLevel"),
                PointId = r.TryGetValue("point", out var p) && int.TryParse(p, out var pi) ? pi : null,
                Source = PowerSource.TileDetail,
            });
        }
        return outp;
    }

    [GeneratedRegex(@"\{([^{}]*)\}")] private static partial Regex RecordRx();
    [GeneratedRegex(@"(\w+)=([^,}]*)")] private static partial Regex FieldRx();

    /// <summary>Pulls alliance rosters out of a Go-client interactive log of al.rank calls.</summary>
    public static IReadOnlyList<PowerSample> ReadAllianceRankLog(
        string path, ServerId server, DateTimeOffset takenAt)
    {
        var outp = new List<PowerSample>();
        var seen = new HashSet<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (!line.Contains("\"msg\":\"received response\"") || !line.Contains("al.rank")) continue;
            foreach (Match m in RecordRx().Matches(line))
            {
                var f = FieldRx().Matches(m.Value)
                    .ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value.Trim());
                if (!f.TryGetValue("uid", out var uid) || uid.Length < 6) continue;
                if (!f.TryGetValue("power", out var pw) || !long.TryParse(pw, out var power)) continue;
                if (!seen.Add(uid)) continue;
                outp.Add(new PowerSample
                {
                    TakenAt = takenAt,
                    Server = server,
                    Uid = new PlayerUid(uid),
                    Power = power,
                    Name = f.GetValueOrDefault("name", ""),
                    AllianceId = f.GetValueOrDefault("allianceId", ""),
                    KillArmy = long.TryParse(f.GetValueOrDefault("armyKill"), out var k) ? k : 0,
                    HqLevel = int.TryParse(f.GetValueOrDefault("mainCityLv"), out var l) ? l : 0,
                    Source = PowerSource.AllianceRoster,
                });
            }
        }
        return outp;
    }

    static string Str(Dictionary<string, string> r, string k) => r.GetValueOrDefault(k, "").Trim();
    static int Int(Dictionary<string, string> r, string k) => int.TryParse(Str(r, k), out var v) ? v : 0;
    static long Long(Dictionary<string, string> r, string k) => long.TryParse(Str(r, k), out var v) ? v : 0;
}
