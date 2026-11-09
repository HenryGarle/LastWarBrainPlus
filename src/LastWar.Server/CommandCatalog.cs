namespace LastWar.Server;

/// <summary>
/// The 3,094-command reference recovered from the client, served for browsing. Loaded
/// once - the file only changes when the game does.
/// </summary>
public sealed class CommandCatalog
{
    public sealed record Entry(string Command, string MsgName, string Module, string Params, int NParams);

    public IReadOnlyList<Entry> Entries { get; }

    public CommandCatalog(string csvPath)
    {
        var list = new List<Entry>();
        if (File.Exists(csvPath))
            foreach (var line in File.ReadLines(csvPath).Skip(1))
            {
                var f = Split(line);
                if (f.Count < 5 || f[0].Length == 0) continue;
                list.Add(new Entry(f[0], f[1], f[2], f[3], int.TryParse(f[4], out var n) ? n : 0));
            }
        Entries = list;
    }

    public IEnumerable<Entry> Search(string? q, int limit) =>
        (string.IsNullOrWhiteSpace(q)
            ? Entries
            : Entries.Where(e =>
                e.Command.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.MsgName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Module.Contains(q, StringComparison.OrdinalIgnoreCase)))
        .Take(limit);

    /// <summary>Minimal CSV split: quoted fields, doubled quotes. The params column has commas in it.</summary>
    static List<string> Split(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch != '"') { sb.Append(ch); }
                else if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = false;
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
