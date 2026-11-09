namespace LastWar.Client.Auth;

/// <summary>
/// The phone a session says it is, in the shape Unity reports it (SystemInfo.deviceModel / .operatingSystem, which
/// the app sends as phone_model / osVersion). Picked 2026-10-08 (Henry: "the most common modern iPhone / a common
/// Android"): iPhone 17 (best-selling phone of 2026) and Galaxy S26 Ultra (best-selling Android, Q2 2026).
/// A session saved before this has no profile and keeps sending what it always sent ("GoClient"): a known account
/// suddenly changing phone is its own signal. Guests and new sign-ins get a profile.
/// </summary>
public sealed record DeviceProfile(string Key, string Model, string Os, string Manufacturer, string OsNumber)
{
    public static readonly DeviceProfile Android = new("android", "samsung SM-S948B",
        "Android OS 16 / API-36 (BP2A.250605.031.A3/S948BXXS2AZF1)", "samsung", "16");
    public static readonly DeviceProfile IPhone = new("iphone", "iPhone18,3", "iOS 26.5", "Apple", "26.5");

    /// <summary>The profile a session config names ("android" / "iphone"), or null for the old fixed values.</summary>
    public static DeviceProfile? Of(string? key) => key switch { "android" => Android, "iphone" => IPhone, _ => null };

    public static DeviceProfile For(bool ios) => ios ? IPhone : Android;
}
