using System.Security.Cryptography;
using System.Text;

namespace RNM.Platform.Api.Security;

public sealed class VapiWebhookValidator
{
    public bool IsValidBearerToken(string? authorizationHeader, string expectedBearerToken)
    {
        const string bearerPrefix = "Bearer ";

        if (string.IsNullOrWhiteSpace(authorizationHeader)
            || string.IsNullOrWhiteSpace(expectedBearerToken)
            || !authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var providedToken = authorizationHeader[bearerPrefix.Length..].Trim();
        return ApiKeyRequestValidator.FixedTimeEquals(providedToken, expectedBearerToken);
    }

    public bool IsValidLegacySecret(string? xVapiSecretHeader, string expectedSecret)
    {
        if (string.IsNullOrWhiteSpace(xVapiSecretHeader) || string.IsNullOrWhiteSpace(expectedSecret))
        {
            return false;
        }

        return ApiKeyRequestValidator.FixedTimeEquals(xVapiSecretHeader, expectedSecret);
    }

    public bool IsValidHmacSha256(string rawBody, string? signatureHeader, string secret)
    {
        if (string.IsNullOrWhiteSpace(rawBody)
            || string.IsNullOrWhiteSpace(signatureHeader)
            || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        var normalizedSignature = signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signatureHeader
            : $"sha256={signatureHeader}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expectedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));
        var expectedSignature = $"sha256={Convert.ToHexString(expectedHash).ToLowerInvariant()}";

        return ApiKeyRequestValidator.FixedTimeEquals(normalizedSignature, expectedSignature);
    }
}
