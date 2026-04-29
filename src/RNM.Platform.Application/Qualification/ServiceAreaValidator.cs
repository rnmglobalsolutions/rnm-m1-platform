namespace RNM.Platform.Application.Qualification;

public sealed class ServiceAreaValidator
{
    public bool IsInServiceArea(string? zipCode, IReadOnlyCollection<string> configuredZipCodes)
    {
        return Validate(zipCode, configuredZipCodes).State is ServiceAreaDecisionState.InServiceArea;
    }

    public ServiceAreaDecision Validate(string? zipCode, IReadOnlyCollection<string> configuredZipCodes)
    {
        if (string.IsNullOrWhiteSpace(zipCode))
        {
            return ServiceAreaDecision.MissingZipCode();
        }

        var normalizedZipCode = NormalizeZipCode(zipCode);
        if (!IsValidUsZipCode(normalizedZipCode))
        {
            return ServiceAreaDecision.InvalidZipCode();
        }

        if (configuredZipCodes.Count == 0)
        {
            return ServiceAreaDecision.NeedsEscalation();
        }

        var isAllowed = configuredZipCodes
            .Select(NormalizeZipCode)
            .Contains(normalizedZipCode, StringComparer.Ordinal);

        return isAllowed
            ? ServiceAreaDecision.InServiceArea()
            : ServiceAreaDecision.OutOfServiceArea();
    }

    public static string NormalizeZipCode(string zipCode)
    {
        return zipCode.Trim();
    }

    public static bool IsValidUsZipCode(string zipCode)
    {
        return zipCode.Length == 5 && zipCode.All(char.IsDigit);
    }
}
