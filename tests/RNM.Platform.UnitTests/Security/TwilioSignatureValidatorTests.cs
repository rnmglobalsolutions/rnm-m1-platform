using System.Security.Cryptography;
using System.Text;
using RNM.Platform.Api.Security;
using Xunit;

namespace RNM.Platform.UnitTests.Security;

public sealed class TwilioSignatureValidatorTests
{
    [Fact]
    public void IsValid_ReturnsTrue_WhenSignatureMatchesRequestUrlAndAllFormValues()
    {
        var validator = new TwilioSignatureValidator();
        var requestUri = new Uri("https://example.com/webhooks/twilio/sms-status");
        KeyValuePair<string, string>[] formValues =
        [
            new("MessageStatus", "delivered"),
            new("MessageSid", "SM123"),
            new("To", "+15551234567")
        ];
        var signature = ComputeTwilioSignature(
            validator.BuildSignatureBase(requestUri, formValues),
            "auth-token");

        var result = validator.IsValid(signature, requestUri, formValues, "auth-token");

        Assert.True(result);
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenAnyFormValueChanges()
    {
        var validator = new TwilioSignatureValidator();
        var requestUri = new Uri("https://example.com/webhooks/twilio/sms-status");
        KeyValuePair<string, string>[] originalValues =
        [
            new("MessageStatus", "delivered"),
            new("MessageSid", "SM123")
        ];
        KeyValuePair<string, string>[] tamperedValues =
        [
            new("MessageStatus", "failed"),
            new("MessageSid", "SM123")
        ];
        var signature = ComputeTwilioSignature(
            validator.BuildSignatureBase(requestUri, originalValues),
            "auth-token");

        var result = validator.IsValid(signature, requestUri, tamperedValues, "auth-token");

        Assert.False(result);
    }

    private static string ComputeTwilioSignature(string signatureBase, string authToken)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureBase)));
    }
}
