using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using RNM.Platform.Application.FollowUps;
using RNM.Platform.Application.Ports.FollowUps;

namespace RNM.Platform.Infrastructure.FollowUps;

public sealed class AzureTableFollowUpStore : IFollowUpStore
{
    private const string DefaultTableName = "RnmFollowUpDue";
    private const int MaxTableStringLength = 32000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string? connectionString;
    private readonly string tableName;

    public AzureTableFollowUpStore()
    {
        connectionString = GetConnectionString();
        tableName = GetSetting("RNM_FOLLOW_UP_DUE_TABLE_NAME", DefaultTableName);
    }

    public async Task ScheduleFollowUpAsync(
        FollowUpDueRecord followUp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Azure Table connection string is missing.");
        }

        var table = await GetTableClientAsync(cancellationToken).ConfigureAwait(false);
        var entity = new TableEntity(followUp.TenantId, followUp.RowKey)
        {
            ["ProviderContactId"] = SafeValue(followUp.ProviderContactId),
            ["SequenceId"] = SafeValue(followUp.SequenceId),
            ["StepIndex"] = followUp.StepIndex,
            ["TriggerEventType"] = SafeValue(followUp.TriggerEventType),
            ["Channel"] = SafeValue(followUp.Channel),
            ["DueAt"] = followUp.DueAt,
            ["Status"] = FollowUpStatuses.Pending,
            ["CorrelationId"] = SafeValue(followUp.CorrelationId),
            ["CustomerName"] = SafeValue(followUp.CustomerName),
            ["CustomerPhoneNumber"] = SafeValue(followUp.CustomerPhoneNumber),
            ["CustomerEmail"] = SafeValue(followUp.CustomerEmail),
            ["Reason"] = SafeTableString(followUp.Reason),
            ["CreatedAt"] = followUp.CreatedAt,
            ["UpdatedAt"] = DateTimeOffset.UtcNow,
            ["AttributesJson"] = SerializeAttributes(followUp.Attributes)
        };

        try
        {
            await table.AddEntityAsync(entity, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            // Idempotency: the same trigger/sequence/step/due time was already scheduled.
        }
    }

    public async Task<IReadOnlyCollection<FollowUpDueRecord>> GetDueFollowUpsAsync(
        string tenantId,
        DateTimeOffset dueAt,
        int maxItems,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return [];
        }

        var take = Math.Clamp(maxItems, 1, 100);
        try
        {
            var table = await GetTableClientAsync(cancellationToken).ConfigureAwait(false);
            var followUps = new List<FollowUpDueRecord>();
            var filter = $"PartitionKey eq '{EscapeODataString(tenantId)}' and Status eq '{FollowUpStatuses.Pending}'";
            await foreach (var entity in table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
            {
                if (ReadDateTimeOffset(entity, "DueAt") is not { } entityDueAt || entityDueAt > dueAt)
                {
                    continue;
                }

                followUps.Add(Materialize(entity));
                if (followUps.Count >= take)
                {
                    break;
                }
            }

            return followUps;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return [];
        }
    }

    public async Task<bool> TryClaimFollowUpAsync(
        string tenantId,
        string rowKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            var table = await GetTableClientAsync(cancellationToken).ConfigureAwait(false);
            var existing = await table.GetEntityAsync<TableEntity>(
                    tenantId,
                    rowKey,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(ReadString(existing.Value, "Status"), FollowUpStatuses.Pending, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            existing.Value["Status"] = FollowUpStatuses.Claimed;
            existing.Value["ClaimedAt"] = DateTimeOffset.UtcNow;
            existing.Value["UpdatedAt"] = DateTimeOffset.UtcNow;
            existing.Value["CorrelationId"] = correlationId;
            await table.UpdateEntityAsync(
                    existing.Value,
                    existing.Value.ETag,
                    TableUpdateMode.Merge,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException)
        {
            return false;
        }
    }

    public async Task MarkFollowUpAsync(
        string tenantId,
        string rowKey,
        string status,
        string correlationId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        try
        {
            var table = await GetTableClientAsync(cancellationToken).ConfigureAwait(false);
            var entity = new TableEntity(tenantId, rowKey)
            {
                ["Status"] = status,
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = correlationId
            };
            if (!string.IsNullOrWhiteSpace(reason))
            {
                entity["Reason"] = SafeTableString(reason);
            }

            if (string.Equals(status, FollowUpStatuses.Sent, StringComparison.OrdinalIgnoreCase))
            {
                entity["SentAt"] = DateTimeOffset.UtcNow;
            }

            await table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException)
        {
            // Follow-up status is best-effort; the application service emits telemetry.
        }
    }

    public async Task<int> CountSentForContactOnDateAsync(
        string tenantId,
        string providerContactId,
        DateOnly localDate,
        string timeZone,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return 0;
        }

        var zone = ResolveTimeZone(timeZone);
        var count = 0;
        try
        {
            var table = await GetTableClientAsync(cancellationToken).ConfigureAwait(false);
            var filter = $"PartitionKey eq '{EscapeODataString(tenantId)}' and ProviderContactId eq '{EscapeODataString(providerContactId)}' and Status eq '{FollowUpStatuses.Sent}'";
            await foreach (var entity in table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
            {
                var sentAt = ReadDateTimeOffset(entity, "SentAt");
                if (sentAt is null)
                {
                    continue;
                }

                var sentLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(sentAt.Value, zone).Date);
                if (sentLocalDate == localDate)
                {
                    count++;
                }
            }
        }
        catch (RequestFailedException)
        {
            return 0;
        }

        return count;
    }

    private async Task<TableClient> GetTableClientAsync(CancellationToken cancellationToken)
    {
        var client = new TableClient(connectionString!, tableName);
        await client.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    private static FollowUpDueRecord Materialize(TableEntity entity) =>
        new(
            entity.PartitionKey,
            entity.RowKey,
            ReadString(entity, "ProviderContactId") ?? string.Empty,
            ReadString(entity, "SequenceId") ?? string.Empty,
            ReadInt(entity, "StepIndex") ?? 0,
            ReadString(entity, "TriggerEventType") ?? string.Empty,
            ReadString(entity, "Channel") ?? string.Empty,
            ReadDateTimeOffset(entity, "DueAt") ?? DateTimeOffset.MinValue,
            ReadString(entity, "Status") ?? string.Empty,
            ReadString(entity, "CorrelationId") ?? string.Empty)
        {
            CustomerName = ReadString(entity, "CustomerName"),
            CustomerPhoneNumber = ReadString(entity, "CustomerPhoneNumber"),
            CustomerEmail = ReadString(entity, "CustomerEmail"),
            Reason = ReadString(entity, "Reason"),
            CreatedAt = ReadDateTimeOffset(entity, "CreatedAt") ?? DateTimeOffset.MinValue,
            Attributes = ReadAttributes(entity)
        };

    private static string SerializeAttributes(IReadOnlyDictionary<string, string> attributes) =>
        SafeTableString(JsonSerializer.Serialize(attributes, JsonOptions));

    private static IReadOnlyDictionary<string, string> ReadAttributes(TableEntity entity)
    {
        var json = ReadString(entity, "AttributesJson");
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
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

    private static string? ReadString(TableEntity entity, string propertyName) =>
        entity.TryGetValue(propertyName, out var value) ? value?.ToString() : null;

    private static int? ReadInt(TableEntity entity, string propertyName)
    {
        if (!entity.TryGetValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue when longValue <= int.MaxValue && longValue >= int.MinValue => (int)longValue,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset? ReadDateTimeOffset(TableEntity entity, string propertyName)
    {
        if (!entity.TryGetValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(dateTime),
            string text when DateTimeOffset.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static string SafeValue(string? value) => value?.Trim() ?? string.Empty;

    private static string SafeTableString(string? value)
    {
        var safe = SafeValue(value);
        return safe.Length <= MaxTableStringLength ? safe : safe[..MaxTableStringLength];
    }

    private static string? GetConnectionString()
    {
        var explicitConnectionString = Environment.GetEnvironmentVariable("RNM_CRM_TABLE_STORAGE_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            return explicitConnectionString;
        }

        var azureWebJobsStorage = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        return string.IsNullOrWhiteSpace(azureWebJobsStorage) ? null : azureWebJobsStorage;
    }

    private static string GetSetting(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static string EscapeODataString(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
