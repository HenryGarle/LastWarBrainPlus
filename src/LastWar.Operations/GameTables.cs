using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LastWar.Client.Auth;
using LastWar.Lua;

namespace LastWar.Operations;

/// <summary>
/// The game's config tables, kept current the way the app does it on every launch (no account, no login):
/// getlsu3dversion.php with table_env=table_online names the live table ("39516,19518361,&lt;md5&gt;,&lt;crc&gt;;patches..."),
/// and the app fetches &lt;cdn&gt;/hotupdate/table/table_&lt;version&gt;_&lt;md5&gt;.data (ClientConfig / TableFileUpdate in the APK).
/// The file is a zip of Lua constant chunks - the same format as the copy inside the APK (assets.extract).
/// Each version downloaded is kept under data/assets/downloads; tables/*.json are rewritten from the newest.
/// The database keeps every row it has ever seen (upserts), so past SvS line-ups stay after the game drops them.
/// </summary>
public static class GameTables
{
    static readonly string[] Cdns = ["https://lastwar-cdn.akamaized.net/hotupdate/", "https://lastwar-cdn.lastwarapp.net/hotupdate/", "https://cdn.lastwar.com/hotupdate/"];
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>Downloads the live table if it is newer than the one extracted; returns a line for the log.</summary>
    public static async Task<string> UpdateAsync(string dataDir, CancellationToken ct)
    {
        var assets = Path.Combine(dataDir, "assets");
        var stamp = Path.Combine(assets, "tables", "_table_version.txt");
        var (version, size, md5) = await LiveVersionAsync(ct);
        var name = $"table_{version}_{md5}.data";
        if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == name) return $"tables already at {version}";

        var file = Path.Combine(assets, "downloads", name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file))
        {
            byte[]? bytes = null;
            foreach (var cdn in Cdns)
            {
                try { bytes = await Http.GetByteArrayAsync(cdn + "table/" + name, ct); break; }
                catch (HttpRequestException) { }
            }
            if (bytes is null) throw new InvalidOperationException($"no CDN served {name}");
            if ((ulong)bytes.Length != size || Convert.ToHexStringLower(MD5.HashData(bytes)) != md5)
                throw new InvalidOperationException($"{name}: got {bytes.Length} bytes, md5 {Convert.ToHexStringLower(MD5.HashData(bytes))} - not what the version check promised");
            if (bytes is not [(byte)'P', (byte)'K', ..])
                throw new InvalidOperationException($"{name} is not a plain zip (encrypted?) - kept, not extracted");
            await File.WriteAllBytesAsync(file, bytes, ct);
        }

        using var zip = ZipFile.OpenRead(file);
        var (ok, failed) = Extract(zip, Path.Combine(assets, "tables"));
        File.WriteAllText(stamp, name);
        return $"tables updated to {version}: {ok} tables{(failed > 0 ? $", {failed} skipped" : "")}";
    }

    /// <summary>The live table's version, size and md5, from the same version check the app makes at launch.</summary>
    static async Task<(int Version, ulong Size, string Md5)> LiveVersionAsync(CancellationToken ct)
    {
        Exception? last = null;
        foreach (var host in GslClient.Hosts)
        {
            try
            {
                // Same parameters, same (ordinal) order as GslClient.CheckVersionAsync; table_env as the app sends it.
                var url = host + "/gameservice/getlsu3dversion.php?appVersion=" + GslClient.AppVersion + "&buildId=" + GslClient.VersionCode
                    + "&deviceId=&gm=0&packageName=" + GslClient.PackageName + "&platform=" + GslClient.Platform
                    + "&returnJson=1&server=&table_env=table_online&uid=&unityVersion=440";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
                var head = doc.RootElement.GetProperty("table_version").GetString()!.Split(';')[0].Split(',');
                return (int.Parse(head[0]), ulong.Parse(head[1]), head[2]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
        }
        throw new InvalidOperationException("no version check answered", last);
    }

    /// <summary>Every plain constant table in a table zip -> tables/&lt;name&gt;.json.</summary>
    public static (int Ok, int Failed) Extract(ZipArchive tables, string outDir)
    {
        Directory.CreateDirectory(outDir);
        int ok = 0, failed = 0;
        foreach (var t in tables.Entries)
        {
            try
            {
                using var ms = new MemoryStream();
                using (var s = t.Open()) s.CopyTo(ms);
                var parsed = LuaTableReader.Rows(LuaTableReader.Evaluate(LuaReader.Load(ms.ToArray())));
                File.WriteAllText(Path.Combine(outDir, $"{t.Name}.json"), new JsonArray([.. parsed]).ToJsonString());
                ok++;
            }
            catch (Exception) { failed++; }
        }
        return (ok, failed);
    }
}
