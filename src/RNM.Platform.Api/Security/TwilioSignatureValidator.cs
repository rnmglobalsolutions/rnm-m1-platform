using System.Security.Cryptography;
using System.Text;

namespace RNM.Platform.Api.Security;

public sealed class TwilioSignatureValidator
{
    public bool IsValid(
        string? signatureHeader,
        Uri requestUri,
        IEnumerable<KeyValuePair<string, string>> formValues,
        string authToken)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)
            || !requestUri.IsAbsoluteUri
            || string.IsNullOrWhiteSpace(authToken))
        {
            return false;
        }

        var signatureBase = BuildSignatureBase(requestUri, formValues);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        var computedSignature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureBase)));

        return ApiKeyRequestValidator.FixedTimeEquals(signatureHeader, computedSignature);
    }

    public string BuildSignatureBase(Uri requestUri, IEnumerable<KeyValuePair<string, string>> formValues)
    {
        var builder = new StringBuilder(requestUri.ToString());
        foreach (var pair in formValues.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key);
            builder.Append(pair.Value);
        }

        return builder.ToString();
    }
}
