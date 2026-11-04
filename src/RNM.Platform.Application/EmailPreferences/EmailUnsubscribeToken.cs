using System.Security.Cryptography;
using System.Text;

namespace RNM.Platform.Application.EmailPreferences;

/// <summary>
/// Signed, tenant-scoped unsubscribe links. The token proves the link was issued by M1 for this tenant and
/// address, so the public unsubscribe endpoint needs no login and cannot be used to unsubscribe others.
/// </summary>
public static class EmailUnsubscribeToken
{
    public const string SigningKeySecretName = "rnm-email-unsubscribe-signing-key";

    public static string Create(string signingKey, string tenantId, string email) =>
        Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Payload(tenantId, email)));

    public static bool IsValid(string signingKey, string tenantId, string email, string? token)
    {
        if (string.IsNullOrWhiteSpace(signingKey) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Create(signingKey, tenantId, email));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(token));
    }

    public static string EncodeEmail(string email) => Base64UrlEncode(Encoding.UTF8.GetBytes(NormalizeEmail(email)));

    public static string? DecodeEmail(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 512)
        {
            return null;
        }

        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            return NormalizeEmail(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static byte[] Payload(string tenantId, string email) =>
        Encoding.UTF8.GetBytes($"{tenantId}\n{NormalizeEmail(email)}");

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
