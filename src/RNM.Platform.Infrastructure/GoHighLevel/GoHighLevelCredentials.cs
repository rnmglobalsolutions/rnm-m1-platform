using System.Text.Json;

namespace RNM.Platform.Infrastructure.GoHighLevel;

internal sealed record GoHighLevelCredentials(
    string AccessToken,
    string? LocationId,
    string? CalendarId,
    string ApiVersion)
{
    private const string DefaultApiVersion = "2021-07-28";

    public static bool TryParse(string secretValue, out GoHighLevelCredentials credentials)
    {
        credentials = new GoHighLevelCredentials(string.Empty, null, null, DefaultApiVersion);
        if (string.IsNullOrWhiteSpace(secretValue))
        {
            return false;
        }

        var trimmed = secretValue.Trim();
        if (!trimmed.StartsWith('{'))
        {
            credentials = new GoHighLevelCredentials(trimmed, null, null, DefaultApiVersion);
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            var accessToken = ReadString(root, "accessToken")
                ?? ReadString(root, "apiKey")
                ?? ReadString(root, "token");

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return false;
            }

            credentials = new GoHighLevelCredentials(
                accessToken,
                ReadString(root, "locationId"),
                ReadString(root, "calendarId"),
                ReadString(root, "apiVersion") ?? DefaultApiVersion);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;
    }
}
