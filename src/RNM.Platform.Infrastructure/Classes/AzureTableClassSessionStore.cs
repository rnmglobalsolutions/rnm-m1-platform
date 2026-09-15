using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;
using RNM.Platform.Application.Classes;
using RNM.Platform.Application.Ports.Classes;

namespace RNM.Platform.Infrastructure.Classes;

public sealed class AzureTableClassSessionStore : IClassSessionStore
{
    private const string DefaultSessionsTableName = "RnmClassSessions";
    private const string DefaultRegistrationsTableName = "RnmClassRegistrations";
    private const string DefaultReminderDueTableName = "RnmClassReminderDue";
    private const int MaxTableStringLength = 32000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string? connectionString;
    private readonly string sessionsTableName;
    private readonly string registrationsTableName;
    private readonly string reminderDueTableName;

    public AzureTableClassSessionStore()
    {
        connectionString = GetConnectionString();
        sessionsTableName = GetSetting("RNM_CLASS_SESSIONS_TABLE_NAME", DefaultSessionsTableName);
        registrationsTableName = GetSetting("RNM_CLASS_REGISTRATIONS_TABLE_NAME", DefaultRegistrationsTableName);
        reminderDueTableName = GetSetting("RNM_CLASS_REMINDER_DUE_TABLE_NAME", DefaultReminderDueTableName);
    }

    public async Task<ClassSessionUpsertResult> UpsertSessionAsync(
        ClassSessionUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return FailedSession(ClassFailureReason.StorageFailure, "Azure Table connection string is missing.");
        }

        try
        {
            var table = await GetTableClientAsync(sessionsTableName, cancellationToken).ConfigureAwait(false);
            var entity = new TableEntity(request.TenantId, request.SessionId)
            {
                ["Title"] = SafeTableString(request.Title),
                ["StartsAt"] = request.StartsAt,
                ["TimeZone"] = SafeValue(request.TimeZone),
                ["ZoomUrl"] = SafeTableString(request.ZoomUrl),
                ["CampaignId"] = SafeValue(request.CampaignId),
                ["AttributesJson"] = SerializeMetadata(request.Attributes),
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };
            AddIfPresent(entity, "EndsAt", request.EndsAt);
            if (request.Capacity.HasValue)
            {
                entity["Capacity"] = request.Capacity.Value;
            }

            await table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);
            return new ClassSessionUpsertResult(true, MaterializeSession(entity));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedSession(ClassFailureReason.StorageFailure, "Class session write failed.");
        }
    }

    public async Task<ClassSessionRecord?> GetSessionAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            var table = await GetTableClientAsync(sessionsTableName, cancellationToken).ConfigureAwait(false);
            var response = await table.GetEntityAsync<TableEntity>(
                    tenantId,
                    sessionId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MaterializeSession(response.Value);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<ClassRegistrationRecord?> GetRegistrationAsync(
        string tenantId,
        string registrationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            var table = await GetTableClientAsync(registrationsTableName, cancellationToken).ConfigureAwait(false);
            var response = await table.GetEntityAsync<TableEntity>(
                    tenantId,
                    registrationId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return MaterializeRegistration(response.Value);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyCollection<ClassRegistrationRecord>> GetRegistrationsBySessionAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return [];
        }

        try
        {
            var table = await GetTableClientAsync(registrationsTableName, cancellationToken).ConfigureAwait(false);
            var registrations = new List<ClassRegistrationRecord>();
            var filter = $"PartitionKey eq '{EscapeODataString(tenantId)}' and SessionId eq '{EscapeODataString(sessionId)}'";
            await foreach (var entity in table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
            {
                registrations.Add(MaterializeRegistration(entity));
            }

            return registrations;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return [];
        }
    }

    public async Task<ClassRegistrationRecord?> FindRegistrationAsync(
        string tenantId,
        string sessionId,
        string providerContactId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            var table = await GetTableClientAsync(registrationsTableName, cancellationToken).ConfigureAwait(false);
            var filter = $"PartitionKey eq '{EscapeODataString(tenantId)}' and SessionId eq '{EscapeODataString(sessionId)}' and ProviderContactId eq '{EscapeODataString(providerContactId)}'";
            await foreach (var entity in table.QueryAsync<TableEntity>(filter, maxPerPage: 1, cancellationToken: cancellationToken))
            {
                return MaterializeRegistration(entity);
            }

            return null;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<ClassRegistrationRecord> UpsertRegistrationAsync(
        ClassRegistrationRecord registration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Azure Table connection string is missing.");
        }

        var table = await GetTableClientAsync(registrationsTableName, cancellationToken).ConfigureAwait(false);
        var entity = new TableEntity(registration.TenantId, registration.RegistrationId)
        {
            ["RegistrationId"] = registration.RegistrationId,
            ["SessionId"] = registration.SessionId,
            ["ProviderContactId"] = registration.ProviderContactId,
            ["CustomerName"] = SafeValue(registration.CustomerName),
            ["CustomerPhoneNumber"] = SafeValue(registration.CustomerPhoneNumber),
            ["CustomerEmail"] = SafeValue(registration.CustomerEmail),
            ["Source"] = SafeValue(registration.Source),
            ["CampaignId"] = SafeValue(registration.CampaignId),
            ["ConsentStatus"] = SafeValue(registration.ConsentStatus),
            ["ConfirmationSmsStatus"] = SafeValue(registration.ConfirmationSmsStatus),
            ["ConfirmationEmailStatus"] = SafeValue(registration.ConfirmationEmailStatus),
            ["Status"] = ClassRegistrationStatuses.Registered,
            ["AttributesJson"] = SerializeMetadata(registration.Attributes),
            ["CreatedAt"] = registration.CreatedAt,
            ["UpdatedAt"] = DateTimeOffset.UtcNow
        };

        await table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);
        return MaterializeRegistration(entity);
    }

    public async Task UpdateRegistrationNotificationStatusAsync(
        ClassNotificationStatusUpdate update,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var table = await GetTableClientAsync(registrationsTableName, cancellationToken).ConfigureAwait(false);
        var entity = new TableEntity(update.TenantId, update.RegistrationId)
        {
            ["ConfirmationSmsStatus"] = SafeValue(update.SmsStatus),
            ["ConfirmationEmailStatus"] = SafeValue(update.EmailStatus),
            ["UpdatedAt"] = DateTimeOffset.UtcNow,
            ["CorrelationId"] = update.CorrelationId
        };

        await table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);
    }

    public async Task ScheduleRemindersAsync(
        ClassReminderScheduleRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Azure Table connection string is missing.");
        }

        var table = await GetTableClientAsync(reminderDueTableName, cancellationToken).ConfigureAwait(false);
        foreach (var offsetMinutes in request.ReminderOffsetsMinutes.Distinct().Where(value => value > 0))
        {
            var dueAt = request.Session.StartsAt.AddMinutes(-offsetMinutes);
            if (dueAt <= DateTimeOffset.UtcNow)
            {
                continue;
            }

            var rowKey = CreateReminderRowKey(dueAt, request.Registration.RegistrationId, offsetMinutes);
            var entity = new TableEntity(request.TenantId, rowKey)
            {
                ["TargetType"] = ReminderTargetTypes.ClassSession,
                ["TargetId"] = request.Session.SessionId,
                ["RegistrationId"] = request.Registration.RegistrationId,
                ["SessionId"] = request.Session.SessionId,
                ["ProviderContactId"] = request.Registration.ProviderContactId,
                ["ReminderKind"] = $"{offsetMinutes}m_before",
                ["DueAt"] = dueAt,
                ["Status"] = ClassReminderStatuses.Pending,
                ["CreatedAt"] = DateTimeOffset.UtcNow,
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };

            await table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ScheduleAppointmentRemindersAsync(
        AppointmentReminderScheduleRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Azure Table connection string is missing.");
        }

        if (string.IsNullOrWhiteSpace(request.ProviderContactId)
            || string.IsNullOrWhiteSpace(request.ProviderBookingId)
            || request.StartsAt <= DateTimeOffset.UtcNow)
        {
            return;
        }

        var table = await GetTableClientAsync(reminderDueTableName, cancellationToken).ConfigureAwait(false);
        foreach (var offsetMinutes in request.ReminderOffsetsMinutes.Distinct().Where(value => value > 0))
        {
            var dueAt = request.StartsAt.AddMinutes(-offsetMinutes);
            if (dueAt <= DateTimeOffset.UtcNow)
            {
                continue;
            }

            var rowKey = CreateReminderRowKey(dueAt, CreateReminderTargetRowKeyComponent(request.ProviderBookingId), offsetMinutes);
            var entity = new TableEntity(request.TenantId, rowKey)
            {
                ["TargetType"] = ReminderTargetTypes.Appointment,
                ["TargetId"] = request.ProviderBookingId,
                ["RegistrationId"] = string.Empty,
                ["SessionId"] = string.Empty,
                ["ProviderContactId"] = request.ProviderContactId,
                ["ProviderBookingId"] = request.ProviderBookingId,
                ["CustomerName"] = SafeValue(request.CustomerName),
                ["CustomerPhoneNumber"] = SafeValue(request.CustomerPhoneNumber),
                ["CustomerEmail"] = SafeValue(request.CustomerEmail),
                ["BookingLabel"] = SafeValue(request.BookingLabel),
                ["TimeZone"] = SafeValue(request.TimeZone),
                ["OnlineMeetingUrl"] = SafeTableString(request.OnlineMeetingUrl),
                ["ReminderKind"] = $"{offsetMinutes}m_before",
                ["DueAt"] = dueAt,
                ["Status"] = ClassReminderStatuses.Pending,
                ["AttributesJson"] = SerializeMetadata(request.Attributes),
                ["CreatedAt"] = DateTimeOffset.UtcNow,
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };
            AddIfPresent(entity, "StartsAt", request.StartsAt);
            AddIfPresent(entity, "EndsAt", request.EndsAt);

            await table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyCollection<ClassReminderRecord>> GetDueRemindersAsync(
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
            var table = await GetTableClientAsync(reminderDueTableName, cancellationToken).ConfigureAwait(false);
            var reminders = new List<ClassReminderRecord>();
            var filter = $"PartitionKey eq '{EscapeODataString(tenantId)}' and Status eq '{ClassReminderStatuses.Pending}'";
            await foreach (var entity in table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
            {
                if (ReadDateTimeOffset(entity, "DueAt") is not { } entityDueAt || entityDueAt > dueAt)
                {
                    continue;
                }

                reminders.Add(MaterializeReminder(entity));
                if (reminders.Count >= take)
                {
                    break;
                }
            }

            return reminders;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return [];
        }
    }

    public async Task<IReadOnlyCollection<ClassReminderRecord>> GetRemindersBySessionAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return [];
        }

        try
        {
            var table = await GetTableClientAsync(reminderDueTableName, cancellationToken).ConfigureAwait(false);
            var reminders = new List<ClassReminderRecord>();
            var filter = $"PartitionKey eq '{EscapeODataString(tenantId)}' and SessionId eq '{EscapeODataString(sessionId)}'";
            await foreach (var entity in table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
            {
                reminders.Add(MaterializeReminder(entity));
            }

            return reminders;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return [];
        }
    }

    public async Task<bool> TryClaimReminderAsync(
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
            var table = await GetTableClientAsync(reminderDueTableName, cancellationToken).ConfigureAwait(false);
            var existing = await table.GetEntityAsync<TableEntity>(
                    tenantId,
                    rowKey,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(ReadString(existing.Value, "Status"), ClassReminderStatuses.Pending, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            existing.Value["Status"] = ClassReminderStatuses.Claimed;
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

    public async Task MarkReminderAsync(
        string tenantId,
        string rowKey,
        string status,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        try
        {
            var table = await GetTableClientAsync(reminderDueTableName, cancellationToken).ConfigureAwait(false);
            var entity = new TableEntity(tenantId, rowKey)
            {
                ["Status"] = status,
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = correlationId
            };
            if (string.Equals(status, ClassReminderStatuses.Sent, StringComparison.OrdinalIgnoreCase))
            {
                entity["SentAt"] = DateTimeOffset.UtcNow;
            }

            await table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException)
        {
            // Reminder status is best-effort; telemetry lives in the application service.
        }
    }

    private async Task<TableClient> GetTableClientAsync(string tableName, CancellationToken cancellationToken)
    {
        var client = new TableClient(connectionString!, tableName);
        await client.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    private static ClassSessionRecord MaterializeSession(TableEntity entity) =>
        new(
            entity.PartitionKey,
            entity.RowKey,
            ReadString(entity, "Title") ?? string.Empty,
            ReadDateTimeOffset(entity, "StartsAt") ?? DateTimeOffset.MinValue,
            ReadDateTimeOffset(entity, "EndsAt"),
            ReadString(entity, "TimeZone") ?? string.Empty,
            ReadString(entity, "ZoomUrl") ?? string.Empty,
            ReadInt(entity, "Capacity"),
            ReadString(entity, "CampaignId"),
            ReadMetadata(entity));

    private static ClassRegistrationRecord MaterializeRegistration(TableEntity entity) =>
        new(
            entity.PartitionKey,
            ReadString(entity, "RegistrationId") ?? entity.RowKey,
            ReadString(entity, "SessionId") ?? string.Empty,
            ReadString(entity, "ProviderContactId") ?? string.Empty,
            ReadString(entity, "CustomerName") ?? string.Empty,
            ReadString(entity, "CustomerPhoneNumber"),
            ReadString(entity, "CustomerEmail"),
            ReadString(entity, "Source") ?? string.Empty,
            ReadString(entity, "CampaignId"),
            ReadString(entity, "ConsentStatus") ?? string.Empty,
            ReadDateTimeOffset(entity, "CreatedAt") ?? DateTimeOffset.MinValue,
            ReadMetadata(entity))
        {
            ConfirmationSmsStatus = ReadString(entity, "ConfirmationSmsStatus"),
            ConfirmationEmailStatus = ReadString(entity, "ConfirmationEmailStatus")
        };

    private static ClassReminderRecord MaterializeReminder(TableEntity entity) =>
        new(
            entity.PartitionKey,
            entity.RowKey,
            ReadString(entity, "RegistrationId") ?? string.Empty,
            ReadString(entity, "SessionId") ?? string.Empty,
            ReadString(entity, "ProviderContactId") ?? string.Empty,
            ReadString(entity, "ReminderKind") ?? string.Empty,
            ReadDateTimeOffset(entity, "DueAt") ?? DateTimeOffset.MinValue,
            ReadString(entity, "Status") ?? string.Empty,
            ReadString(entity, "CorrelationId") ?? string.Empty)
        {
            TargetType = ReadString(entity, "TargetType") ?? ReminderTargetTypes.ClassSession,
            TargetId = ReadString(entity, "TargetId") ?? ReadString(entity, "SessionId") ?? string.Empty,
            CustomerName = ReadString(entity, "CustomerName"),
            CustomerPhoneNumber = ReadString(entity, "CustomerPhoneNumber"),
            CustomerEmail = ReadString(entity, "CustomerEmail"),
            BookingLabel = ReadString(entity, "BookingLabel"),
            StartsAt = ReadDateTimeOffset(entity, "StartsAt"),
            EndsAt = ReadDateTimeOffset(entity, "EndsAt"),
            TimeZone = ReadString(entity, "TimeZone"),
            OnlineMeetingUrl = ReadString(entity, "OnlineMeetingUrl"),
            Attributes = ReadMetadata(entity)
        };

    private static string CreateReminderRowKey(
        DateTimeOffset dueAt,
        string registrationId,
        int offsetMinutes) =>
        $"{dueAt.UtcDateTime.Ticks:D20}|{offsetMinutes:D5}|{registrationId}";

    private static string CreateReminderTargetRowKeyComponent(string targetId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetId))).ToLowerInvariant();

    private static ClassSessionUpsertResult FailedSession(ClassFailureReason reason, string message) =>
        new(false, null, reason, message);

    private static void AddIfPresent(TableEntity entity, string propertyName, DateTimeOffset? value)
    {
        if (value.HasValue)
        {
            entity[propertyName] = value.Value;
        }
    }

    private static string SafeValue(string? value) => value?.Trim() ?? string.Empty;

    private static string SafeTableString(string? value)
    {
        var safe = SafeValue(value);
        return safe.Length <= MaxTableStringLength ? safe : safe[..MaxTableStringLength];
    }

    private static string SerializeMetadata(IReadOnlyDictionary<string, string> metadata) =>
        SafeTableString(JsonSerializer.Serialize(metadata, JsonOptions));

    private static IReadOnlyDictionary<string, string> ReadMetadata(TableEntity entity)
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
