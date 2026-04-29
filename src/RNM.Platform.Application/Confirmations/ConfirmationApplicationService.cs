using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Messaging;

namespace RNM.Platform.Application.Confirmations;

public sealed class ConfirmationApplicationService
{
    private readonly ISmsSender smsSender;
    private readonly IEmailSender emailSender;
    private readonly IEventLogger eventLogger;

    public ConfirmationApplicationService(
        ISmsSender smsSender,
        IEmailSender emailSender,
        IEventLogger eventLogger)
    {
        this.smsSender = smsSender;
        this.emailSender = emailSender;
        this.eventLogger = eventLogger;
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

        var smsResult = await SendSmsAsync(request, cancellationToken).ConfigureAwait(false);
        var emailResult = await SendEmailAsync(request, cancellationToken).ConfigureAwait(false);
        return new BookingConfirmationResult(smsResult, emailResult);
    }

    private async Task<ConfirmationChannelResult> SendSmsAsync(
        BookingConfirmationRequest request,
        CancellationToken cancellationToken)
    {
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

        try
        {
            var sendResult = await smsSender
                .SendSmsAsync(
                    new SmsMessageRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.CustomerPhoneNumber,
                        RenderTemplate(request.Templates.SmsBodyTemplate, request)),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!sendResult.Succeeded)
            {
                var failed = Failed(ConfirmationChannel.Sms, ConfirmationFailureReason.SmsSendFailed);
                await LogAsync(TelemetryEventNames.SmsConfirmationFailed, request, failed, cancellationToken)
                    .ConfigureAwait(false);
                return failed;
            }

            var sent = Sent(ConfirmationChannel.Sms, sendResult.ProviderMessageId);
            await LogAsync(TelemetryEventNames.SmsConfirmationSent, request, sent, cancellationToken)
                .ConfigureAwait(false);
            return sent;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failed = Failed(ConfirmationChannel.Sms, ConfirmationFailureReason.SmsSenderException);
            await LogAsync(TelemetryEventNames.SmsConfirmationFailed, request, failed, cancellationToken)
                .ConfigureAwait(false);
            return failed;
        }
    }

    private async Task<ConfirmationChannelResult> SendEmailAsync(
        BookingConfirmationRequest request,
        CancellationToken cancellationToken)
    {
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

        try
        {
            var sendResult = await emailSender
                .SendEmailAsync(
                    new EmailMessageRequest(
                        request.TenantId,
                        request.CorrelationId,
                        request.CustomerEmail,
                        RenderTemplate(request.Templates.EmailSubjectTemplate, request),
                        RenderTemplate(request.Templates.EmailBodyTemplate, request)),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!sendResult.Succeeded)
            {
                var failed = Failed(ConfirmationChannel.Email, ConfirmationFailureReason.EmailSendFailed);
                await LogAsync(TelemetryEventNames.EmailConfirmationFailed, request, failed, cancellationToken)
                    .ConfigureAwait(false);
                return failed;
            }

            var sent = Sent(ConfirmationChannel.Email, sendResult.ProviderMessageId);
            await LogAsync(TelemetryEventNames.EmailConfirmationSent, request, sent, cancellationToken)
                .ConfigureAwait(false);
            return sent;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failed = Failed(ConfirmationChannel.Email, ConfirmationFailureReason.EmailSenderException);
            await LogAsync(TelemetryEventNames.EmailConfirmationFailed, request, failed, cancellationToken)
                .ConfigureAwait(false);
            return failed;
        }
    }

    private static string RenderTemplate(
        string template,
        BookingConfirmationRequest request)
    {
        var startsAt = request.BookingDecision.SelectedSlot?.StartsAt;
        var endsAt = request.BookingDecision.SelectedSlot?.EndsAt;

        return template
            .Replace("{{tenantId}}", request.TenantId, StringComparison.OrdinalIgnoreCase)
            .Replace("{{verticalId}}", request.VerticalId, StringComparison.OrdinalIgnoreCase)
            .Replace("{{serviceType}}", request.ServiceType ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{bookingStart}}", startsAt?.ToString("O") ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{bookingEnd}}", endsAt?.ToString("O") ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{bookingDate}}", startsAt?.ToString("yyyy-MM-dd") ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{bookingTime}}", startsAt?.ToString("HH:mm") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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

    private async Task LogAsync(
        string eventName,
        BookingConfirmationRequest request,
        ConfirmationChannelResult? result,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", request.CorrelationId)
            .Add("tenantId", request.TenantId)
            .Add("verticalId", request.VerticalId)
            .Add("bookingState", request.BookingDecision.State.ToString())
            .Add("crmState", request.CrmSyncResult.State.ToString())
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
}
