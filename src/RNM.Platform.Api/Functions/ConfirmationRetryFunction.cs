using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Compliance;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Classes;
using RNM.Platform.Application.Ports.Classes;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Infrastructure.Messaging;

namespace RNM.Platform.Api.Functions;

public sealed class ConfirmationRetryFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ISmsSender smsSender;
    private readonly IEmailSender emailSender;
    private readonly IEventLogger eventLogger;
    private readonly IClassSessionStore? classSessionStore;
    private readonly ISmsEligibilityGate smsEligibilityGate;

    public ConfirmationRetryFunction(
        ISmsSender smsSender,
        IEmailSender emailSender,
        IEventLogger eventLogger,
        ISmsEligibilityGate smsEligibilityGate,
        IClassSessionStore? classSessionStore = null)
    {
        this.smsSender = smsSender;
        this.emailSender = emailSender;
        this.eventLogger = eventLogger;
        this.smsEligibilityGate = smsEligibilityGate;
        this.classSessionStore = classSessionStore;
    }

    [Function("ConfirmationRetry")]
    public async Task RunAsync(
        [QueueTrigger(AzureQueueConfirmationRetryScheduler.QueueName, Connection = "AzureWebJobsStorage")]
        string message,
        CancellationToken cancellationToken)
    {
        var retry = JsonSerializer.Deserialize<ConfirmationRetryRequest>(message, JsonOptions)
            ?? throw new InvalidOperationException("Confirmation retry message is invalid.");

        var (outcome, skipReason) = retry.Kind switch
        {
            ConfirmationRetryKind.CustomerSms or ConfirmationRetryKind.BusinessSms =>
                await RetrySmsAsync(retry, cancellationToken).ConfigureAwait(false),
            ConfirmationRetryKind.CustomerEmail or ConfirmationRetryKind.BusinessEmail =>
                (await RetryEmailAsync(retry, cancellationToken).ConfigureAwait(false)
                    ? RetryDispatchOutcome.Sent
                    : RetryDispatchOutcome.Failed, null),
            _ => (RetryDispatchOutcome.Failed, (SmsEligibilitySkipReason?)null)
        };

        await LogAsync(
                outcome switch
                {
                    RetryDispatchOutcome.Sent => TelemetryEventNames.ConfirmationRetrySucceeded,
                    RetryDispatchOutcome.Skipped => TelemetryEventNames.ConfirmationRetrySkipped,
                    _ => TelemetryEventNames.ConfirmationRetryFailed
                },
                retry,
                outcome,
                skipReason,
                cancellationToken)
            .ConfigureAwait(false);

        if (outcome is RetryDispatchOutcome.Sent)
        {
            await TryUpdateClassRegistrationStatusAsync(retry, cancellationToken).ConfigureAwait(false);
        }

        if (outcome is RetryDispatchOutcome.Failed)
        {
            throw new InvalidOperationException("Confirmation retry provider call failed.");
        }
    }

    private async Task TryUpdateClassRegistrationStatusAsync(
        ConfirmationRetryRequest retry,
        CancellationToken cancellationToken)
    {
        if (classSessionStore is null || string.IsNullOrWhiteSpace(retry.ClassRegistrationId))
        {
            return;
        }

        var smsStatus = retry.Kind is ConfirmationRetryKind.CustomerSms
            ? ConfirmationChannelStatus.Sent.ToString()
            : null;
        var emailStatus = retry.Kind is ConfirmationRetryKind.CustomerEmail
            ? ConfirmationChannelStatus.Sent.ToString()
            : null;
        if (smsStatus is null && emailStatus is null)
        {
            return;
        }

        try
        {
            await classSessionStore
                .UpdateRegistrationNotificationStatusAsync(
                    new ClassNotificationStatusUpdate(
                        retry.TenantId,
                        retry.ClassRegistrationId,
                        retry.CorrelationId,
                        smsStatus,
                        emailStatus),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Delivery already succeeded; a status projection failure must not resend the message.
        }
    }

    private async Task<(RetryDispatchOutcome Outcome, SmsEligibilitySkipReason? SkipReason)> RetrySmsAsync(
        ConfirmationRetryRequest retry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(retry.Destination)
            || string.IsNullOrWhiteSpace(retry.Body))
        {
            return (RetryDispatchOutcome.Failed, null);
        }

        var category = retry.SmsCategory
            ?? (retry.Kind is ConfirmationRetryKind.BusinessSms
                ? SmsMessageCategory.InternalOperational
                : SmsMessageCategory.BookingConfirmation);
        var eligibility = await smsEligibilityGate.EvaluateAsync(
                new SmsEligibilityRequest(
                    retry.TenantId,
                    retry.CorrelationId,
                    category,
                    retry.ProviderContactId,
                    retry.Destination)
                {
                    IsRetry = true,
                    OriginalRequestedAt = retry.OriginalRequestedAt
                },
                cancellationToken)
            .ConfigureAwait(false);
        // Messages queued before eligibility context existed have no timestamp; the gate skips them as RetryMissingTimestamp.
        if (!eligibility.IsEligible || eligibility.Proof is null)
        {
            return (RetryDispatchOutcome.Skipped, eligibility.SkipReason);
        }

        var result = await smsSender.SendSmsAsync(
                new SmsMessageRequest(
                    retry.TenantId,
                    retry.CorrelationId,
                    retry.Destination,
                    retry.Body,
                    eligibility.Proof),
                cancellationToken)
            .ConfigureAwait(false);
        return (result.Succeeded ? RetryDispatchOutcome.Sent : RetryDispatchOutcome.Failed, null);
    }

    private async Task<bool> RetryEmailAsync(
        ConfirmationRetryRequest retry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(retry.Destination)
            || string.IsNullOrWhiteSpace(retry.Subject)
            || string.IsNullOrWhiteSpace(retry.Body))
        {
            return false;
        }

        var result = await emailSender.SendEmailAsync(
                new EmailMessageRequest(
                    retry.TenantId,
                    retry.CorrelationId,
                    retry.Destination,
                    retry.Subject,
                    retry.Body),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded;
    }

    private async Task LogAsync(
        string eventName,
        ConfirmationRetryRequest retry,
        RetryDispatchOutcome outcome,
        SmsEligibilitySkipReason? skipReason,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", retry.CorrelationId)
            .Add("tenantId", retry.TenantId)
            .Add("retryKind", retry.Kind.ToString())
            .Add("outcome", outcome switch
            {
                RetryDispatchOutcome.Sent => "succeeded",
                RetryDispatchOutcome.Skipped => "skipped",
                _ => "failed"
            })
            .Add("skipReason", skipReason?.ToString() ?? string.Empty)
            .ToDictionary();

        try
        {
            await eventLogger.LogEventAsync(eventName, properties, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Retry telemetry is best-effort.
        }
    }

    private enum RetryDispatchOutcome
    {
        Sent = 0,
        Skipped = 1,
        Failed = 2
    }
}
