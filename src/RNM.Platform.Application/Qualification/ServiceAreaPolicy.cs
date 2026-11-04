using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Qualification;

/// <summary>
/// Effective service area rule for a tenant: the tenant overrides the vertical; with no mode anywhere the
/// mode is <see cref="ServiceAreaModes.Zip"/>, so tenant JSON written before modes existed behaves as before.
/// </summary>
public sealed record ServiceAreaPolicy(
    string Mode,
    IReadOnlyCollection<string> ZipCodes,
    IReadOnlyCollection<string> States)
{
    /// <summary>
    /// False when the mode needs a list (ZIP codes for zip, states for state) and that list is empty.
    /// </summary>
    public bool IsConfigured => Mode switch
    {
        ServiceAreaModes.None => true,
        ServiceAreaModes.State => States.Count > 0,
        _ => ZipCodes.Count > 0
    };

    public static ServiceAreaPolicy Resolve(
        VerticalConfiguration? vertical,
        TenantConfiguration tenant)
    {
        var mode = FirstValid(tenant.ServiceArea.Mode, vertical?.ServiceArea?.Mode) ?? ServiceAreaModes.Zip;
        var states = tenant.ServiceArea.States is { Count: > 0 }
            ? tenant.ServiceArea.States
            : vertical?.ServiceArea?.States ?? [];

        return new ServiceAreaPolicy(mode, tenant.ServiceArea.ZipCodes, states);
    }

    private static string? FirstValid(params string?[] modes)
    {
        return modes.FirstOrDefault(mode => !string.IsNullOrWhiteSpace(mode))?.Trim();
    }
}
