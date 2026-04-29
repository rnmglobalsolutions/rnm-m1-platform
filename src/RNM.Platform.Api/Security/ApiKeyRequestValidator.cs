using System.Security.Cryptography;
using System.Text;

namespace RNM.Platform.Api.Security;

public sealed class ApiKeyRequestValidator
{
    public bool IsValid(string? providedApiKey, string expectedApiKey)
    {
        if (string.IsNullOrWhiteSpace(providedApiKey) || string.IsNullOrWhiteSpace(expectedApiKey))
        {
            return false;
        }

        return FixedTimeEquals(providedApiKey, expectedApiKey);
    }

    internal static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
