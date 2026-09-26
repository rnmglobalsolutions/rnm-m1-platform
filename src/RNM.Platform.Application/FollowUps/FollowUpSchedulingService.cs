using System.Security.Cryptography;
using System.Text;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.FollowUps;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.FollowUps;

public sealed class FollowUpSchedulingService
{
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly IFollowUpStore followUpStore;
    private readonly ICrmAdapter crmAdapter;
    private readonly IEventLogger eventLogger;

    public FollowUpSchedulingService(
        ITenantConfigurationProvider tenantConfigurationProvider,
        IFollowUpStore followUpStore,
        ICrmAdapter crmAdapter,
        IEventLogger eventLogger)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.followUpStore = followUpStore;
        this.crmAdapter = crmAdapter;
        this.eventLogger = eventLogger;
    }

    /// <summary>
    /// A sequence whose trigger is "{trigger}.{leadTemperature}" (for example "followup.required.cold") replaces
    /// the generic "{trigger}" sequences for leads of that temperature, so each tier can have its own cadence.
    /// </summary>
    private static FollowUpSequenceConfiguration[] SelectSequences(
        IEnumerable<FollowUpSequenceConfiguration> sequences,
        FollowUpScheduleRequest request)
    {
        var all = sequences.ToArray();
        var temperature = request.Attributes.TryGetValue("leadTemperature", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
        if (temperature is not null)
        {
            var tierTrigger = $"{request.TriggerEventType}.{temperature}";
            var tierSequences = all
                .Where(sequence => string.Equals(sequence.Trigger, tierTrigger, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (tierSequences.Length > 0)
            {
                return tierSequences;
            }
        }

        return all
            .Where(sequence => string.Equals(sequence.Trigger, request.TriggerEventType, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public async Task ScheduleAsync(
        FollowUpScheduleRequest request,
        CancellationToken cancellationToken)
    {
        TenantConfiguration tenant;
        try
        {
            tenant = await tenantConfigurationProvider
                .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ConfigurationException)
        {
            return;
        }

        var configuration = tenant.FollowUps;
        if (configuration?.EffectiveEnabled is not true)
        {
            return;
        }

        var sequences = SelectSequences(configuration.EffectiveSequences, request);
        foreach (var sequence in sequences)
        {
            var firstStep = sequence.EffectiveSteps.FirstOrDefault();
            if (firstStep is null)
            {
                continue;
            }

            var dueAt = request.TriggeredAt.AddMinutes(firstStep.DelayMinutes);
            var followUp = new FollowUpDueRecord(
                request.TenantId,
                CreateRowKey(dueAt, request.ProviderContactId, sequence.Id, stepIndex: 0),
                request.ProviderContactId,
                sequence.Id,
                StepIndex: 0,
                request.TriggerEventType,
                firstStep.Channel,
                dueAt,
                FollowUpStatuses.Pending,
                request.CorrelationId)
            {
                CustomerName = request.CustomerName,
                CustomerPhoneNumber = request.CustomerPhoneNumber,
                CustomerEmail = request.CustomerEmail,
                Reason = request.Reason,
                CreatedAt = DateTimeOffset.UtcNow,
                Attributes = request.Attributes
            };

            try
            {
                await followUpStore.ScheduleFollowUpAsync(followUp, cancellationToken).ConfigureAwait(false);
                await RecordTimelineAsync(request, sequence.Id, firstStep.Channel, dueAt, cancellationToken).ConfigureAwait(false);
                await LogAsync(TelemetryEventNames.FollowUpScheduled, request.TenantId, request.CorrelationId, sequence.Id, "scheduled", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                await LogAsync(TelemetryEventNames.FollowUpSendFailed, request.TenantId, request.CorrelationId, sequence.Id, "schedule_failed", cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    internal static string CreateRowKey(
        DateTimeOffset dueAt,
        string providerContactId,
        string sequenceId,
        int stepIndex)
    {
        var hashSource = $"{providerContactId}|{sequenceId}|{stepIndex}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashSource))).ToLowerInvariant();
        return $"{dueAt.UtcDateTime.Ticks:D20}|{sequenceId}|{stepIndex:D3}|{hash}";
    }

    private async Task RecordTimelineAsync(
        FollowUpScheduleRequest request,
        string sequenceId,
        string channel,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await crmAdapter
                .AddTimelineEventAsync(
                    new CrmTimelineEventRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.ProviderContactId,
                        ProviderBookingId: null,
                        CrmTimelineEventTypes.FollowUpScheduled,
                        "FollowUpAutomation",
                        "Follow-up sequence scheduled.",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["sequenceId"] = sequenceId,
                            ["channel"] = channel,
                            ["triggerEventType"] = request.TriggerEventType,
                            ["dueAt"] = dueAt.ToUniversalTime().ToString("O"),
                            ["reason"] = request.Reason
                        }),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.CrmTimelineEventFailed, request.TenantId, request.CorrelationId, sequenceId, "timeline_failed", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task LogAsync(
        string eventName,
        string tenantId,
        string correlationId,
        string? sequenceId,
        string outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger
                .LogEventAsync(
                    eventName,
                    new SafeTelemetryProperties()
                        .Add("tenantId", tenantId)
                        .Add("correlationId", correlationId)
                        .Add("sequenceId", sequenceId)
                        .Add("outcome", outcome)
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Follow-up scheduling telemetry is best-effort.
        }
    }
}
