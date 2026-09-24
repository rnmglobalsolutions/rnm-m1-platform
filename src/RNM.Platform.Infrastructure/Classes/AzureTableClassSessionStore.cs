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
    private static readonly TimeSpan ReminderClaimLease = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RegistrationReservationLease = TimeSpan.FromMinutes(15);
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
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var existing = await table
                    .GetEntityIfExistsAsync<TableEntity>(request.TenantId, request.SessionId, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var entity = existing.HasValue
                    ? existing.Value!
                    : new TableEntity(request.TenantId, request.SessionId);
                entity["Title"] = SafeTableString(request.Title);
                entity["StartsAt"] = request.StartsAt;
                entity["TimeZone"] = SafeValue(request.TimeZone);
                entity["ZoomUrl"] = SafeTableString(request.ZoomUrl);
                entity["Status"] = SafeValue(request.Status).ToLowerInvariant();
                entity["CampaignId"] = SafeValue(request.CampaignId);
                entity["AttributesJson"] = SerializeMetadata(request.Attributes);
                entity["UpdatedAt"] = DateTimeOffset.UtcNow;
                entity["CorrelationId"] = request.CorrelationId;
                if (!existing.HasValue)
                {
                    entity["CreatedAt"] = DateTimeOffset.UtcNow;
                    entity["RegisteredCount"] = 0;
                }

                SetOrRemove(entity, "EndsAt", request.EndsAt);
                SetOrRemove(entity, "Capacity", request.Capacity);

                try
                {
                    if (existing.HasValue)
                    {
                        await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await table.AddEntityAsync(entity, cancellationToken).ConfigureAwait(false);
                    }

                    return new ClassSessionUpsertResult(true, MaterializeSession(entity));
                }
                catch (RequestFailedException exception) when (exception.Status is 409 or 412)
                {
                    // A concurrent capacity reservation or session update won; reload and retry.
                }
            }

            return FailedSession(ClassFailureReason.StorageFailure, "Class session changed concurrently and could not be updated.");
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

    public async Task<ClassRegistrationReservationResult> TryReserveRegistrationAsync(
        string tenantId,
        string sessionId,
        string registrationId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new ClassRegistrationReservationResult(false, Message: "Azure Table connection string is missing.");
        }

        var sessions = await GetTableClientAsync(sessionsTableName, cancellationToken).ConfigureAwait(false);
        var registrations = await GetTableClientAsync(registrationsTableName, cancellationToken).ConfigureAwait(false);
        var reservationRowKey = CreateReservationRowKey(sessionId, registrationId);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var existingReservation = await sessions
                .GetEntityIfExistsAsync<TableEntity>(tenantId, reservationRowKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (existingReservation.HasValue)
            {
                var existingReservationEntity = existingReservation.Value!;
                var createdAt = ReadDateTimeOffset(existingReservationEntity, "CreatedAt");
                if (createdAt is null || createdAt > DateTimeOffset.UtcNow.Subtract(RegistrationReservationLease))
                {
                    return new ClassRegistrationReservationResult(
                        false,
                        AlreadyReserved: true,
                        Message: "Class registration is already being processed.");
                }

                existingReservationEntity["CreatedAt"] = DateTimeOffset.UtcNow;
                existingReservationEntity["CorrelationId"] = correlationId;
                try
                {
                    await sessions.UpdateEntityAsync(existingReservationEntity, existingReservationEntity.ETag, TableUpdateMode.Merge, cancellationToken)
                        .ConfigureAwait(false);
                    return new ClassRegistrationReservationResult(true, AlreadyReserved: true);
                }
                catch (RequestFailedException exception) when (exception.Status is 409 or 412)
                {
                    continue;
                }
            }

            Response<TableEntity> sessionResponse;
            try
            {
                sessionResponse = await sessions
                    .GetEntityAsync<TableEntity>(tenantId, sessionId, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                return new ClassRegistrationReservationResult(false, Message: "Class session was not found.");
            }

            var session = sessionResponse.Value;
            var registeredCount = ReadInt(session, "RegisteredCount")
                ?? await CountRegistrationsAsync(registrations, tenantId, sessionId, cancellationToken).ConfigureAwait(false);
            var capacity = ReadInt(session, "Capacity");
            if (capacity.HasValue && registeredCount >= capacity.Value)
            {
                return new ClassRegistrationReservationResult(
                    false,
                    CapacityReached: true,
                    Message: "Class session capacity has been reached.");
            }

            session["RegisteredCount"] = registeredCount + 1;
            session["UpdatedAt"] = DateTimeOffset.UtcNow;
            session["CorrelationId"] = correlationId;
            var reservation = new TableEntity(tenantId, reservationRowKey)
            {
                ["SessionId"] = sessionId,
                ["RegistrationId"] = registrationId,
                ["Status"] = "reserved",
                ["CreatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = correlationId
            };

            try
            {
                await sessions.SubmitTransactionAsync(
                        [
                            new TableTransactionAction(TableTransactionActionType.UpdateMerge, session, session.ETag),
                            new TableTransactionAction(TableTransactionActionType.Add, reservation)
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
                return new ClassRegistrationReservationResult(true);
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412)
            {
                // A duplicate reservation or concurrent capacity update is resolved on the next iteration.
            }
        }

        return new ClassRegistrationReservationResult(false, Message: "Class registration reservation could not be completed.");
    }

    public async Task ReleaseRegistrationReservationAsync(
        string tenantId,
        string sessionId,
        string registrationId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var table = await GetTableClientAsync(sessionsTableName, cancellationToken).ConfigureAwait(false);
        var reservationRowKey = CreateReservationRowKey(sessionId, registrationId);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var reservation = await table
                .GetEntityIfExistsAsync<TableEntity>(tenantId, reservationRowKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!reservation.HasValue)
            {
                return;
            }

            var session = await table
                .GetEntityIfExistsAsync<TableEntity>(tenantId, sessionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!session.HasValue)
            {
                await table.DeleteEntityAsync(tenantId, reservationRowKey, reservation.Value!.ETag, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var sessionEntity = session.Value!;
            sessionEntity["RegisteredCount"] = Math.Max(0, (ReadInt(sessionEntity, "RegisteredCount") ?? 1) - 1);
            sessionEntity["UpdatedAt"] = DateTimeOffset.UtcNow;
            sessionEntity["CorrelationId"] = correlationId;
            try
            {
                await table.SubmitTransactionAsync(
                        [
                            new TableTransactionAction(TableTransactionActionType.UpdateMerge, sessionEntity, sessionEntity.ETag),
                            new TableTransactionAction(TableTransactionActionType.Delete, reservation.Value!, reservation.Value!.ETag)
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException exception) when (exception.Status is 404 or 409 or 412)
            {
                // A concurrent reservation change won; reload before deciding whether compensation is still needed.
            }
        }

        throw new InvalidOperationException("Class registration reservation could not be released.");
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
            ["UpdatedAt"] = DateTimeOffset.UtcNow,
            ["CorrelationId"] = update.CorrelationId
        };
        if (!string.IsNullOrWhiteSpace(update.SmsStatus))
        {
            entity["ConfirmationSmsStatus"] = SafeValue(update.SmsStatus);
        }

        if (!string.IsNullOrWhiteSpace(update.EmailStatus))
        {
            entity["ConfirmationEmailStatus"] = SafeValue(update.EmailStatus);
        }

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
                ["StartsAt"] = request.Session.StartsAt,
                ["Status"] = ClassReminderStatuses.Pending,
                ["CreatedAt"] = DateTimeOffset.UtcNow,
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };

            try
            {
                await table.AddEntityAsync(entity, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (exception.Status == 409)
            {
                if (request.ReplaceExisting)
                {
                    await TryReactivateSessionChangedReminderAsync(table, entity, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public async Task CancelPendingRemindersBySessionAsync(
        string tenantId,
        string sessionId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Azure Table connection string is missing.");
        }

        var table = await GetTableClientAsync(reminderDueTableName, cancellationToken).ConfigureAwait(false);
        var filter = $"PartitionKey eq '{EscapeODataString(tenantId)}' and SessionId eq '{EscapeODataString(sessionId)}'";
        await foreach (var entity in table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
        {
            var status = ReadString(entity, "Status");
            if (!string.Equals(status, ClassReminderStatuses.Pending, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, ClassReminderStatuses.Claimed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entity["Status"] = ClassReminderStatuses.Skipped;
            entity["SkipReason"] = "session_changed";
            entity["UpdatedAt"] = DateTimeOffset.UtcNow;
            entity["CorrelationId"] = correlationId;
            try
            {
                await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (exception.Status == 412)
            {
                // A concurrent dispatcher owns the row; it will evaluate the current session before sending.
            }
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
            var filter = $"PartitionKey eq '{EscapeODataString(tenantId)}' and (Status eq '{ClassReminderStatuses.Pending}' or Status eq '{ClassReminderStatuses.Claimed}')";
            await foreach (var entity in table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
            {
                if (ReadDateTimeOffset(entity, "DueAt") is not { } entityDueAt || entityDueAt > dueAt)
                {
                    continue;
                }

                var status = ReadString(entity, "Status");
                var claimedAt = ReadDateTimeOffset(entity, "ClaimedAt");
                if (string.Equals(status, ClassReminderStatuses.Claimed, StringComparison.OrdinalIgnoreCase)
                    && claimedAt.HasValue
                    && claimedAt > DateTimeOffset.UtcNow.Subtract(ReminderClaimLease))
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
            var status = ReadString(existing.Value, "Status");
            var claimedAt = ReadDateTimeOffset(existing.Value, "ClaimedAt");
            var isStaleClaim = string.Equals(status, ClassReminderStatuses.Claimed, StringComparison.OrdinalIgnoreCase)
                && (!claimedAt.HasValue
                    || claimedAt.Value <= DateTimeOffset.UtcNow.Subtract(ReminderClaimLease));
            if (!string.Equals(status, ClassReminderStatuses.Pending, StringComparison.OrdinalIgnoreCase)
                && !isStaleClaim)
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
            ReadMetadata(entity))
        {
            Status = ReadString(entity, "Status") ?? ClassSessionStatuses.Published
        };

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
            ClaimedAt = ReadDateTimeOffset(entity, "ClaimedAt"),
            SkipReason = ReadString(entity, "SkipReason"),
            Attributes = ReadMetadata(entity)
        };

    private static async Task TryReactivateSessionChangedReminderAsync(
        TableClient table,
        TableEntity replacement,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await table
                .GetEntityIfExistsAsync<TableEntity>(replacement.PartitionKey, replacement.RowKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!existing.HasValue)
            {
                try
                {
                    await table.AddEntityAsync(replacement, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (RequestFailedException exception) when (exception.Status == 409)
                {
                    continue;
                }
            }

            var current = existing.Value!;
            if (!string.Equals(ReadString(current, "Status"), ClassReminderStatuses.Skipped, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ReadString(current, "SkipReason"), "session_changed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var etag = current.ETag;
            current.Clear();
            foreach (var property in replacement)
            {
                current[property.Key] = property.Value;
            }

            try
            {
                await table.UpdateEntityAsync(current, etag, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException exception) when (exception.Status is 404 or 412)
            {
                // Reload and only reactivate if the reminder is still a session-change skip.
            }
        }
    }

    private static string CreateReminderRowKey(
        DateTimeOffset dueAt,
        string registrationId,
        int offsetMinutes) =>
        $"{dueAt.UtcDateTime.Ticks:D20}|{offsetMinutes:D5}|{registrationId}";

    private static string CreateReminderTargetRowKeyComponent(string targetId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetId))).ToLowerInvariant();

    private static string CreateReservationRowKey(string sessionId, string registrationId) =>
        $"reservation|{CreateReminderTargetRowKeyComponent($"{sessionId}|{registrationId}")}";

    private static async Task<int> CountRegistrationsAsync(
        TableClient table,
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var filter = $"PartitionKey eq '{EscapeODataString(tenantId)}' and SessionId eq '{EscapeODataString(sessionId)}'";
        await foreach (var _ in table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
        {
            count++;
        }

        return count;
    }

    private static ClassSessionUpsertResult FailedSession(ClassFailureReason reason, string message) =>
        new(false, null, reason, message);

    private static void AddIfPresent(TableEntity entity, string propertyName, DateTimeOffset? value)
    {
        if (value.HasValue)
        {
            entity[propertyName] = value.Value;
        }
    }

    private static void SetOrRemove(TableEntity entity, string propertyName, DateTimeOffset? value)
    {
        if (value.HasValue)
        {
            entity[propertyName] = value.Value;
        }
        else
        {
            entity.Remove(propertyName);
        }
    }

    private static void SetOrRemove(TableEntity entity, string propertyName, int? value)
    {
        if (value.HasValue)
        {
            entity[propertyName] = value.Value;
        }
        else
        {
            entity.Remove(propertyName);
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
