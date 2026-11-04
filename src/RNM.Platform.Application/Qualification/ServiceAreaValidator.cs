using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Qualification;

public sealed class ServiceAreaValidator
{
    private const string AnyValidZipCode = "*";

    public bool IsInServiceArea(string? zipCode, IReadOnlyCollection<string> configuredZipCodes)
    {
        return Validate(zipCode, configuredZipCodes).State is ServiceAreaDecisionState.InServiceArea;
    }

    /// <summary>
    /// Validates against the effective mode. Zip mode is the original rule; state mode checks the caller's
    /// state against the allowed USPS codes; none mode needs nothing.
    /// </summary>
    public ServiceAreaDecision Validate(
        string mode,
        string? zipCode,
        IReadOnlyCollection<string> configuredZipCodes,
        string? state,
        IReadOnlyCollection<string> configuredStates)
    {
        return mode switch
        {
            ServiceAreaModes.None => ServiceAreaDecision.NotRequired(),
            ServiceAreaModes.State => ValidateState(state, configuredStates),
            _ => Validate(zipCode, configuredZipCodes)
        };
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

        if (configuredZipCodes
            .Select(NormalizeZipCode)
            .Contains(AnyValidZipCode, StringComparer.Ordinal))
        {
            return ServiceAreaDecision.InServiceArea();
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

    private static ServiceAreaDecision ValidateState(
        string? state,
        IReadOnlyCollection<string> configuredStates)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return ServiceAreaDecision.MissingState();
        }

        if (!UsStates.TryNormalize(state, out var code))
        {
            return ServiceAreaDecision.InvalidState();
        }

        if (configuredStates.Count == 0)
        {
            return ServiceAreaDecision.NeedsEscalation();
        }

        return configuredStates.Contains(code, StringComparer.OrdinalIgnoreCase)
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
