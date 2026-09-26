using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Observability;

namespace RNM.Platform.Application.LeadIntake;

/// <summary>
/// Classifies an inbound lead with the tenant's effective policy (vertical defaults plus tenant overrides).
/// Configuration problems never block intake: the lead is classified with the platform default and the problem is logged.
/// </summary>
public sealed class LeadClassifier
{
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly IVerticalConfigurationProvider verticalConfigurationProvider;
    private readonly IEventLogger eventLogger;

    public LeadClassifier(
        ITenantConfigurationProvider tenantConfigurationProvider,
        IVerticalConfigurationProvider verticalConfigurationProvider,
        IEventLogger eventLogger)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.verticalConfigurationProvider = verticalConfigurationProvider;
        this.eventLogger = eventLogger;
    }

    public async Task<LeadClassificationDecision> ClassifyAsync(
        string tenantId,
        string correlationId,
        IReadOnlyDictionary<string, string> attributes,
        string consentStatus,
        CancellationToken cancellationToken)
    {
        var policy = await ResolvePolicyAsync(tenantId, correlationId, cancellationToken).ConfigureAwait(false);
        return LeadClassificationPolicy.Evaluate(policy, attributes, consentStatus);
    }

    private async Task<ResolvedLeadClassification> ResolvePolicyAsync(
        string tenantId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenant = await tenantConfigurationProvider
                .GetTenantConfigurationAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
            var vertical = await verticalConfigurationProvider
                .GetVerticalConfigurationAsync(tenant.VerticalId.Value, cancellationToken)
                .ConfigureAwait(false);
            var resolution = LeadClassificationPolicy.Resolve(vertical.LeadClassification, tenant.LeadClassification);
            if (resolution.IsValid)
            {
                return resolution.Policy;
            }

            await LogFallbackAsync(tenantId, correlationId, "config_invalid", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogFallbackAsync(tenantId, correlationId, "config_unavailable", cancellationToken).ConfigureAwait(false);
        }

        return LeadClassificationPolicy.PlatformDefault;
    }

    private async Task LogFallbackAsync(
        string tenantId,
        string correlationId,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger.LogEventAsync(
                    TelemetryEventNames.LeadClassificationFallback,
                    new SafeTelemetryProperties()
                        .Add("tenantId", tenantId)
                        .Add("correlationId", correlationId)
                        .Add("reason", reason)
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Telemetry is best-effort; classification already has a safe policy.
        }
    }
}
