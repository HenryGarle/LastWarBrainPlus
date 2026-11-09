using System.Security.Cryptography;
using System.Text;
using LastWar.Client.Auth;

namespace LastWar.Tests;

/// <summary>
/// The GSL envelope fails silently when it is wrong - the gateway just refuses - so the
/// three details that are easy to get wrong are pinned here.
/// </summary>
public class GslCryptoTests
{
    static RSA NewKey() => RSA.Create(2048);

    [Fact]
    public void RoundTripsItsOwnEnvelope()
    {
        var key = NewKey();
        var crypto = new GslCrypto(key);
        const string form = "uuid=abc_n3d&airKey=lwDid_YWJj&loginFlag=1&opt=new";

        var (uuid, data) = crypto.EncryptRequest(form);

        // The salt travels RSA-encrypted; recovering it proves the padding and that the
        // AES key is derived from exactly those bytes.
        var salt = Encoding.ASCII.GetString(
            key.Decrypt(GslCrypto.FromUrlSafeB64(uuid), RSAEncryptionPadding.Pkcs1));
        Assert.Equal(20, salt.Length);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.ASCII.GetBytes(
            Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(salt))).ToLowerInvariant());
        Assert.Equal(form, Encoding.UTF8.GetString(
            aes.DecryptEcb(GslCrypto.FromUrlSafeB64(data), PaddingMode.PKCS7)));
    }

    [Fact]
    public void ResponseDecryptsUnderTheSameSalt()
    {
        var crypto = new GslCrypto(NewKey());
        // The response comes back under the salt the request generated, so the ciphertext
        // this produced must decode again through the same instance.
        var (_, data) = crypto.EncryptRequest("code=0&msg=ok");
        Assert.Equal("code=0&msg=ok", crypto.DecryptResponse(data));
    }

    [Fact]
    public void UrlSafeBase64HasNoPadding()
    {
        var encoded = GslCrypto.UrlSafeB64([0xfb, 0xff, 0xfe, 0x00, 0x01]);
        Assert.DoesNotContain('=', encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.Equal([0xfb, 0xff, 0xfe, 0x00, 0x01], GslCrypto.FromUrlSafeB64(encoded));
    }

    [Fact]
    public void AirKeyIsStandardBase64NotTheUrlSafeKind()
    {
        // Standard base64, unlike the uuid/data/bin fields, and whatever it produces goes
        // into the plaintext form untouched. A 36-char device id happens to need no
        // padding, but a differently sized one does and the '=' must survive.
        const string deviceId = "0123456789abcdef0123456789abcdef_n3d";
        var airKey = GslCrypto.AirKey(deviceId);
        Assert.StartsWith("lwDid_", airKey);
        Assert.Equal("lwDid_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(deviceId)), airKey);
        Assert.DoesNotContain('-', airKey[6..]);
        Assert.DoesNotContain('_', airKey[6..]);

        Assert.EndsWith("=", GslCrypto.AirKey("abcd_n3d"));   // 8 bytes: padding required
    }

    [Fact]
    public void GuestDeviceIdLooksLikeTheClientsFallback()
    {
        var id = GuestBootstrap.NewDeviceId();
        Assert.EndsWith("_n3d", id);
        Assert.Equal(36, id.Length);
        Assert.True(id[..32].All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
    }
}
