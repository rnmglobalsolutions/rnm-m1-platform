namespace RNM.Platform.SharedKernel.Correlation;

public readonly record struct CorrelationId(string Value)
{
    public const string HeaderName = "x-correlation-id";
    public const int MaxLength = 128;

    public static CorrelationId New() => new(Guid.NewGuid().ToString("N"));

    public static CorrelationId FromStringOrNew(string? value)
    {
        return TryNormalize(value, out var normalizedValue)
            ? new CorrelationId(normalizedValue)
            : New();
    }

    public static bool TryNormalize(string? value, out string normalizedValue)
    {
        normalizedValue = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmedValue = value.Trim();
        if (trimmedValue.Length is 0 or > MaxLength)
        {
            return false;
        }

        foreach (var character in trimmedValue)
        {
            if (!IsSafeCharacter(character))
            {
                return false;
            }
        }

        normalizedValue = trimmedValue;
        return true;
    }

    private static bool IsSafeCharacter(char character)
    {
        return char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':';
    }

    public override string ToString() => Value;
}
