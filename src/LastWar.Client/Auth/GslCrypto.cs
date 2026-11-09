using System.Security.Cryptography;
using System.Text;

namespace LastWar.Client.Auth;

/// <summary>
/// The envelope every GSL call is wrapped in. Reproduces AESHelper:
///
///   salt = 20 random chars from a fixed alphabet
///   uuid = urlsafe_b64( RSA_PKCS1v15( salt ) )        - RSA covers the salt, nothing else
///   data = urlsafe_b64( AES256_ECB_PKCS7( form ) )    - key = the 32 ASCII bytes of md5hex(salt)
///
/// Three things here are easy to get wrong and all of them fail silently:
/// the AES key is the lowercase hex STRING's bytes, not the 16-byte digest; the mode is
/// ECB with no IV, not CBC; and the RSA padding is PKCS#1 v1.5, not OAEP. The same salt
/// must still be in scope to decrypt the response.
/// </summary>
public sealed class GslCrypto(RSA publicKey)
{
    const string SaltAlphabet =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()_+-/=<>?{}[]";

    string _salt = "";

    public (string Uuid, string Data) EncryptRequest(string plainForm)
    {
        _salt = RandomSalt(20);
        var uuid = UrlSafeB64(publicKey.Encrypt(Encoding.ASCII.GetBytes(_salt), RSAEncryptionPadding.Pkcs1));
        var data = UrlSafeB64(Transform(Encoding.UTF8.GetBytes(plainForm), _salt, encrypt: true));
        return (uuid, data);
    }

    public string DecryptResponse(string bin)
    {
        if (_salt.Length == 0) throw new InvalidOperationException("no salt in scope - call EncryptRequest first");
        return Encoding.UTF8.GetString(Transform(FromUrlSafeB64(bin), _salt, encrypt: false));
    }

    static byte[] Transform(byte[] input, string salt, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Md5HexKey(salt);
        return encrypt
            ? aes.EncryptEcb(input, PaddingMode.PKCS7)
            : aes.DecryptEcb(input, PaddingMode.PKCS7);
    }

    /// <summary>md5 of the salt, lowercase hex, and it is those 32 ASCII characters that are the key.</summary>
    static byte[] Md5HexKey(string salt) =>
        Encoding.ASCII.GetBytes(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(salt))).ToLowerInvariant());

    static string RandomSalt(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = SaltAlphabet[RandomNumberGenerator.GetInt32(SaltAlphabet.Length)];
        return new string(chars);
    }

    public static string UrlSafeB64(byte[] b) =>
        Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static byte[] FromUrlSafeB64(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight((t.Length + 3) / 4 * 4, '='));
    }

    /// <summary>
    /// airKey is STANDARD padded base64, unlike everything else here. The trailing '='
    /// goes into the plaintext form untouched - stripping it breaks the call.
    /// </summary>
    public static string AirKey(string deviceId) =>
        "lwDid_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(deviceId));

    public static RSA ParsePublicKey(string base64Der)
    {
        var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64Der), out _);
        return rsa;
    }
}
