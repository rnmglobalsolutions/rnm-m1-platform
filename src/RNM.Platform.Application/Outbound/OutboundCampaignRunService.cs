using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.Outbound;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Outbound;

public sealed class OutboundCampaignRunService
{
    private const int DefaultMaxAttemptsPerLead = 3;
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ICrmAdapter crmAdapter;
    private readonly IOutboundCallAdapter outboundCallAdapter;
    private readonly IEventLogger eventLogger;
    private readonly Func<DateTimeOffset> utcNowProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public OutboundCampaignRunService(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ICrmAdapter crmAdapter,
        IOutboundCallAdapter outboundCallAdapter,
        IEventLogger eventLogger)
        : this(
            tenantConfigurationProvider,
            crmAdapter,
            outboundCallAdapter,
            eventLogger,
            () => DateTimeOffset.UtcNow,
            Task.Delay)
    {
    }

    internal OutboundCampaignRunService(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ICrmAdapter crmAdapter,
        IOutboundCallAdapter outboundCallAdapter,
        IEventLogger eventLogger,
        Func<DateTimeOffset> utcNowProvider,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.crmAdapter = crmAdapter;
        this.outboundCallAdapter = outboundCallAdapter;
        this.eventLogger = eventLogger;
        this.utcNowProvider = utcNowProvider;
        this.delay = delay;
    }

    public async Task<OutboundCampaignRunResult> RunAsync(
        OutboundCampaignRunRequest request,
        CancellationToken cancellationToken)
    {
        await LogAsync(
                TelemetryEventNames.OutboundCampaignRunRequested,
                request,
                "requested",
                cancellationToken)
            .ConfigureAwait(false);

        var tenantConfiguration = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
            .ConfigureAwait(false);
        var outbound = tenantConfiguration.Voice?.Outbound;
        var missingField = GetMissingCriticalConfigurationField(outbound);
        if (missingField is not null)
        {
            await LogAsync(
                    TelemetryEventNames.OutboundCampaignRunFailed,
                    request,
                    "missing_configuration",
                    cancellationToken,
                    missingField: missingField)
                .ConfigureAwait(false);

            return new OutboundCampaignRunResult(
                false,
                request.TenantId,
                request.CampaignId,
                request.CorrelationId,
                0,
                0,
                0,
                [],
                "missing_configuration",
                "Outbound voice configuration is incomplete.");
        }

        var pacing = outbound!.Pacing ?? new OutboundPacingConfiguration();
        var tcpaWindow = outbound.TcpaWindow ?? new TcpaWindowConfiguration();
        var maxConcurrentCalls = pacing.EffectiveMaxConcurrentCalls;
        var minSecondsBetweenCalls = pacing.EffectiveMinSecondsBetweenCalls;
        var maxAttempts = outbound.MaxAttemptsPerLead ?? DefaultMaxAttemptsPerLead;
        var excludedProviderContactIds = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<OutboundCampaignRunItem>();
        var startedCallCount = 0;
        var skippedLeadCount = 0;
        var failedCallStartCount = 0;

        for (var index = 0; index < maxConcurrentCalls; index++)
        {
            var nextLead = await crmAdapter
                .GetNextLeadToCallAsync(
                    new CrmNextLeadToCallRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.CampaignId)
                    {
                        MaxOutboundAttempts = maxAttempts,
                        Now = utcNowProvider(),
                        ExcludedProviderContactIds = excludedProviderContactIds.ToArray()
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!nextLead.Succeeded || nextLead.Lead is null)
            {
                break;
            }

            var lead = nextLead.Lead;
            excludedProviderContactIds.Add(lead.ProviderContactId);

            if (IsOptedOut(lead))
            {
                skippedLeadCount++;
                items.Add(new OutboundCampaignRunItem(lead.ProviderContactId, "skipped_opted_out"));
                await LogLeadOutcomeAsync(
                        TelemetryEventNames.OutboundCallSkipped,
                        request,
                        lead.ProviderContactId,
                        "skipped_opted_out",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (string.IsNullOrWhiteSpace(lead.PhoneNumber))
            {
                skippedLeadCount++;
                items.Add(new OutboundCampaignRunItem(lead.ProviderContactId, "skipped_missing_phone"));
                await LogLeadOutcomeAsync(
                        TelemetryEventNames.OutboundCallSkipped,
                        request,
                        lead.ProviderContactId,
                        "skipped_missing_phone",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var timezone = ResolveLeadTimezone(lead, tenantConfiguration.TimeZone);
            if (!IsWithinTcpaWindow(utcNowProvider(), timezone.TimeZoneId, tcpaWindow, out var localHour))
            {
                skippedLeadCount++;
                items.Add(new OutboundCampaignRunItem(
                    lead.ProviderContactId,
                    "skipped_outside_tcpa_window",
                    TimeZoneBasis: timezone.Basis,
                    TimeZone: timezone.TimeZoneId));
                await LogLeadOutcomeAsync(
                        TelemetryEventNames.OutboundCallSkipped,
                        request,
                        lead.ProviderContactId,
                        "skipped_outside_tcpa_window",
                        cancellationToken,
                        timezoneBasis: timezone.Basis,
                        timezone: timezone.TimeZoneId,
                        localHour: localHour.ToString())
                    .ConfigureAwait(false);
                continue;
            }

            var callbackWebhookUrl = BuildCallbackWebhookUrl(
                outbound.CallbackWebhookBaseUrl!,
                request.TenantId,
                request.CampaignId,
                lead.ProviderContactId,
                request.CorrelationId);

            await LogLeadOutcomeAsync(
                    TelemetryEventNames.OutboundCallStartRequested,
                    request,
                    lead.ProviderContactId,
                    "requested",
                    cancellationToken,
                    timezoneBasis: timezone.Basis,
                    timezone: timezone.TimeZoneId)
                .ConfigureAwait(false);

            var startResult = await outboundCallAdapter
                .StartCallAsync(
                    new OutboundCallStartRequest(
                        request.TenantId,
                        request.CorrelationId,
                        lead,
                        outbound.OutboundAssistantId!,
                        outbound.OutboundPhoneNumberId!,
                        callbackWebhookUrl),
                    cancellationToken)
                .ConfigureAwait(false);

            if (startResult.Succeeded)
            {
                startedCallCount++;
                items.Add(new OutboundCampaignRunItem(
                    lead.ProviderContactId,
                    "call_started",
                    startResult.ProviderCallId,
                    startResult.Status,
                    timezone.Basis,
                    timezone.TimeZoneId));
                await LogLeadOutcomeAsync(
                        TelemetryEventNames.OutboundCallStarted,
                        request,
                        lead.ProviderContactId,
                        "call_started",
                        cancellationToken,
                        providerCallId: startResult.ProviderCallId,
                        status: startResult.Status,
                        timezoneBasis: timezone.Basis,
                        timezone: timezone.TimeZoneId)
                    .ConfigureAwait(false);
            }
            else
            {
                failedCallStartCount++;
                items.Add(new OutboundCampaignRunItem(
                    lead.ProviderContactId,
                    "call_start_failed",
                    Status: startResult.Status,
                    TimeZoneBasis: timezone.Basis,
                    TimeZone: timezone.TimeZoneId,
                    Retryable: startResult.Retryable));
                await LogLeadOutcomeAsync(
                        TelemetryEventNames.OutboundCallFailed,
                        request,
                        lead.ProviderContactId,
                        startResult.FailureReason?.ToString() ?? "call_start_failed",
                        cancellationToken,
                        status: startResult.Status,
                        retryable: startResult.Retryable.ToString())
                    .ConfigureAwait(false);
            }

            if (startedCallCount < maxConcurrentCalls && minSecondsBetweenCalls > 0)
            {
                await delay(TimeSpan.FromSeconds(minSecondsBetweenCalls), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await LogAsync(
                TelemetryEventNames.OutboundCampaignRunCompleted,
                request,
                "completed",
                cancellationToken,
                startedCallCount: startedCallCount.ToString(),
                skippedLeadCount: skippedLeadCount.ToString(),
                failedCallStartCount: failedCallStartCount.ToString())
            .ConfigureAwait(false);

        return new OutboundCampaignRunResult(
            true,
            request.TenantId,
            request.CampaignId,
            request.CorrelationId,
            startedCallCount,
            skippedLeadCount,
            failedCallStartCount,
            items);
    }

    private static string? GetMissingCriticalConfigurationField(OutboundVoiceConfiguration? outbound)
    {
        if (outbound is null)
        {
            return "voice.outbound";
        }

        if (string.IsNullOrWhiteSpace(outbound.VapiApiKeySecretName))
        {
            return "voice.outbound.vapiApiKeySecretName";
        }

        if (string.IsNullOrWhiteSpace(outbound.VapiBaseUrl))
        {
            return "voice.outbound.vapiBaseUrl";
        }

        if (string.IsNullOrWhiteSpace(outbound.OutboundAssistantId))
        {
            return "voice.outbound.outboundAssistantId";
        }

        if (string.IsNullOrWhiteSpace(outbound.OutboundPhoneNumberId))
        {
            return "voice.outbound.outboundPhoneNumberId";
        }

        if (string.IsNullOrWhiteSpace(outbound.CallbackWebhookBaseUrl))
        {
            return "voice.outbound.callbackWebhookBaseUrl";
        }

        return null;
    }

    private static bool IsOptedOut(CrmContactRecord lead) =>
        string.Equals(lead.ConsentStatus, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase);

    private static LeadTimezoneResolution ResolveLeadTimezone(CrmContactRecord lead, string tenantTimeZone)
    {
        var leadTimezone = FirstNonEmptyAttribute(lead, "timeZone", "timezone", "leadTimeZone", "leadTimezone");
        return string.IsNullOrWhiteSpace(leadTimezone)
            ? new LeadTimezoneResolution(tenantTimeZone, "tenant")
            : IsValidTimeZone(leadTimezone)
                ? new LeadTimezoneResolution(leadTimezone, "lead")
                : new LeadTimezoneResolution(tenantTimeZone, "tenant");
    }

    private static string? FirstNonEmptyAttribute(CrmContactRecord lead, params string[] names)
    {
        foreach (var name in names)
        {
            if (lead.Attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsWithinTcpaWindow(
        DateTimeOffset utcNow,
        string timezone,
        TcpaWindowConfiguration tcpaWindow,
        out int localHour)
    {
        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var localTime = TimeZoneInfo.ConvertTime(utcNow, timeZoneInfo);
        localHour = localTime.Hour;
        return localTime.Hour >= tcpaWindow.EffectiveStartHour
            && localTime.Hour < tcpaWindow.EffectiveEndHour;
    }

    private static bool IsValidTimeZone(string timeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static string BuildCallbackWebhookUrl(
        string callbackWebhookBaseUrl,
        string tenantId,
        string campaignId,
        string providerContactId,
        string correlationId)
    {
        var baseUri = new Uri(callbackWebhookBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var path = $"api/tenants/{Uri.EscapeDataString(tenantId)}/webhooks/vapi/outbound";
        var builder = new UriBuilder(new Uri(baseUri, path))
        {
            Query = string.Join(
                "&",
                $"campaignId={Uri.EscapeDataString(campaignId)}",
                $"contactId={Uri.EscapeDataString(providerContactId)}",
                $"correlationId={Uri.EscapeDataString(correlationId)}")
        };

        return builder.Uri.ToString();
    }

    private Task LogAsync(
        string eventName,
        OutboundCampaignRunRequest request,
        string outcome,
        CancellationToken cancellationToken,
        string? missingField = null,
        string? startedCallCount = null,
        string? skippedLeadCount = null,
        string? failedCallStartCount = null)
    {
        var properties = new SafeTelemetryProperties()
            .Add("tenantId", request.TenantId)
            .Add("campaignId", request.CampaignId)
            .Add("correlationId", request.CorrelationId)
            .Add("outcome", outcome)
            .Add("missingField", missingField)
            .Add("startedCallCount", startedCallCount)
            .Add("skippedLeadCount", skippedLeadCount)
            .Add("failedCallStartCount", failedCallStartCount)
            .ToDictionary();

        return eventLogger.LogEventAsync(eventName, properties, cancellationToken);
    }

    private Task LogLeadOutcomeAsync(
        string eventName,
        OutboundCampaignRunRequest request,
        string providerContactId,
        string outcome,
        CancellationToken cancellationToken,
        string? providerCallId = null,
        string? status = null,
        string? timezoneBasis = null,
        string? timezone = null,
        string? localHour = null,
        string? retryable = null)
    {
        var properties = new SafeTelemetryProperties()
            .Add("tenantId", request.TenantId)
            .Add("campaignId", request.CampaignId)
            .Add("correlationId", request.CorrelationId)
            .Add("providerContactId", providerContactId)
            .Add("outcome", outcome)
            .Add("providerCallId", providerCallId)
            .Add("status", status)
            .Add("timezoneBasis", timezoneBasis)
            .Add("timezone", timezone)
            .Add("localHour", localHour)
            .Add("retryable", retryable)
            .ToDictionary();

        return eventLogger.LogEventAsync(eventName, properties, cancellationToken);
    }

    private sealed record LeadTimezoneResolution(string TimeZoneId, string Basis);
}
