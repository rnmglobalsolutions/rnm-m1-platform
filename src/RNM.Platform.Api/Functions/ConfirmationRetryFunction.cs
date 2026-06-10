using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Infrastructure.Messaging;

namespace RNM.Platform.Api.Functions;

public sealed class ConfirmationRetryFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ISmsSender smsSender;
    private readonly IEmailSender emailSender;
    private readonly IEventLogger eventLogger;

    public ConfirmationRetryFunction(
        ISmsSender smsSender,
        IEmailSender emailSender,
        IEventLogger eventLogger)
    {
        this.smsSender = smsSender;
        this.emailSender = emailSender;
        this.eventLogger = eventLogger;
    }

    [Function("ConfirmationRetry")]
    public async Task RunAsync(
        [QueueTrigger(AzureQueueConfirmationRetryScheduler.QueueName, Connection = "AzureWebJobsStorage")]
        string message,
        CancellationToken cancellationToken)
    {
        var retry = JsonSerializer.Deserialize<ConfirmationRetryRequest>(message, JsonOptions)
            ?? throw new InvalidOperationException("Confirmation retry message is invalid.");

        var succeeded = retry.Kind switch
        {
            ConfirmationRetryKind.CustomerSms or ConfirmationRetryKind.BusinessSms =>
                await RetrySmsAsync(retry, cancellationToken).ConfigureAwait(false),
            ConfirmationRetryKind.CustomerEmail or ConfirmationRetryKind.BusinessEmail =>
                await RetryEmailAsync(retry, cancellationToken).ConfigureAwait(false),
            _ => false
        };

        await LogAsync(
                succeeded
                    ? TelemetryEventNames.ConfirmationRetrySucceeded
                    : TelemetryEventNames.ConfirmationRetryFailed,
                retry,
                succeeded,
                cancellationToken)
            .ConfigureAwait(false);

        if (!succeeded)
        {
            throw new InvalidOperationException("Confirmation retry provider call failed.");
        }
    }

    private async Task<bool> RetrySmsAsync(
        ConfirmationRetryRequest retry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(retry.Destination)
            || string.IsNullOrWhiteSpace(retry.Body))
        {
            return false;
        }

        var result = await smsSender.SendSmsAsync(
                new SmsMessageRequest(
                    retry.TenantId,
                    retry.CorrelationId,
                    retry.Destination,
                    retry.Body),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded;
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
        bool succeeded,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", retry.CorrelationId)
            .Add("tenantId", retry.TenantId)
            .Add("retryKind", retry.Kind.ToString())
            .Add("outcome", succeeded ? "succeeded" : "failed")
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
}
