namespace RNM.Platform.Domain.Configuration;

/// <summary>
/// Tenant service area. <paramref name="Mode"/> and <paramref name="States"/> are optional overrides of the
/// vertical's service area; with no mode anywhere the effective mode is <see cref="ServiceAreaModes.Zip"/>.
/// </summary>
public sealed record ServiceAreaConfiguration(
    IReadOnlyCollection<string> ZipCodes,
    IReadOnlyCollection<string> Cities,
    string? ReferralMessage,
    string? Mode = null,
    IReadOnlyCollection<string>? States = null);

/// <summary>
/// Service area declared by a vertical: the mode and, optionally, default allowed states.
/// </summary>
public sealed record VerticalServiceAreaConfiguration(
    string? Mode,
    IReadOnlyCollection<string>? States);

public static class ServiceAreaModes
{
    public const string Zip = "zip";
    public const string State = "state";
    public const string None = "none";

    public static bool IsValid(string? mode)
    {
        return string.Equals(mode, Zip, StringComparison.Ordinal)
            || string.Equals(mode, State, StringComparison.Ordinal)
            || string.Equals(mode, None, StringComparison.Ordinal);
    }
}
