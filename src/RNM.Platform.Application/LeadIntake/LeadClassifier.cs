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
        var decision = LeadClassificationPolicy.Evaluate(policy, attributes, consentStatus);
        if (decision.UnexpectedAttributes.Count > 0)
        {
            // Attribute names only: values come from the lead source and are not logged.
            await LogAsync(
                    TelemetryEventNames.LeadClassificationUnexpectedValue,
                    tenantId,
                    correlationId,
                    ("attributes", string.Join(",", decision.UnexpectedAttributes)),
                    ("ruleId", decision.RuleId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return decision;
    }

    /// <summary>
    /// Resolves the tenant's effective policy once, for batch callers that evaluate many leads
    /// with <see cref="LeadClassificationPolicy.Evaluate"/>. Never throws for configuration problems.
    /// </summary>
    public async Task<ResolvedLeadClassification> ResolvePolicyAsync(
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

            await LogAsync(TelemetryEventNames.LeadClassificationFallback, tenantId, correlationId, ("reason", "config_invalid"), default, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.LeadClassificationFallback, tenantId, correlationId, ("reason", "config_unavailable"), default, cancellationToken)
                .ConfigureAwait(false);
        }

        return LeadClassificationPolicy.PlatformDefault;
    }

    private async Task LogAsync(
        string eventName,
        string tenantId,
        string correlationId,
        (string Name, string Value) first,
        (string Name, string Value)? second,
        CancellationToken cancellationToken)
    {
        try
        {
            var properties = new SafeTelemetryProperties()
                .Add("tenantId", tenantId)
                .Add("correlationId", correlationId)
                .Add(first.Name, first.Value);
            if (second is { } extra)
            {
                properties.Add(extra.Name, extra.Value);
            }

            await eventLogger.LogEventAsync(eventName, properties.ToDictionary(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Telemetry is best-effort; classification already has a safe policy.
        }
    }
}
