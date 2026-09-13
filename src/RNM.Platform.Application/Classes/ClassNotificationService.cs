using System.Text.RegularExpressions;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Classes;

public sealed class ClassNotificationService
{
    private static readonly Regex TemplateTokenRegex = new(@"\{\{\s*(?<token>[^{}]+?)\s*\}\}", RegexOptions.Compiled);

    private readonly ISmsSender smsSender;
    private readonly IEmailSender emailSender;
    private readonly ICrmAdapter crmAdapter;
    private readonly IEventLogger eventLogger;

    public ClassNotificationService(
        ISmsSender smsSender,
        IEmailSender emailSender,
        ICrmAdapter crmAdapter,
        IEventLogger eventLogger)
    {
        this.smsSender = smsSender;
        this.emailSender = emailSender;
        this.crmAdapter = crmAdapter;
        this.eventLogger = eventLogger;
    }

    public async Task<(ConfirmationChannelResult? Sms, ConfirmationChannelResult? Email)> SendAsync(
        ClassNotificationRequest request,
        TenantConfiguration tenant,
        CancellationToken cancellationToken)
    {
        var sms = await SendSmsAsync(request, cancellationToken).ConfigureAwait(false);
        var email = await SendEmailAsync(request, cancellationToken).ConfigureAwait(false);
        return (sms, email);
    }

    private async Task<ConfirmationChannelResult?> SendSmsAsync(
        ClassNotificationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Templates.SmsBodyTemplate))
        {
            return Skipped(ConfirmationChannel.Sms, ConfirmationFailureReason.MissingSmsTemplate);
        }

        if (string.IsNullOrWhiteSpace(request.Registration.CustomerPhoneNumber))
        {
            return Skipped(ConfirmationChannel.Sms, ConfirmationFailureReason.MissingPhoneNumber);
        }

        if (string.Equals(request.Registration.ConsentStatus, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase))
        {
            return Skipped(ConfirmationChannel.Sms, ConfirmationFailureReason.ContactOptedOut);
        }

        if (!string.Equals(request.Registration.ConsentStatus, CrmConsentStatuses.OptIn, StringComparison.OrdinalIgnoreCase))
        {
            return Skipped(ConfirmationChannel.Sms, ConfirmationFailureReason.MarketingConsentNotGranted);
        }

        var body = RenderTemplate(request.Templates.SmsBodyTemplate, request);
        try
        {
            var result = await smsSender
                .SendSmsAsync(
                    new SmsMessageRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.Registration.CustomerPhoneNumber,
                        body),
                    cancellationToken)
                .ConfigureAwait(false);

            var channelResult = result.Succeeded
                ? Sent(ConfirmationChannel.Sms, result.ProviderMessageId)
                : Failed(ConfirmationChannel.Sms, ConfirmationFailureReason.SmsSendFailed);
            await RecordNotificationTimelineAsync(request, channelResult, "sms", cancellationToken).ConfigureAwait(false);
            return channelResult;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failed = Failed(ConfirmationChannel.Sms, ConfirmationFailureReason.SmsSenderException);
            await LogNotificationFailureAsync(request, "sms", failed.FailureReason?.ToString(), cancellationToken).ConfigureAwait(false);
            return failed;
        }
    }

    private async Task<ConfirmationChannelResult?> SendEmailAsync(
        ClassNotificationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Templates.EmailSubjectTemplate)
            || string.IsNullOrWhiteSpace(request.Templates.EmailBodyTemplate))
        {
            return Skipped(ConfirmationChannel.Email, ConfirmationFailureReason.MissingEmailTemplate);
        }

        if (string.IsNullOrWhiteSpace(request.Registration.CustomerEmail))
        {
            return Skipped(ConfirmationChannel.Email, ConfirmationFailureReason.MissingEmail);
        }

        var subject = RenderTemplate(request.Templates.EmailSubjectTemplate, request);
        var body = RenderTemplate(request.Templates.EmailBodyTemplate, request);
        try
        {
            var result = await emailSender
                .SendEmailAsync(
                    new EmailMessageRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.Registration.CustomerEmail,
                        subject,
                        body),
                    cancellationToken)
                .ConfigureAwait(false);

            var channelResult = result.Succeeded
                ? Sent(ConfirmationChannel.Email, result.ProviderMessageId)
                : Failed(ConfirmationChannel.Email, ConfirmationFailureReason.EmailSendFailed);
            await RecordNotificationTimelineAsync(request, channelResult, "email", cancellationToken).ConfigureAwait(false);
            return channelResult;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failed = Failed(ConfirmationChannel.Email, ConfirmationFailureReason.EmailSenderException);
            await LogNotificationFailureAsync(request, "email", failed.FailureReason?.ToString(), cancellationToken).ConfigureAwait(false);
            return failed;
        }
    }

    private static string RenderTemplate(
        string template,
        ClassNotificationRequest request)
    {
        var zone = ResolveTimeZone(request.Session.TimeZone);
        var startsAt = TimeZoneInfo.ConvertTime(request.Session.StartsAt, zone);
        var endsAt = request.Session.EndsAt.HasValue
            ? TimeZoneInfo.ConvertTime(request.Session.EndsAt.Value, zone)
            : (DateTimeOffset?)null;
        var attributes = MergeAttributes(request.Session.Attributes, request.Registration.Attributes);

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenantId"] = request.TenantId,
            ["correlationId"] = request.CorrelationId,
            ["sessionId"] = request.Session.SessionId,
            ["registrationId"] = request.Registration.RegistrationId,
            ["campaignId"] = request.Registration.CampaignId ?? request.Session.CampaignId ?? string.Empty,
            ["classTitle"] = request.Session.Title,
            ["classStart"] = startsAt.ToString("O"),
            ["classEnd"] = endsAt?.ToString("O") ?? string.Empty,
            ["classDate"] = startsAt.ToString("yyyy-MM-dd"),
            ["classTime"] = startsAt.ToString("HH:mm"),
            ["timeZone"] = request.Session.TimeZone,
            ["zoomUrl"] = request.Session.ZoomUrl,
            ["customerName"] = request.Registration.CustomerName,
            ["customerPhoneNumber"] = request.Registration.CustomerPhoneNumber ?? string.Empty,
            ["customerEmail"] = request.Registration.CustomerEmail ?? string.Empty
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

    private async Task RecordNotificationTimelineAsync(
        ClassNotificationRequest request,
        ConfirmationChannelResult result,
        string channel,
        CancellationToken cancellationToken)
    {
        if (result.Status is not ConfirmationChannelStatus.Sent)
        {
            await LogNotificationFailureAsync(request, channel, result.FailureReason?.ToString(), cancellationToken).ConfigureAwait(false);
            return;
        }

        var eventType = request.Kind is ClassNotificationKind.Reminder
            ? ClassTimelineEventTypes.ReminderSent
            : ClassTimelineEventTypes.RegistrationConfirmationSent;
        try
        {
            var timelineResult = await crmAdapter
                .AddTimelineEventAsync(
                    new CrmTimelineEventRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.Registration.ProviderContactId,
                        ProviderBookingId: null,
                        eventType,
                        "ClassNotification",
                        $"{channel} class notification sent.",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["channel"] = channel,
                            ["sessionId"] = request.Session.SessionId,
                            ["registrationId"] = request.Registration.RegistrationId,
                            ["providerMessageId"] = result.ProviderMessageId ?? string.Empty,
                            ["notificationKind"] = request.Kind.ToString()
                        }),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!timelineResult.Succeeded)
            {
                await LogNotificationFailureAsync(request, channel, "timeline_write_failed", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogNotificationFailureAsync(request, channel, "timeline_exception", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LogNotificationFailureAsync(
        ClassNotificationRequest request,
        string channel,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger
                .LogEventAsync(
                    request.Kind is ClassNotificationKind.Reminder
                        ? TelemetryEventNames.ClassReminderFailed
                        : TelemetryEventNames.ClassNotificationFailed,
                    new SafeTelemetryProperties()
                        .Add("tenantId", request.TenantId)
                        .Add("correlationId", request.CorrelationId)
                        .Add("sessionId", request.Session.SessionId)
                        .Add("registrationId", request.Registration.RegistrationId)
                        .Add("channel", channel)
                        .Add("failureReason", reason)
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Notification telemetry is best-effort.
        }
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
}
