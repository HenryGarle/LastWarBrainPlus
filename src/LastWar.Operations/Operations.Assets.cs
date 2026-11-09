using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using LastWar.Lua;

namespace LastWar.Operations;

/// <summary>
/// Game assets that ship inside the APK's install-time pack, pulled out into data/assets:
///   locale/&lt;lang&gt;.json  every UI string by dialog id (assets/locale/&lt;ver&gt;/&lt;lang&gt;.bin:
///                          gzip; int32, then .NET-style length-prefixed key/value string pairs)
///   tables/&lt;name&gt;.json   the config tables (assets/table/*.data: a zip of Lua chunks that only
///                          build constants - evaluated by LuaTableReader, never run)
///   lwemoji.ttf           the chat emoji font (CBDT colour bitmaps) - browsers render it directly
/// Nothing in the APK is modified; re-run after a game update to refresh.
/// </summary>
public static partial class Operations
{
    static void AddAssetOperations()
    {
        Add("assets.extract", "Assets", "Extract game assets",
            "Pulls the UI strings (for mail titles, items, positions...), the config tables and the chat "
            + "emoji font out of the APK's install-time pack into data/assets. Local only - no network.",
            c =>
            {
                var apk = c.Text("apk", @"C:\Temp\LWC\apk\split_install_time_pack.apk");
                var outDir = Path.Combine(c.DataDir, "assets");
                var langs = c.Text("languages", "en").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var rows = new List<object?[]>();
                using var zip = ZipFile.OpenRead(apk);

                Directory.CreateDirectory(Path.Combine(outDir, "locale"));
                foreach (var lang in langs)
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("assets/locale/", StringComparison.Ordinal) && e.Name == $"{lang}.bin");
                    if (entry is null) { rows.Add(["locale", lang, 0, "not in the pack"]); continue; }
                    var strings = ReadLocale(entry.Open());
                    File.WriteAllText(Path.Combine(outDir, "locale", $"{lang}.json"), JsonSerializer.Serialize(strings));
                    rows.Add(["locale", lang, strings.Count, entry.FullName]);
                }

                var tableEntry = zip.Entries.First(e => e.FullName.StartsWith("assets/table/table_", StringComparison.Ordinal));
                Directory.CreateDirectory(Path.Combine(outDir, "tables"));
                using (var tables = new ZipArchive(tableEntry.Open(), ZipArchiveMode.Read))
                {
                    var (ok, failed) = GameTables.Extract(tables, Path.Combine(outDir, "tables"));
                    rows.Add(["tables", "", ok, failed > 0 ? $"{failed} not plain constant tables, skipped" : tableEntry.FullName]);
                }

                // Enums that live in the scripts rather than the tables: the mail type names.
                var luaDir = c.Text("lua", @"C:\Temp\LWC\apk\lua");
                foreach (var (file, global, name) in new[] { ("DataCenter__MailData__MailEnum.luac", "MailType", "mail_types"), ("Global__EnumType.luac", "RewardType", "reward_types") })
                {
                    var path = Path.Combine(luaDir, file);
                    if (!File.Exists(path)) { rows.Add(["enum", name, 0, $"{file} not found"]); continue; }
                    var types = LuaTableReader.Evaluate(LuaReader.LoadFile(path), lenient: true)?[global];
                    File.WriteAllText(Path.Combine(outDir, "tables", $"{name}.json"), types?.ToJsonString() ?? "{}");
                    rows.Add(["enum", name, (types as JsonObject)?.Count ?? 0, file]);
                }

                var font = zip.Entries.FirstOrDefault(e => e.Name.Contains("emojifont", StringComparison.OrdinalIgnoreCase));
                if (font is not null)
                {
                    font.ExtractToFile(Path.Combine(outDir, "lwemoji.ttf"), overwrite: true);
                    rows.Add(["emoji font", "", font.Length, font.FullName]);
                }
                return Task.FromResult(OperationResult.Table($"assets written to {outDir}", ["what", "language", "count / bytes", "from"], rows));
            },
            needsSession: false,
            ps:
            [
                new OperationParam("apk", "Install-time pack", "text", @"C:\Temp\LWC\apk\split_install_time_pack.apk"),
                new OperationParam("languages", "Languages", "text", "en", Help: "Comma list, e.g. en,de,fr."),
                new OperationParam("lua", "Unpacked Lua folder", "text", @"C:\Temp\LWC\apk\lua"),
            ]);
    }

    static Dictionary<string, string> ReadLocale(Stream bin)
    {
        using var gz = new GZipStream(bin, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        ms.Position = 0;
        using var r = new BinaryReader(ms, System.Text.Encoding.UTF8);
        r.ReadInt32();   // format version (2)
        var outp = new Dictionary<string, string>();
        while (ms.Position < ms.Length) outp[r.ReadString()] = r.ReadString();
        return outp;
    }
}
