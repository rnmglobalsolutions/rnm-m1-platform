using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.Messaging;

namespace RNM.Platform.Application.Confirmations;

public sealed class ConfirmationApplicationService
{
    private readonly ISmsSender smsSender;
    private readonly IEmailSender emailSender;
    private readonly IConfirmationRetryScheduler retryScheduler;
    private readonly IEventLogger eventLogger;
    private readonly ICrmAdapter crmAdapter;

    public ConfirmationApplicationService(
        ISmsSender smsSender,
        IEmailSender emailSender,
        IConfirmationRetryScheduler retryScheduler,
        IEventLogger eventLogger,
        ICrmAdapter crmAdapter)
    {
        this.smsSender = smsSender;
        this.emailSender = emailSender;
        this.retryScheduler = retryScheduler;
        this.eventLogger = eventLogger;
        this.crmAdapter = crmAdapter;
    }

    public async Task<BookingConfirmationResult> SendBookingConfirmationAsync(
        BookingConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        await LogAsync(TelemetryEventNames.ConfirmationRequested, request, null, cancellationToken)
            .ConfigureAwait(false);

        if (!request.BookingDecision.IsBooked)
        {
            var smsSkipped = Skipped(ConfirmationChannel.Sms, ConfirmationFailureReason.BookingNotCompleted);
            var emailSkipped = Skipped(ConfirmationChannel.Email, ConfirmationFailureReason.BookingNotCompleted);
            await LogAsync(TelemetryEventNames.EmailConfirmationSkipped, request, emailSkipped, cancellationToken)
                .ConfigureAwait(false);
            return new BookingConfirmationResult(smsSkipped, emailSkipped);
        }

        var contactOptedOut = await IsCustomerOptedOutAsync(request, cancellationToken).ConfigureAwait(false);
        var smsResult = await SendSmsAsync(request, contactOptedOut, cancellationToken).ConfigureAwait(false);
        var emailResult = await SendEmailAsync(request, contactOptedOut, cancellationToken).ConfigureAwait(false);
        var businessEmailResult = await SendBusinessEmailAsync(request, cancellationToken).ConfigureAwait(false);
        var businessSmsResult = await SendBusinessSmsAsync(request, cancellationToken).ConfigureAwait(false);
        return new BookingConfirmationResult(smsResult, emailResult, businessSmsResult, businessEmailResult);
    }

    private async Task<ConfirmationChannelResult> SendSmsAsync(
        BookingConfirmationRequest request,
        bool contactOptedOut,
        CancellationToken cancellationToken)
    {
        if (contactOptedOut)
        {
            var skipped = Skipped(ConfirmationChannel.Sms, ConfirmationFailureReason.ContactOptedOut);
            await LogAsync(TelemetryEventNames.SmsConfirmationSkipped, request, skipped, cancellationToken)
                .ConfigureAwait(false);
            return skipped;
        }

        if (string.IsNullOrWhiteSpace(request.CustomerPhoneNumber))
        {
            var failed = Failed(ConfirmationChannel.Sms, ConfirmationFailureReason.MissingPhoneNumber);
            await LogAsync(TelemetryEventNames.SmsConfirmationFailed, request, failed, cancellationToken)
                .ConfigureAwait(false);
            return failed;
        }

        if (string.IsNullOrWhiteSpace(request.Templates.SmsBodyTemplate))
        {
            var failed = Failed(ConfirmationChannel.Sms, ConfirmationFailureReason.MissingSmsTemplate);
            await LogAsync(TelemetryEventNames.SmsConfirmationFailed, request, failed, cancellationToken)
                .ConfigureAwait(false);
            return failed;
        }

        var body = RenderTemplate(request.Templates.SmsBodyTemplate, request);
        try
        {
            var sendResult = await smsSender
                .SendSmsAsync(
                    new SmsMessageRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.CustomerPhoneNumber,
                        body),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!sendResult.Succeeded)
            {
                await ScheduleRetryAsync(
                        request,
                        new ConfirmationRetryRequest(
                            request.TenantId,
                            request.CorrelationId,
                            ConfirmationRetryKind.CustomerSms,
                            request.CustomerPhoneNumber,
                            body),
                        cancellationToken)
                    .ConfigureAwait(false);
                var failed = Failed(ConfirmationChannel.Sms, ConfirmationFailureReason.SmsSendFailed);
                await LogAsync(TelemetryEventNames.SmsConfirmationFailed, request, failed, cancellationToken)
                    .ConfigureAwait(false);
                return failed;
            }

            var sent = Sent(ConfirmationChannel.Sms, sendResult.ProviderMessageId);
            await LogAsync(TelemetryEventNames.SmsConfirmationSent, request, sent, cancellationToken)
                .ConfigureAwait(false);
            await TryRecordSentTimelineEventAsync(
                    request,
                    CrmTimelineEventTypes.SmsSent,
                    "SMS confirmation sent.",
                    sent,
                    "customer",
                    cancellationToken)
                .ConfigureAwait(false);
            return sent;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ScheduleRetryAsync(
                    request,
                    new ConfirmationRetryRequest(
                        request.TenantId,
                        request.CorrelationId,
                        ConfirmationRetryKind.CustomerSms,
                        request.CustomerPhoneNumber,
                        body),
                    cancellationToken)
                .ConfigureAwait(false);
            var failed = Failed(ConfirmationChannel.Sms, ConfirmationFailureReason.SmsSenderException);
            await LogAsync(TelemetryEventNames.SmsConfirmationFailed, request, failed, cancellationToken)
                .ConfigureAwait(false);
            return failed;
        }
    }

    private async Task<ConfirmationChannelResult> SendEmailAsync(
        BookingConfirmationRequest request,
        bool contactOptedOut,
        CancellationToken cancellationToken)
    {
        if (contactOptedOut)
        {
            var skipped = Skipped(ConfirmationChannel.Email, ConfirmationFailureReason.ContactOptedOut);
            await LogAsync(TelemetryEventNames.EmailConfirmationSkipped, request, skipped, cancellationToken)
                .ConfigureAwait(false);
            return skipped;
        }

        if (string.IsNullOrWhiteSpace(request.CustomerEmail))
        {
            var skipped = Skipped(ConfirmationChannel.Email, ConfirmationFailureReason.MissingEmail);
            await LogAsync(TelemetryEventNames.EmailConfirmationSkipped, request, skipped, cancellationToken)
                .ConfigureAwait(false);
            return skipped;
        }

        if (string.IsNullOrWhiteSpace(request.Templates.EmailSubjectTemplate)
            || string.IsNullOrWhiteSpace(request.Templates.EmailBodyTemplate))
        {
            var skipped = Skipped(ConfirmationChannel.Email, ConfirmationFailureReason.MissingEmailTemplate);
            await LogAsync(TelemetryEventNames.EmailConfirmationSkipped, request, skipped, cancellationToken)
                .ConfigureAwait(false);
            return skipped;
        }

        var subject = RenderTemplate(request.Templates.EmailSubjectTemplate, request);
        var body = RenderTemplate(request.Templates.EmailBodyTemplate, request);
        try
        {
            var sendResult = await emailSender
                .SendEmailAsync(
                    new EmailMessageRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.CustomerEmail,
                        subject,
                        body),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!sendResult.Succeeded)
            {
                await ScheduleRetryAsync(
                        request,
                        new ConfirmationRetryRequest(
                            request.TenantId,
                            request.CorrelationId,
                            ConfirmationRetryKind.CustomerEmail,
                            request.CustomerEmail,
                            body,
                            subject),
                        cancellationToken)
                    .ConfigureAwait(false);
                var failed = Failed(ConfirmationChannel.Email, ConfirmationFailureReason.EmailSendFailed);
                await LogAsync(TelemetryEventNames.EmailConfirmationFailed, request, failed, cancellationToken)
                    .ConfigureAwait(false);
                return failed;
            }

            var sent = Sent(ConfirmationChannel.Email, sendResult.ProviderMessageId);
            await LogAsync(TelemetryEventNames.EmailConfirmationSent, request, sent, cancellationToken)
                .ConfigureAwait(false);
            await TryRecordSentTimelineEventAsync(
                    request,
                    CrmTimelineEventTypes.EmailSent,
                    "Email confirmation sent.",
                    sent,
                    "customer",
                    cancellationToken)
                .ConfigureAwait(false);
            return sent;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ScheduleRetryAsync(
                    request,
                    new ConfirmationRetryRequest(
                        request.TenantId,
                        request.CorrelationId,
                        ConfirmationRetryKind.CustomerEmail,
                        request.CustomerEmail,
                        body,
                        subject),
                    cancellationToken)
                .ConfigureAwait(false);
            var failed = Failed(ConfirmationChannel.Email, ConfirmationFailureReason.EmailSenderException);
            await LogAsync(TelemetryEventNames.EmailConfirmationFailed, request, failed, cancellationToken)
                .ConfigureAwait(false);
            return failed;
        }
    }

    private async Task<ConfirmationChannelResult?> SendBusinessEmailAsync(
        BookingConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BusinessNotificationEmail))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.Templates.BusinessEmailSubjectTemplate)
            || string.IsNullOrWhiteSpace(request.Templates.BusinessEmailBodyTemplate))
        {
            var skipped = Skipped(ConfirmationChannel.Email, ConfirmationFailureReason.MissingBusinessEmailTemplate);
            await LogAsync(TelemetryEventNames.BusinessEmailNotificationSkipped, request, skipped, cancellationToken, "business")
                .ConfigureAwait(false);
            return skipped;
        }

        var subject = RenderTemplate(request.Templates.BusinessEmailSubjectTemplate, request);
        var body = RenderTemplate(request.Templates.BusinessEmailBodyTemplate, request);
        try
        {
            var sendResult = await emailSender
                .SendEmailAsync(
                    new EmailMessageRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.BusinessNotificationEmail,
                        subject,
                        body),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!sendResult.Succeeded)
            {
                await ScheduleRetryAsync(
                        request,
                        new ConfirmationRetryRequest(
                            request.TenantId,
                            request.CorrelationId,
                            ConfirmationRetryKind.BusinessEmail,
                            request.BusinessNotificationEmail,
                            body,
                            subject),
                        cancellationToken)
                    .ConfigureAwait(false);
                var failed = Failed(ConfirmationChannel.Email, ConfirmationFailureReason.EmailSendFailed);
                await LogAsync(TelemetryEventNames.BusinessEmailNotificationFailed, request, failed, cancellationToken, "business")
                    .ConfigureAwait(false);
                return failed;
            }

            var sent = Sent(ConfirmationChannel.Email, sendResult.ProviderMessageId);
            await LogAsync(TelemetryEventNames.BusinessEmailNotificationSent, request, sent, cancellationToken, "business")
                .ConfigureAwait(false);
            await TryRecordSentTimelineEventAsync(
                    request,
                    CrmTimelineEventTypes.EmailSent,
                    "Business email notification sent.",
                    sent,
                    "business",
                    cancellationToken)
                .ConfigureAwait(false);
            return sent;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ScheduleRetryAsync(
                    request,
                    new ConfirmationRetryRequest(
                        request.TenantId,
                        request.CorrelationId,
                        ConfirmationRetryKind.BusinessEmail,
                        request.BusinessNotificationEmail,
                        body,
                        subject),
                    cancellationToken)
                .ConfigureAwait(false);
            var failed = Failed(ConfirmationChannel.Email, ConfirmationFailureReason.EmailSenderException);
            await LogAsync(TelemetryEventNames.BusinessEmailNotificationFailed, request, failed, cancellationToken, "business")
                .ConfigureAwait(false);
            return failed;
        }
    }

    private async Task<ConfirmationChannelResult?> SendBusinessSmsAsync(
        BookingConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.NotifyBusinessBySms)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.BusinessNotificationPhoneNumber))
        {
            var skipped = Skipped(ConfirmationChannel.Sms, ConfirmationFailureReason.MissingBusinessPhoneNumber);
            await LogAsync(TelemetryEventNames.BusinessSmsNotificationSkipped, request, skipped, cancellationToken, "business")
                .ConfigureAwait(false);
            return skipped;
        }

        if (string.IsNullOrWhiteSpace(request.Templates.BusinessSmsBodyTemplate))
        {
            var skipped = Skipped(ConfirmationChannel.Sms, ConfirmationFailureReason.MissingBusinessSmsTemplate);
            await LogAsync(TelemetryEventNames.BusinessSmsNotificationSkipped, request, skipped, cancellationToken, "business")
                .ConfigureAwait(false);
            return skipped;
        }

        var body = RenderTemplate(request.Templates.BusinessSmsBodyTemplate, request);
        try
        {
            var sendResult = await smsSender
                .SendSmsAsync(
                    new SmsMessageRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.BusinessNotificationPhoneNumber,
                        body),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!sendResult.Succeeded)
            {
                await ScheduleRetryAsync(
                        request,
                        new ConfirmationRetryRequest(
                            request.TenantId,
                            request.CorrelationId,
                            ConfirmationRetryKind.BusinessSms,
                            request.BusinessNotificationPhoneNumber,
                            body),
                        cancellationToken)
                    .ConfigureAwait(false);
                var failed = Failed(ConfirmationChannel.Sms, ConfirmationFailureReason.SmsSendFailed);
                await LogAsync(TelemetryEventNames.BusinessSmsNotificationFailed, request, failed, cancellationToken, "business")
                    .ConfigureAwait(false);
                return failed;
            }

            var sent = Sent(ConfirmationChannel.Sms, sendResult.ProviderMessageId);
            await LogAsync(TelemetryEventNames.BusinessSmsNotificationSent, request, sent, cancellationToken, "business")
                .ConfigureAwait(false);
            await TryRecordSentTimelineEventAsync(
                    request,
                    CrmTimelineEventTypes.SmsSent,
                    "Business SMS notification sent.",
                    sent,
                    "business",
                    cancellationToken)
                .ConfigureAwait(false);
            return sent;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ScheduleRetryAsync(
                    request,
                    new ConfirmationRetryRequest(
                        request.TenantId,
                        request.CorrelationId,
                        ConfirmationRetryKind.BusinessSms,
                        request.BusinessNotificationPhoneNumber,
                        body),
                    cancellationToken)
                .ConfigureAwait(false);
            var failed = Failed(ConfirmationChannel.Sms, ConfirmationFailureReason.SmsSenderException);
            await LogAsync(TelemetryEventNames.BusinessSmsNotificationFailed, request, failed, cancellationToken, "business")
                .ConfigureAwait(false);
            return failed;
        }
    }

    private static string RenderTemplate(
        string template,
        BookingConfirmationRequest request)
    {
        var zone = ResolveTimeZone(request.TimeZone);
        var startsAt = ConvertToLocal(request.BookingDecision.SelectedSlot?.StartsAt, zone);
        var endsAt = ConvertToLocal(request.BookingDecision.SelectedSlot?.EndsAt, zone);

        return template
            .Replace("{{tenantId}}", request.TenantId, StringComparison.OrdinalIgnoreCase)
            .Replace("{{verticalId}}", request.VerticalId, StringComparison.OrdinalIgnoreCase)
            .Replace("{{correlationId}}", request.CorrelationId, StringComparison.OrdinalIgnoreCase)
            .Replace("{{customerName}}", request.CustomerName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{customerPhoneNumber}}", request.CustomerPhoneNumber ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{customerEmail}}", request.CustomerEmail ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{serviceType}}", request.ServiceType ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{propertyType}}", request.PropertyType ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{serviceAddress}}", request.ServiceAddress ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{zipCode}}", request.ZipCode ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{urgency}}", request.Urgency ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{providerBookingId}}", request.BookingDecision.ProviderBookingId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{bookingLabel}}", request.BookingDecision.SelectedSlot?.Label ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{bookingStart}}", startsAt?.ToString("O") ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{bookingEnd}}", endsAt?.ToString("O") ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{bookingDate}}", startsAt?.ToString("yyyy-MM-dd") ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{bookingTime}}", startsAt?.ToString("HH:mm") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? ConvertToLocal(DateTimeOffset? value, TimeZoneInfo zone)
    {
        return value is null ? null : TimeZoneInfo.ConvertTime(value.Value, zone);
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

    private static ConfirmationChannelResult Sent(
        ConfirmationChannel channel,
        string? providerMessageId) =>
        new(channel, ConfirmationChannelStatus.Sent, ProviderMessageId: providerMessageId);

    private static ConfirmationChannelResult Failed(
        ConfirmationChannel channel,
        ConfirmationFailureReason reason) =>
        new(channel, ConfirmationChannelStatus.Failed, reason);

    private static ConfirmationChannelResult Skipped(
        ConfirmationChannel channel,
        ConfirmationFailureReason reason) =>
        new(channel, ConfirmationChannelStatus.Skipped, reason);

    private async Task ScheduleRetryAsync(
        BookingConfirmationRequest request,
        ConfirmationRetryRequest retryRequest,
        CancellationToken cancellationToken)
    {
        var scheduled = false;
        try
        {
            scheduled = await retryScheduler.ScheduleAsync(retryRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            scheduled = false;
        }

        var properties = new SafeTelemetryProperties()
            .Add("correlationId", request.CorrelationId)
            .Add("tenantId", request.TenantId)
            .Add("verticalId", request.VerticalId)
            .Add("retryKind", retryRequest.Kind.ToString())
            .Add("outcome", scheduled ? "scheduled" : "schedule_failed")
            .ToDictionary();

        try
        {
            await eventLogger.LogEventAsync(
                    scheduled
                        ? TelemetryEventNames.ConfirmationRetryScheduled
                        : TelemetryEventNames.ConfirmationRetryScheduleFailed,
                    properties,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Retry telemetry is best-effort.
        }
    }

    private async Task TryRecordSentTimelineEventAsync(
        BookingConfirmationRequest request,
        string eventType,
        string summary,
        ConfirmationChannelResult result,
        string target,
        CancellationToken cancellationToken)
    {
        try
        {
            var timelineResult = await crmAdapter
                .AddTimelineEventAsync(
                    new CrmTimelineEventRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.CrmSyncResult.ProviderContactId,
                        request.BookingDecision.ProviderBookingId,
                        eventType,
                        "Confirmation",
                        summary,
                        new Dictionary<string, string>
                        {
                            ["target"] = target,
                            ["channel"] = result.Channel.ToString(),
                            ["providerMessageId"] = result.ProviderMessageId ?? string.Empty
                        }),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!timelineResult.Succeeded)
            {
                await LogAsync(TelemetryEventNames.CrmTimelineEventFailed, request, result, cancellationToken, target)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.CrmTimelineEventFailed, request, result, cancellationToken, target)
                .ConfigureAwait(false);
        }
    }

    private async Task LogAsync(
        string eventName,
        BookingConfirmationRequest request,
        ConfirmationChannelResult? result,
        CancellationToken cancellationToken,
        string target = "customer")
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", request.CorrelationId)
            .Add("tenantId", request.TenantId)
            .Add("verticalId", request.VerticalId)
            .Add("bookingState", request.BookingDecision.State.ToString())
            .Add("crmState", request.CrmSyncResult.State.ToString())
            .Add("target", target)
            .AddIf(result is not null, "channel", result?.Channel.ToString())
            .AddIf(result is not null, "confirmationStatus", result?.Status.ToString())
            .AddIf(result?.FailureReason is not null, "failureReason", result?.FailureReason.ToString())
            .AddIf(!string.IsNullOrWhiteSpace(request.ServiceType), "serviceTypePresent", "true")
            .ToDictionary();

        try
        {
            await eventLogger.LogEventAsync(eventName, properties, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Confirmation telemetry is best-effort.
        }
    }

    private async Task<bool> IsCustomerOptedOutAsync(
        BookingConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerPhoneNumber)
            && string.IsNullOrWhiteSpace(request.CustomerEmail))
        {
            return false;
        }

        try
        {
            var lookup = await crmAdapter
                .FindContactByPhoneOrEmailAsync(
                    new CrmContactLookupRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.CustomerPhoneNumber,
                        request.CustomerEmail),
                    cancellationToken)
                .ConfigureAwait(false);

            return string.Equals(
                lookup.Contact?.ConsentStatus,
                CrmConsentStatuses.OptedOut,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // CRM lookup failures should not block a caller-requested appointment confirmation.
            return false;
        }
    }
}
