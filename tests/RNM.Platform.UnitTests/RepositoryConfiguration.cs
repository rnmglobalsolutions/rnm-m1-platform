using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.LeadIntake;
using RNM.Platform.Application.Observability;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Infrastructure.Configuration;

namespace RNM.Platform.UnitTests;

/// <summary>
/// Loads the checked-in /config files so tests exercise the rules that actually ship.
/// </summary>
internal static class RepositoryConfiguration
{
    public static string ConfigRoot { get; } = Path.Combine(FindRepositoryRoot(), "config");

    public static JsonVerticalConfigurationProvider Verticals() => new(ConfigRoot, new ConfigurationValidator());

    public static Task<VerticalConfiguration> LoadVerticalAsync(string verticalId) =>
        Verticals().GetVerticalConfigurationAsync(verticalId, CancellationToken.None);

    /// <summary>
    /// Classifier whose tenants all resolve to the given checked-in vertical, regardless of the tenant's vertical id.
    /// </summary>
    public static LeadClassifier Classifier(
        ITenantConfigurationProvider tenants,
        IEventLogger eventLogger,
        string verticalId = "life-insurance") =>
        new(tenants, new FixedVerticalProvider(verticalId), eventLogger);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RNM.Platform.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class FixedVerticalProvider(string verticalId) : IVerticalConfigurationProvider
    {
        public Task<VerticalConfiguration> GetVerticalConfigurationAsync(string requestedVerticalId, CancellationToken cancellationToken) =>
            Verticals().GetVerticalConfigurationAsync(verticalId, cancellationToken);
    }
}
