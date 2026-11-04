using System.Security.Cryptography;
using System.Text;

namespace RNM.Platform.Api.Security;

/// <summary>
/// Verifies SendGrid Signed Event Webhook requests: ECDSA P-256 over SHA-256 of (timestamp + raw body),
/// signature base64 DER, public key as shown in SendGrid (base64 SubjectPublicKeyInfo, PEM headers optional).
/// </summary>
public sealed class SendGridEventWebhookValidator
{
    public const string SignatureHeader = "X-Twilio-Email-Event-Webhook-Signature";
    public const string TimestampHeader = "X-Twilio-Email-Event-Webhook-Timestamp";
    public const string VerificationKeySecretName = "rnm-sendgrid-webhook-verification-key";
    public static readonly TimeSpan MaxTimestampAge = TimeSpan.FromMinutes(10);

    public bool IsValid(
        string publicKey,
        string? signature,
        string? timestamp,
        byte[] body,
        DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(publicKey)
            || string.IsNullOrWhiteSpace(signature)
            || !long.TryParse(timestamp, out var unixSeconds))
        {
            return false;
        }

        // Replay protection: SendGrid signs the send time, so an old captured request is rejected.
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if ((utcNow - signedAt).Duration() > MaxTimestampAge)
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(StripPem(publicKey)), out _);
            var timestampBytes = Encoding.UTF8.GetBytes(timestamp!);
            var signedData = new byte[timestampBytes.Length + body.Length];
            timestampBytes.CopyTo(signedData, 0);
            body.CopyTo(signedData, timestampBytes.Length);
            return ecdsa.VerifyData(
                signedData,
                Convert.FromBase64String(signature.Trim()),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static string StripPem(string key) =>
        string.Concat(key
            .Replace("-----BEGIN PUBLIC KEY-----", string.Empty, StringComparison.Ordinal)
            .Replace("-----END PUBLIC KEY-----", string.Empty, StringComparison.Ordinal)
            .Where(character => !char.IsWhiteSpace(character)));
}
