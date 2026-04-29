namespace RNM.Platform.Application.Observability;

public sealed class SafeTelemetryProperties
{
    public const int MaxValueLength = 256;
    public const string RedactedValue = "[redacted]";

    private static readonly string[] SensitiveNameFragments =
    [
        "authorization",
        "auth",
        "token",
        "secret",
        "password",
        "key",
        "signature",
        "payload",
        "body",
        "raw"
    ];

    private static readonly string[] SensitiveValueFragments =
    [
        "bearer ",
        "basic ",
        "sha256=",
        "authorization:",
        "x-twilio-signature",
        "x-vapi-secret"
    ];

    private readonly Dictionary<string, string> properties = new(StringComparer.Ordinal);

    public SafeTelemetryProperties Add(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(value)
            || IsSensitiveName(name))
        {
            return this;
        }

        var safeValue = SanitizeValue(value);
        if (string.IsNullOrWhiteSpace(safeValue))
        {
            return this;
        }

        properties[name] = safeValue;
        return this;
    }

    public SafeTelemetryProperties AddIf(bool condition, string name, string? value)
    {
        return condition ? Add(name, value) : this;
    }

    public IReadOnlyDictionary<string, string> ToDictionary() => properties;

    public static bool IsSensitiveName(string name)
    {
        return SensitiveNameFragments.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    public static string SanitizeValue(string value)
    {
        var trimmedValue = value.Trim();
        if (ContainsSensitiveMarker(trimmedValue))
        {
            return RedactedValue;
        }

        var sanitizedCharacters = new List<char>(Math.Min(trimmedValue.Length, MaxValueLength));
        foreach (var character in trimmedValue)
        {
            if (sanitizedCharacters.Count >= MaxValueLength)
            {
                break;
            }

            sanitizedCharacters.Add(char.IsControl(character) ? ' ' : character);
        }

        return new string(sanitizedCharacters.ToArray()).Trim();
    }

    private static bool ContainsSensitiveMarker(string value)
    {
        return SensitiveValueFragments.Any(fragment =>
            value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
