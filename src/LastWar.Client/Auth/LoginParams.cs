using System.Security.Cryptography;
using System.Text;
using LastWar.Protocol.Sfs;

namespace LastWar.Client.Auth;

/// <summary>
/// Builds the "p" block of the SFS login request. Every field below was established
/// against the live server; the shape is deliberately faithful rather than tidy,
/// because the server rejects logins that do not look like the real client.
///
/// IMPORTANT: iosMode must match how the account was originally issued. An
/// iOS-issued account logging in with Android fields fails with ec=28 / E011.
/// </summary>
public static class LoginParams
{
    public const string AndroidPackage = "com.fun.lastwar.gp";
    public const string IosPackage = "com.lastwar.ios";
    public const string AndroidAppVersion = "1.0.351";
    public const string AndroidVersionCode = "1835";
    public const string IosAppVersion = "1.0.344";
    public const string IosVersionCode = "786";

    const string SecuritySalt = "4d1c383ccbedf3d98320d6ea06d8dedc";

    /// <summary>The APK signing certificate, hashed into the "psh" field.</summary>
    const string ApkCertHex =
        "3082029c30820184a00302010202046fdfb4c4300d06092a864886f70d0101050500300f310d300b060355040a0c044c4359443020170d3233303131363133323131335a180f32303733303130333133323131335a300f310d300b060355040a0c044c43594430820122300d06092a864886f70d01010105000382010f003082010a028201010089ef3aa54cf5a75c83d18c37694482552227620de97dbce3747d0d820b94400a322a65ad61b8360deaf036a9755a4058a6eb01388f6fcb3b061bea3730bc401022471739ebcbe3337c3a67e211c5f7a909397198a6824a0f71a9d8c715575b6529055d922fe7e007ce71b16098c491bdea6f4767ac2e2e7e6cbaa5fbb11c55ffb6561a895c3e7437cb41697db019005473d9f77f4430b163848231ec4903259949b489d5eb436f9cbf4206fcbf040d8c6da6300e30edfeecb5619abfb79dbab2837b4adddba51582a5c85e50568ae32355eba1f974f5567dbb4ac8afdd9ce474f6555e9f5f1085b82072052a08a772db1ee943615b38833dd1e95f0c827ffeed0203010001300d06092a864886f70d01010505000382010100488695eee865bc3992e9dcfa86a19719eb753b7abcd7a5d7ddfd9471229136b37bf08bce8520a98280c0915202ea5a6530f12f81fc6b85769685d8049e73f94d7584ad5e62babc1f9c8c34617f2856d40f7592dad213a0b29edcb5c6ef9179e2d220fd4d83ed42bd6b4f0e430bbcb970db89b352aac9a41f90b15dcb1f76f3efd8e6d104fc23c71c77d50448d9b7666ca182210a33d95b7786a2400f20ff6b9bb33a464bd1134f14eb6a4d6c169d526c2875a5ba0dca2f1f8b8920d1eb3dabfa7977f35fd348eba2a72ee7b052dd8618d5a50b84c25a5e7afdeba271c5eff310d49b934bd8b80724dd7784f35550cfe72c6e0a00140bf50f45acbe1a7eafc2ff";

    public sealed record Input
    {
        public int FutureId { get; init; } = 1;
        public required string DeviceId { get; init; }
        public required string AirKey { get; init; }
        public string GameUid { get; init; } = "";
        public string AccessToken { get; init; } = "";
        public string ServerId { get; init; } = "";
        public string ShumeiBoxId { get; init; } = "";
        public bool IosMode { get; init; }
        /// <summary>Null = the old fixed values (phone_model "GoClient"), kept for sessions saved before profiles.</summary>
        public DeviceProfile? Device { get; init; }
    }

    public static SfsObject Build(Input input)
    {
        var cmdBaseTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var (oneCode, coreV) = OneCodeAndCoreV();
        var appVersion = input.IosMode ? IosAppVersion : AndroidAppVersion;
        var versionCode = input.IosMode ? IosVersionCode : AndroidVersionCode;
        var package = input.IosMode ? IosPackage : AndroidPackage;

        var p = new SfsObject();
        p.PutInt("_id", input.FutureId);
        p.PutInt("netType", 2);                       // 2 = wifi
        p.PutUtfString("ta", input.IosMode ? IosAnalyticsBlob(appVersion, versionCode, input.ServerId, input.Device) : "{}");
        p.PutUtfString("distinct_id", "");
        p.PutUtfString("phone_screen", "1920*1080");
        p.PutObject("configVersion", new SfsObject());

        if (input.GameUid.Length == 0)
        {
            p.PutInt("country", 1);
            p.PutBool("suggestCountry", false);
            p.PutUtfString("timeoffset", "0");
            p.PutUtfString("gcmRegisterId", "");
            p.PutUtfString("referrer", "");
        }
        if (!input.IosMode)
        {
            p.PutUtfString("AndroidID", "");
            p.PutUtfString("IMEI", "");
        }

        p.PutUtfString("psh", GameIdentity.Md5Hex(cmdBaseTime + ApkCertHex));
        p.PutUtfString("mt", "");
        p.PutUtfString("deviceId", input.DeviceId);
        p.PutUtfString("airKey", input.AirKey);
        p.PutUtfString("cmdBaseTime", cmdBaseTime);
        p.PutUtfString("SecurityCode", GameIdentity.Md5Hex(cmdBaseTime + SecuritySalt + input.GameUid));
        p.PutUtfString("OneCode", oneCode);
        p.PutUtfString("CoreV", coreV);
        p.PutUtfString("googlePlay", "");
        p.PutUtfString("androidDid", "");
        p.PutUtfString("googleName", "");
        p.PutUtfString("deeplinkParams", "");
        p.PutUtfString("pfId", "");
        if (!input.IosMode) p.PutInt("google_available", 0);   // Android-only key
        p.PutUtfString("fromCountry", "US");
        p.PutUtfString("dataConfigMd5", "");
        if (input.IosMode)
        {
            p.PutUtfString("idfa", "");
            p.PutUtfString("idfv", "");
            p.PutUtfString("phone_native_screen", "720*1280");
        }
        p.PutUtfString("packageName", package);
        p.PutUtfString("packageSign", GameIdentity.Sha1Hex(package));
        p.PutUtfString("platform", input.IosMode ? "0" : "1");
        p.PutInt("lat", 1);
        p.PutUtfString("pf", input.IosMode ? "AppStore" : "market_global");
        p.PutUtfString("firebaseId", "");
        p.PutUtfString("afuid", "");
        p.PutUtfString("phone_model", input.Device?.Model ?? "GoClient");
        p.PutInt("configNumber", 0);
        p.PutUtfString("gaid", "");
        p.PutUtfString("osVersion", input.Device?.Os ?? "GoClient/1.0");
        p.PutUtfString("parseRegisterId", "");
        p.PutUtfString("gameUid", input.GameUid);
        p.PutUtfString("appVersion", appVersion);
        p.PutUtfString("resVersion", "0");
        p.PutUtfString("versionCode", versionCode);
        p.PutUtfString("lang", "en");
        p.PutUtfString("serverId", input.ServerId);
        p.PutInt("gmLogin", 0);
        p.PutUtfString("KCPMode", "0");
        p.PutInt("forbidden_froce_merge", 1);          // sic: the server's own spelling
        p.PutUtfString("shumeiBoxId", input.ShumeiBoxId);
        p.PutUtfString("simOp", "");
        p.PutUtfString("simOpName", "");
        p.PutInt("delete_account_status", 0);
        p.PutBool("isUseLz4", true);                   // enables the Zstd receive path
        if (input.AccessToken.Length > 0) p.PutUtfString("at", input.AccessToken);
        return p;
    }

    /// <summary>
    /// Two interleaved hashes the client sends as a weak integrity check. Reproduced
    /// exactly: OneCode interleaves md5(r) with r, CoreV interleaves md5(md5(reverse(
    /// base64(r))) + r) with md5(reverse(base64(r))), where r is 32 random digits.
    /// </summary>
    public static (string OneCode, string CoreV) OneCodeAndCoreV()
    {
        var r = RandomDigits(32);
        var md5R = GameIdentity.Md5Hex(r);
        var one = new StringBuilder(64);
        for (var i = 0; i < 32; i++) { one.Append(md5R[i]); one.Append(r[i]); }

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(r));
        var reversed = new string(b64.Reverse().ToArray());
        var md2 = GameIdentity.Md5Hex(reversed);
        var md3 = GameIdentity.Md5Hex(md2 + r);
        var core = new StringBuilder(64);
        for (var j = 0; j < 32; j++) { core.Append(md3[j]); core.Append(md2[j]); }

        return (one.ToString(), core.ToString());
    }

    /// <summary>
    /// The analytics body an iOS client posts as "ta". Sending "{}" instead makes an
    /// iOS-issued account fail cross-server login with ec=28 / ep=[E005], so the shape
    /// matters even though every value in it is a placeholder.
    /// </summary>
    static string IosAnalyticsBlob(string appVersion, string versionCode, string serverId, DeviceProfile? device)
    {
        const string ZeroGuid = "00000000-0000-0000-0000-000000000000";
        var blob = new Dictionary<string, object?>
        {
            ["#os"] = "iOS",
            ["#disk"] = "50.0/100.0",
            ["#device_id"] = ZeroGuid,
            ["#os_version"] = device?.OsNumber ?? "0.0",
            ["#system_language"] = "en",
            ["#bundle_id"] = IosPackage,
            ["#carrier"] = "",
            ["#app_version"] = appVersion,
            ["#fps"] = 0,
            ["#ram"] = "0.0/0.0",
            ["#simulator"] = false,
            ["#zone_offset"] = 0,
            ["#manufacturer"] = "Apple",
            ["#screen_height"] = 0,
            ["#network_type"] = "WIFI",
            ["#device_model"] = device?.Model ?? "unknown",
            ["#screen_width"] = 0,
            ["#install_time"] = "2000-01-01 00:00:00.000",
            ["lw_net"] = "wifi",
            ["lw_res_version"] = "0.0",
            ["lw_buildcode"] = versionCode,
            ["lw_line"] = "",
            ["pd_dl"] = 0,
            ["lw_ab"] = "0",
            ["lw_platform"] = "AppStore",
            ["lw_game_session_id"] = ZeroGuid,
            ["lw_first_launch"] = false,
            ["lw_alliance_id"] = new string('0', 32),
            ["lw_device_id"] = "",
            ["lw_device_level"] = 0,
            ["lw_main_level"] = 0,
            ["lw_shumei_id"] = "",
            ["lw_airKey"] = "",
            ["lw_version"] = "0.0.0",
            ["lw_power"] = 0,
            ["lw_zone"] = "APS" + serverId,
        };
        return System.Text.Json.JsonSerializer.Serialize(blob);
    }

    static string RandomDigits(int n)
    {
        var sb = new StringBuilder(n);
        for (var i = 0; i < n; i++) sb.Append((char)('0' + RandomNumberGenerator.GetInt32(10)));
        return sb.ToString();
    }
}
