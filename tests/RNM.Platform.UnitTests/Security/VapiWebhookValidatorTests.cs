using System.Security.Cryptography;
using System.Text;
using RNM.Platform.Api.Security;
using Xunit;

namespace RNM.Platform.UnitTests.Security;

public sealed class VapiWebhookValidatorTests
{
    [Fact]
    public void IsValidBearerToken_ReturnsTrue_WhenBearerTokenMatches()
    {
        var validator = new VapiWebhookValidator();

        var result = validator.IsValidBearerToken("Bearer expected-token", "expected-token");

        Assert.True(result);
    }

    [Fact]
    public void IsValidLegacySecret_ReturnsTrue_WhenSecretMatches()
    {
        var validator = new VapiWebhookValidator();

        var result = validator.IsValidLegacySecret("expected-secret", "expected-secret");

        Assert.True(result);
    }

    [Fact]
    public void IsValidHmacSha256_ReturnsTrue_WhenSignatureMatchesRawBody()
    {
        var validator = new VapiWebhookValidator();
        var rawBody = """{"message":{"type":"call-started"}}""";
        var signature = ComputeVapiSignature(rawBody, "secret");

        var result = validator.IsValidHmacSha256(rawBody, signature, "secret");

        Assert.True(result);
    }

    [Fact]
    public void IsValidHmacSha256_ReturnsFalse_WhenBodyIsTampered()
    {
        var validator = new VapiWebhookValidator();
        var rawBody = """{"message":{"type":"call-started"}}""";
        var signature = ComputeVapiSignature(rawBody, "secret");

        var result = validator.IsValidHmacSha256("""{"message":{"type":"call-ended"}}""", signature, "secret");

        Assert.False(result);
    }

    private static string ComputeVapiSignature(string rawBody, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
