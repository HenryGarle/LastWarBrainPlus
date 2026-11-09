using LastWar.Client.Auth;

namespace LastWar.Tests;

public class LoginParamsTests
{
    static LoginParams.Input Base(bool ios) => new()
    {
        DeviceId = "11111111-2222-3333-4444-555555555555",
        AirKey = "lwDid_abc",
        GameUid = "2359053554001967",
        AccessToken = "token",
        ServerId = "1967",
        IosMode = ios,
    };

    [Fact]
    public void AndroidAndIosSendDifferentPackageAndPlatform()
    {
        var android = LoginParams.Build(Base(ios: false));
        var ios = LoginParams.Build(Base(ios: true));

        Assert.Equal(LoginParams.AndroidPackage, android.GetString("packageName"));
        Assert.Equal("1", android.GetString("platform"));
        Assert.Equal("market_global", android.GetString("pf"));

        Assert.Equal(LoginParams.IosPackage, ios.GetString("packageName"));
        Assert.Equal("0", ios.GetString("platform"));
        Assert.Equal("AppStore", ios.GetString("pf"));
    }

    /// <summary>
    /// An iOS-issued account fails cross-server login with ec=28 / ep=[E005] when "ta"
    /// is empty. Found live: the blob must be populated and must name the zone.
    /// </summary>
    [Fact]
    public void IosModeSendsAPopulatedAnalyticsBlob()
    {
        var ta = LoginParams.Build(Base(ios: true)).GetString("ta");

        Assert.Contains("\"lw_zone\":\"APS1967\"", ta);
        Assert.Contains("\"#bundle_id\":\"com.lastwar.ios\"", ta);
        Assert.Equal("{}", LoginParams.Build(Base(ios: false)).GetString("ta"));
    }

    [Fact]
    public void PlatformOnlyKeysAreOmittedOnTheOtherPlatform()
    {
        var ios = LoginParams.Build(Base(ios: true));
        var android = LoginParams.Build(Base(ios: false));

        Assert.False(ios.Has("google_available"));
        Assert.False(ios.Has("AndroidID"));
        Assert.True(ios.Has("idfa"));

        Assert.True(android.Has("google_available"));
        Assert.False(android.Has("idfa"));
    }

    [Fact]
    public void IntegrityCodesAreSixtyFourCharsAndVaryPerCall()
    {
        var (one1, core1) = LoginParams.OneCodeAndCoreV();
        var (one2, _) = LoginParams.OneCodeAndCoreV();

        Assert.Equal(64, one1.Length);
        Assert.Equal(64, core1.Length);
        Assert.NotEqual(one1, one2);
    }

    [Fact]
    public void AccessTokenIsOmittedWhenAbsent()
    {
        Assert.False(LoginParams.Build(Base(ios: false) with { AccessToken = "" }).Has("at"));
        Assert.Equal("token", LoginParams.Build(Base(ios: false)).GetString("at"));
    }

    [Fact]
    public void AirKeyIsBase64OfDeviceId() =>
        Assert.Equal("lwDid_YWJj", new GameIdentity { DeviceId = "abc" }.AirKey);

    [Fact]
    public void SessionConfigTakesTheFirstFallbackHost()
    {
        var cfg = new SessionConfig { Ip = "a.example|b.example|c.example", Port = 11967 };

        Assert.Equal("a.example", cfg.PrimaryHost);
    }
}
