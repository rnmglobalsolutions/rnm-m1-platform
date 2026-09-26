using System.Text.RegularExpressions;
using RNM.Platform.Application.Compliance;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.FollowUps;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.FollowUps;

public sealed class FollowUpRunService
{
    private static readonly Regex TemplateTokenRegex = new(@"\{\{\s*(?<token>[^{}]+?)\s*\}\}", RegexOptions.Compiled);

    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly IFollowUpStore followUpStore;
    private readonly ICrmAdapter crmAdapter;
    private readonly ISmsSender smsSender;
    private readonly IEmailSender emailSender;
    private readonly ISendWindowPolicy sendWindowPolicy;
    private readonly IEventLogger eventLogger;
    private readonly ISmsEligibilityGate smsEligibilityGate;

    public FollowUpRunService(
        ITenantConfigurationProvider tenantConfigurationProvider,
        IFollowUpStore followUpStore,
        ICrmAdapter crmAdapter,
        ISmsSender smsSender,
        IEmailSender emailSender,
        ISendWindowPolicy sendWindowPolicy,
        IEventLogger eventLogger,
        ISmsEligibilityGate smsEligibilityGate)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.followUpStore = followUpStore;
        this.crmAdapter = crmAdapter;
        this.smsSender = smsSender;
        this.emailSender = emailSender;
        this.sendWindowPolicy = sendWindowPolicy;
        this.eventLogger = eventLogger;
        this.smsEligibilityGate = smsEligibilityGate;
    }

    public async Task<FollowUpRunResult> RunAsync(
        FollowUpRunRequest request,
        CancellationToken cancellationToken)
    {
        await LogAsync(TelemetryEventNames.FollowUpRunRequested, request.TenantId, request.CorrelationId, null, "requested", cancellationToken)
            .ConfigureAwait(false);

        TenantConfiguration tenant;
        try
        {
            tenant = await tenantConfigurationProvider
                .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ConfigurationException)
        {
            return new FollowUpRunResult(request.TenantId, request.CorrelationId, 0, 0, 0, 0);
        }

        if (tenant.FollowUps?.EffectiveEnabled is not true)
        {
            return new FollowUpRunResult(request.TenantId, request.CorrelationId, 0, 0, 0, 0);
        }

        var dueFollowUps = await followUpStore
            .GetDueFollowUpsAsync(
                request.TenantId,
                request.DueAt,
                Math.Clamp(request.MaxItems, 1, 100),
                cancellationToken)
            .ConfigureAwait(false);

        var sent = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var followUp in dueFollowUps)
        {
            if (!await followUpStore.TryClaimFollowUpAsync(request.TenantId, followUp.RowKey, request.CorrelationId, cancellationToken).ConfigureAwait(false))
            {
                skipped++;
                continue;
            }

            var status = await ProcessFollowUpAsync(followUp, tenant, request.CorrelationId, request.DueAt, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(status, FollowUpStatuses.Sent, StringComparison.OrdinalIgnoreCase))
            {
                sent++;
            }
            else if (string.Equals(status, FollowUpStatuses.Skipped, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
            }
            else
            {
                failed++;
            }
        }

        var result = new FollowUpRunResult(request.TenantId, request.CorrelationId, dueFollowUps.Count, sent, skipped, failed);
        await LogAsync(TelemetryEventNames.FollowUpRunCompleted, request.TenantId, request.CorrelationId, null, "completed", cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private async Task<string> ProcessFollowUpAsync(
        FollowUpDueRecord followUp,
        TenantConfiguration tenant,
        string correlationId,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        var sequence = tenant.FollowUps?.EffectiveSequences.FirstOrDefault(item =>
            string.Equals(item.Id, followUp.SequenceId, StringComparison.OrdinalIgnoreCase));
        if (sequence is null)
        {
            return await SkipAsync(followUp, correlationId, FollowUpSkipReasons.SequenceMissing, "Follow-up sequence missing.", cancellationToken)
                .ConfigureAwait(false);
        }

        var step = sequence.EffectiveSteps.ElementAtOrDefault(followUp.StepIndex);
        if (step is null)
        {
            return await SkipAsync(followUp, correlationId, FollowUpSkipReasons.StepMissing, "Follow-up step missing.", cancellationToken)
                .ConfigureAwait(false);
        }

        var stalenessCutoff = TimeSpan.FromMinutes(tenant.FollowUps?.EffectiveStalenessCutoffMinutes ?? 120);
        if (asOf - followUp.DueAt > stalenessCutoff)
        {
            return await SkipAsync(followUp, correlationId, FollowUpSkipReasons.Stale, "Follow-up skipped because it was stale.", cancellationToken)
                .ConfigureAwait(false);
        }

        var lookup = await crmAdapter
            .FindContactByPhoneOrEmailAsync(
                new CrmContactLookupRequest(
                    followUp.TenantId,
                    correlationId,
                    followUp.CustomerPhoneNumber,
                    followUp.CustomerEmail),
                cancellationToken)
            .ConfigureAwait(false);
        if (lookup.Contact is null)
        {
            return await SkipAsync(followUp, correlationId, FollowUpSkipReasons.MissingContact, "Follow-up skipped because contact could not be resolved.", cancellationToken)
                .ConfigureAwait(false);
        }

        var contact = lookup.Contact;
        if (!string.Equals(contact.ProviderContactId, followUp.ProviderContactId, StringComparison.Ordinal))
        {
            return await SkipAsync(followUp, correlationId, FollowUpSkipReasons.OptedOut, "Follow-up skipped because the contact identity mismatched.", cancellationToken)
                .ConfigureAwait(false);
        }

        var isSms = string.Equals(step.Channel, FollowUpChannels.Sms, StringComparison.OrdinalIgnoreCase);
        var channelConsent = isSms ? contact.SmsConsentStatus : contact.EmailConsentStatus;
        if (string.Equals(channelConsent, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase))
        {
            return await SkipAsync(followUp, correlationId, FollowUpSkipReasons.OptedOut, "Follow-up skipped because contact is opted out for the channel.", cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.Equals(channelConsent, CrmConsentStatuses.OptIn, StringComparison.OrdinalIgnoreCase))
        {
            return await SkipAsync(followUp, correlationId, FollowUpSkipReasons.ConsentNotGranted, "Follow-up skipped because marketing consent was not granted.", cancellationToken)
                .ConfigureAwait(false);
        }

        var attributes = MergeAttributes(followUp.Attributes, contact.Attributes);
        if (StopConditionMatched(sequence, contact, attributes))
        {
            return await SkipAsync(followUp, correlationId, FollowUpSkipReasons.StopConditionMatched, "Follow-up skipped because a stop condition matched.", cancellationToken)
                .ConfigureAwait(false);
        }

        var sendWindow = sendWindowPolicy.Evaluate(
            contact,
            tenant.TimeZone,
            tenant.Voice?.Outbound?.TcpaWindow,
            asOf);
        if (string.Equals(step.Channel, FollowUpChannels.Sms, StringComparison.OrdinalIgnoreCase)
            && !sendWindow.IsAllowed)
        {
            return await SkipAsync(
                    followUp,
                    correlationId,
                    FollowUpSkipReasons.OutsideSendWindow,
                    "Follow-up SMS skipped because it is outside the send window.",
                    cancellationToken,
                    sendWindow.TimeZoneBasis,
                    sendWindow.TimeZoneId,
                    sendWindow.LocalHour.ToString())
                .ConfigureAwait(false);
        }

        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOf, ResolveTimeZone(sendWindow.TimeZoneId)).Date);
        var sentToday = await followUpStore
            .CountSentForContactOnDateAsync(
                followUp.TenantId,
                followUp.ProviderContactId,
                localDate,
                sendWindow.TimeZoneId,
                cancellationToken)
            .ConfigureAwait(false);
        if (sentToday >= (tenant.FollowUps?.EffectiveMaxFollowUpsPerContactPerDay ?? 2))
        {
            return await SkipAsync(followUp, correlationId, "daily_limit_reached", "Follow-up skipped because daily contact limit was reached.", cancellationToken)
                .ConfigureAwait(false);
        }

        var dispatch = string.Equals(step.Channel, FollowUpChannels.Sms, StringComparison.OrdinalIgnoreCase)
            ? await SendSmsAsync(followUp, step, tenant, contact, attributes, correlationId, cancellationToken).ConfigureAwait(false)
            : await SendEmailAsync(followUp, step, tenant, contact, attributes, correlationId, cancellationToken).ConfigureAwait(false);

        if (dispatch.Status is ConfirmationChannelStatus.Sent)
        {
            await MarkAsync(followUp, correlationId, FollowUpStatuses.Sent, null, cancellationToken).ConfigureAwait(false);
            await RecordTimelineAsync(followUp, correlationId, CrmTimelineEventTypes.FollowUpSent, "Follow-up sent.", dispatch.Channel.ToString(), cancellationToken)
                .ConfigureAwait(false);
            await ScheduleNextStepAsync(followUp, sequence, tenant, contact, attributes, correlationId, asOf, cancellationToken)
                .ConfigureAwait(false);
            return FollowUpStatuses.Sent;
        }

        if (dispatch.Status is ConfirmationChannelStatus.Skipped)
        {
            return await SkipAsync(
                    followUp,
                    correlationId,
                    dispatch.FailureReason?.ToString() ?? "notification_skipped",
                    "Follow-up notification skipped.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await MarkAsync(followUp, correlationId, FollowUpStatuses.Failed, dispatch.FailureReason?.ToString(), cancellationToken)
            .ConfigureAwait(false);
        await RecordTimelineAsync(followUp, correlationId, CrmTimelineEventTypes.FollowUpFailed, "Follow-up failed.", dispatch.FailureReason?.ToString(), cancellationToken)
            .ConfigureAwait(false);
        await LogAsync(TelemetryEventNames.FollowUpSendFailed, followUp.TenantId, correlationId, followUp.SequenceId, dispatch.FailureReason?.ToString() ?? "send_failed", cancellationToken)
            .ConfigureAwait(false);
        return FollowUpStatuses.Failed;
    }

    private async Task<FollowUpDispatchResult> SendSmsAsync(
        FollowUpDueRecord followUp,
        FollowUpStepConfiguration step,
        TenantConfiguration tenant,
        CrmContactRecord contact,
        IReadOnlyDictionary<string, string> attributes,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.SmsBodyTemplate))
        {
            return new FollowUpDispatchResult(ConfirmationChannel.Sms, ConfirmationChannelStatus.Skipped, ConfirmationFailureReason.MissingSmsTemplate);
        }

        if (string.IsNullOrWhiteSpace(contact.PhoneNumber))
        {
            return new FollowUpDispatchResult(ConfirmationChannel.Sms, ConfirmationChannelStatus.Skipped, ConfirmationFailureReason.MissingPhoneNumber);
        }

        try
        {
            var eligibility = await smsEligibilityGate.EvaluateAsync(
                    new SmsEligibilityRequest(
                        followUp.TenantId,
                        correlationId,
                        SmsMessageCategory.MarketingFollowUp,
                        followUp.ProviderContactId,
                        contact.PhoneNumber),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!eligibility.IsEligible || eligibility.Proof is null)
            {
                return new FollowUpDispatchResult(
                    ConfirmationChannel.Sms,
                    ConfirmationChannelStatus.Skipped,
                    ConfirmationFailureReason.SmsEligibilityDenied);
            }

            var result = await smsSender
                .SendSmsAsync(
                    new SmsMessageRequest(
                        followUp.TenantId,
                        correlationId,
                        contact.PhoneNumber,
                        RenderTemplate(step.SmsBodyTemplate, followUp, tenant, contact, attributes, correlationId),
                        eligibility.Proof),
                    cancellationToken)
                .ConfigureAwait(false);
            return result.Succeeded
                ? new FollowUpDispatchResult(ConfirmationChannel.Sms, ConfirmationChannelStatus.Sent, ProviderMessageId: result.ProviderMessageId)
                : new FollowUpDispatchResult(ConfirmationChannel.Sms, ConfirmationChannelStatus.Failed, ConfirmationFailureReason.SmsSendFailed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new FollowUpDispatchResult(ConfirmationChannel.Sms, ConfirmationChannelStatus.Failed, ConfirmationFailureReason.SmsSenderException);
        }
    }

    private async Task<FollowUpDispatchResult> SendEmailAsync(
        FollowUpDueRecord followUp,
        FollowUpStepConfiguration step,
        TenantConfiguration tenant,
        CrmContactRecord contact,
        IReadOnlyDictionary<string, string> attributes,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.EmailSubjectTemplate)
            || string.IsNullOrWhiteSpace(step.EmailBodyTemplate))
        {
            return new FollowUpDispatchResult(ConfirmationChannel.Email, ConfirmationChannelStatus.Skipped, ConfirmationFailureReason.MissingEmailTemplate);
        }

        if (string.IsNullOrWhiteSpace(contact.Email))
        {
            return new FollowUpDispatchResult(ConfirmationChannel.Email, ConfirmationChannelStatus.Skipped, ConfirmationFailureReason.MissingEmail);
        }

        try
        {
            var result = await emailSender
                .SendEmailAsync(
                    new EmailMessageRequest(
                        followUp.TenantId,
                        correlationId,
                        contact.Email,
                        RenderTemplate(step.EmailSubjectTemplate, followUp, tenant, contact, attributes, correlationId),
                        RenderTemplate(step.EmailBodyTemplate, followUp, tenant, contact, attributes, correlationId)),
                    cancellationToken)
                .ConfigureAwait(false);
            return result.Succeeded
                ? new FollowUpDispatchResult(ConfirmationChannel.Email, ConfirmationChannelStatus.Sent, ProviderMessageId: result.ProviderMessageId)
                : new FollowUpDispatchResult(ConfirmationChannel.Email, ConfirmationChannelStatus.Failed, ConfirmationFailureReason.EmailSendFailed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new FollowUpDispatchResult(ConfirmationChannel.Email, ConfirmationChannelStatus.Failed, ConfirmationFailureReason.EmailSenderException);
        }
    }

    private async Task ScheduleNextStepAsync(
        FollowUpDueRecord followUp,
        FollowUpSequenceConfiguration sequence,
        TenantConfiguration tenant,
        CrmContactRecord contact,
        IReadOnlyDictionary<string, string> attributes,
        string correlationId,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        var nextIndex = followUp.StepIndex + 1;
        var nextStep = sequence.EffectiveSteps.ElementAtOrDefault(nextIndex);
        if (nextStep is null)
        {
            return;
        }

        var dueAt = asOf.AddMinutes(nextStep.DelayMinutes);
        var next = new FollowUpDueRecord(
            followUp.TenantId,
            FollowUpSchedulingService.CreateRowKey(dueAt, followUp.ProviderContactId, sequence.Id, nextIndex),
            followUp.ProviderContactId,
            sequence.Id,
            nextIndex,
            followUp.TriggerEventType,
            nextStep.Channel,
            dueAt,
            FollowUpStatuses.Pending,
            correlationId)
        {
            CustomerName = contact.Name ?? followUp.CustomerName,
            CustomerPhoneNumber = contact.PhoneNumber ?? followUp.CustomerPhoneNumber,
            CustomerEmail = contact.Email ?? followUp.CustomerEmail,
            Reason = followUp.Reason,
            Attributes = attributes
        };

        try
        {
            await followUpStore.ScheduleFollowUpAsync(next, cancellationToken).ConfigureAwait(false);
            await RecordTimelineAsync(next, correlationId, CrmTimelineEventTypes.FollowUpScheduled, "Follow-up sequence next step scheduled.", nextStep.Channel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.FollowUpSendFailed, tenant.TenantId.Value, correlationId, sequence.Id, "schedule_next_failed", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<string> SkipAsync(
        FollowUpDueRecord followUp,
        string correlationId,
        string reason,
        string summary,
        CancellationToken cancellationToken,
        string? timezoneBasis = null,
        string? timezone = null,
        string? localHour = null)
    {
        await MarkAsync(followUp, correlationId, FollowUpStatuses.Skipped, reason, cancellationToken).ConfigureAwait(false);
        await RecordTimelineAsync(followUp, correlationId, CrmTimelineEventTypes.FollowUpSkipped, summary, reason, cancellationToken, timezoneBasis, timezone, localHour)
            .ConfigureAwait(false);
        return FollowUpStatuses.Skipped;
    }

    private async Task MarkAsync(
        FollowUpDueRecord followUp,
        string correlationId,
        string status,
        string? reason,
        CancellationToken cancellationToken)
    {
        await followUpStore
            .MarkFollowUpAsync(followUp.TenantId, followUp.RowKey, status, correlationId, reason, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RecordTimelineAsync(
        FollowUpDueRecord followUp,
        string correlationId,
        string eventType,
        string summary,
        string? reason,
        CancellationToken cancellationToken,
        string? timezoneBasis = null,
        string? timezone = null,
        string? localHour = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sequenceId"] = followUp.SequenceId,
            ["stepIndex"] = followUp.StepIndex.ToString(),
            ["channel"] = followUp.Channel,
            ["triggerEventType"] = followUp.TriggerEventType
        };
        AddIfPresent(metadata, "reason", reason);
        AddIfPresent(metadata, "timezoneBasis", timezoneBasis);
        AddIfPresent(metadata, "timezone", timezone);
        AddIfPresent(metadata, "localHour", localHour);

        try
        {
            await crmAdapter
                .AddTimelineEventAsync(
                    new CrmTimelineEventRequest(
                        followUp.TenantId,
                        correlationId,
                        followUp.ProviderContactId,
                        ProviderBookingId: null,
                        eventType,
                        "FollowUpAutomation",
                        summary,
                        metadata),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.CrmTimelineEventFailed, followUp.TenantId, correlationId, followUp.SequenceId, "timeline_failed", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool StopConditionMatched(
        FollowUpSequenceConfiguration sequence,
        CrmContactRecord contact,
        IReadOnlyDictionary<string, string> attributes)
    {
        foreach (var condition in sequence.EffectiveStopWhen)
        {
            if (!string.IsNullOrWhiteSpace(condition.ConsentStatus)
                && string.Equals(contact.ConsentStatus, condition.ConsentStatus, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(condition.Attribute) || condition.EqualsAny is null || condition.EqualsAny.Count == 0)
            {
                continue;
            }

            var actual = condition.Attribute.Equals("leadStatus", StringComparison.OrdinalIgnoreCase)
                ? contact.OutboundLeadStatus
                : GetAttribute(attributes, condition.Attribute);
            if (!string.IsNullOrWhiteSpace(actual)
                && condition.EqualsAny.Any(expected => string.Equals(actual.Trim(), expected?.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string RenderTemplate(
        string template,
        FollowUpDueRecord followUp,
        TenantConfiguration tenant,
        CrmContactRecord contact,
        IReadOnlyDictionary<string, string> attributes,
        string correlationId)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenantId"] = followUp.TenantId,
            ["businessName"] = tenant.BusinessName,
            ["correlationId"] = correlationId,
            ["providerContactId"] = followUp.ProviderContactId,
            ["customerName"] = contact.Name ?? followUp.CustomerName ?? string.Empty,
            ["customerPhoneNumber"] = contact.PhoneNumber ?? followUp.CustomerPhoneNumber ?? string.Empty,
            ["customerEmail"] = contact.Email ?? followUp.CustomerEmail ?? string.Empty,
            ["sequenceId"] = followUp.SequenceId,
            ["stepIndex"] = followUp.StepIndex.ToString(),
            ["triggerEventType"] = followUp.TriggerEventType,
            ["reason"] = followUp.Reason ?? string.Empty,
            ["campaignId"] = contact.CampaignId ?? GetAttribute(attributes, CrmContactAttributeNames.CampaignId) ?? string.Empty
        };

        return TemplateTokenRegex.Replace(template, match =>
        {
            var token = match.Groups["token"].Value.Trim();
            if (token.StartsWith("attr.", StringComparison.OrdinalIgnoreCase))
            {
                var attributeName = token["attr.".Length..].Trim();
                return !string.IsNullOrWhiteSpace(attributeName) && attributes.TryGetValue(attributeName, out var value)
                    ? value
                    : string.Empty;
            }

            return tokens.TryGetValue(token, out var tokenValue) ? tokenValue : string.Empty;
        });
    }

    private static IReadOnlyDictionary<string, string> MergeAttributes(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in first.Concat(second))
        {
            if (!string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
            {
                merged[item.Key.Trim()] = item.Value.Trim();
            }
        }

        return merged;
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static string? GetAttribute(IReadOnlyDictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static void AddIfPresent(IDictionary<string, string> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
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
            // Follow-up telemetry is best-effort.
        }
    }
}
