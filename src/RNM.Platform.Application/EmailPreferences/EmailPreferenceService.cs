using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;

namespace RNM.Platform.Application.EmailPreferences;

/// <summary>
/// Applies email feedback (unsubscribe links, spam complaints, hard bounces) to the tenant CRM.
/// Addresses and values never reach telemetry; only tenant, correlation, source and outcome do.
/// </summary>
public sealed class EmailPreferenceService
{
    private readonly ICrmAdapter crmAdapter;
    private readonly IEventLogger eventLogger;

    public EmailPreferenceService(ICrmAdapter crmAdapter, IEventLogger eventLogger)
    {
        this.crmAdapter = crmAdapter;
        this.eventLogger = eventLogger;
    }

    /// <summary>
    /// Marks the address as email opted out. Returns false when the CRM could not record it, so the caller can retry.
    /// </summary>
    public async Task<bool> UnsubscribeAsync(
        string tenantId,
        string correlationId,
        string email,
        string source,
        string reason,
        CancellationToken cancellationToken)
    {
        CrmOperationResult result;
        try
        {
            result = await crmAdapter
                .MarkOptOutAsync(
                    new CrmOptOutRequest(tenantId, correlationId, ProviderContactId: null)
                    {
                        Email = EmailUnsubscribeToken.NormalizeEmail(email),
                        Channel = ConsentChannel.Email,
                        Source = source,
                        Reason = reason
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = new CrmOperationResult(false);
        }

        await LogAsync(
                result.Succeeded ? TelemetryEventNames.EmailUnsubscribed : TelemetryEventNames.EmailPreferenceUpdateFailed,
                tenantId,
                correlationId,
                source,
                reason,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <summary>
    /// Records a hard bounce on the contact timeline. SendGrid already suppresses the address; this makes the
    /// bad address visible to the team. Returns false only when the CRM failed.
    /// </summary>
    public async Task<bool> RecordBounceAsync(
        string tenantId,
        string correlationId,
        string email,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var lookup = await crmAdapter
                .FindContactByPhoneOrEmailAsync(
                    new CrmContactLookupRequest(tenantId, correlationId, PhoneNumber: null, EmailUnsubscribeToken.NormalizeEmail(email)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (lookup.Found && !string.IsNullOrWhiteSpace(lookup.ProviderContactId))
            {
                var timeline = await crmAdapter
                    .AddTimelineEventAsync(
                        new CrmTimelineEventRequest(
                            tenantId,
                            correlationId,
                            lookup.ProviderContactId,
                            ProviderBookingId: null,
                            CrmTimelineEventTypes.EmailBounced,
                            "SendGrid",
                            "Email address bounced; SendGrid suppresses further sends.",
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                [CrmContactAttributeNames.EmailDeliverability] = "bounced",
                                ["reason"] = Truncate(reason)
                            }),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!timeline.Succeeded)
                {
                    throw new InvalidOperationException("Timeline write failed.");
                }
            }

            await LogAsync(TelemetryEventNames.EmailBounced, tenantId, correlationId, "SendGrid", lookup.Found ? "contact_found" : "contact_not_found", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.EmailPreferenceUpdateFailed, tenantId, correlationId, "SendGrid", "bounce", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }
    }

    private static string Truncate(string value) => value.Length <= 256 ? value : value[..256];

    private async Task LogAsync(
        string eventName,
        string tenantId,
        string correlationId,
        string source,
        string outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger.LogEventAsync(
                    eventName,
                    new SafeTelemetryProperties()
                        .Add("tenantId", tenantId)
                        .Add("correlationId", correlationId)
                        .Add("source", source)
                        .Add("outcome", outcome)
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Telemetry is best-effort.
        }
    }
}
