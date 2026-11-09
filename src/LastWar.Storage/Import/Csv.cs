namespace LastWar.Storage.Import;

/// <summary>
/// Minimal RFC4180 reader/writer. The files this project handles are machine written
/// and small, so a dependency is not worth it - but quoted fields do occur, because
/// player names contain commas.
/// </summary>
public static class Csv
{
    static readonly char[] Special = [',', '"', '\n', '\r'];

    public static IEnumerable<Dictionary<string, string>> Read(string path)
    {
        // FileShare.ReadWrite so a CSV left open in Excel is still readable - that is
        // the normal state of these files on the operator''s machine.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        var header = ParseLine(r);
        if (header is null) yield break;
        while (ParseLine(r) is { } row)
        {
            if (row.Count == 1 && row[0].Length == 0) continue;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count && i < row.Count; i++) d[header[i]] = row[i];
            yield return d;
        }
    }

    static List<string>? ParseLine(TextReader r)
    {
        if (r.Peek() < 0) return null;
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        while (true)
        {
            var c = r.Read();
            if (c < 0) { fields.Add(sb.ToString()); return fields; }
            var ch = (char)c;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (r.Peek() == '"') { r.Read(); sb.Append('"'); }
                    else quoted = false;
                }
                else sb.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': fields.Add(sb.ToString()); sb.Clear(); break;
                case '\r': break;
                case '\n': fields.Add(sb.ToString()); return fields;
                default: sb.Append(ch); break;
            }
        }
    }

    public static void Write(string path, IReadOnlyList<string> header,
                             IEnumerable<IReadOnlyList<string?>> rows)
    {
        using var w = new StreamWriter(path);
        w.WriteLine(string.Join(',', header.Select(Escape)));
        foreach (var row in rows) w.WriteLine(string.Join(',', row.Select(Escape)));
    }

    static string Escape(string? v)
    {
        v ??= "";
        return v.IndexOfAny(Special) >= 0 ? '"' + v.Replace("\"", "\"\"") + '"' : v;
    }
}
