using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Ports.Messaging;

namespace RNM.Platform.Infrastructure.Messaging;

public sealed class AzureQueueConfirmationRetryScheduler : IConfirmationRetryScheduler
{
    public const string QueueName = "confirmation-retries";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly QueueClient? queueClient;

    public AzureQueueConfirmationRetryScheduler()
    {
        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            queueClient = new QueueClient(
                connectionString,
                QueueName,
                new QueueClientOptions
                {
                    MessageEncoding = QueueMessageEncoding.Base64
                });
        }
    }

    public async Task<bool> ScheduleAsync(
        ConfirmationRetryRequest request,
        CancellationToken cancellationToken)
    {
        if (queueClient is null)
        {
            return false;
        }

        try
        {
            await queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await queueClient.SendMessageAsync(
                    JsonSerializer.Serialize(request, JsonOptions),
                    visibilityTimeout: TimeSpan.Zero,
                    timeToLive: TimeSpan.FromDays(7),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
