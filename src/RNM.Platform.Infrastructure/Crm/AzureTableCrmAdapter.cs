using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using RNM.Platform.Application.Crm;
using RNM.Platform.Infrastructure.Providers;

namespace RNM.Platform.Infrastructure.Crm;

public sealed class AzureTableCrmAdapter : ICrmProviderAdapter
{
    private const string DefaultContactsTableName = "RnmContacts";
    private const string DefaultNotesTableName = "RnmContactNotes";
    private const string DefaultBookingsTableName = "RnmBookings";
    private const string DefaultTimelineTableName = "RnmTimelineEvents";
    private const int MaxTableStringLength = 32000;

    private readonly string? connectionString;
    private readonly string contactsTableName;
    private readonly string notesTableName;
    private readonly string bookingsTableName;
    private readonly string timelineTableName;

    public AzureTableCrmAdapter()
    {
        connectionString = GetConnectionString();
        contactsTableName = GetSetting("RNM_CRM_CONTACTS_TABLE_NAME", DefaultContactsTableName);
        notesTableName = GetSetting("RNM_CRM_CONTACT_NOTES_TABLE_NAME", DefaultNotesTableName);
        bookingsTableName = GetSetting(
            "RNM_CRM_BOOKINGS_TABLE_NAME",
            GetSetting("RNM_CRM_BOOKING_LINKS_TABLE_NAME", DefaultBookingsTableName));
        timelineTableName = GetSetting("RNM_CRM_TIMELINE_TABLE_NAME", DefaultTimelineTableName);
    }

    public string ProviderName => ProviderNames.AzureTable;

    public async Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(
        CrmContactLookupRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new CrmContactLookupResult(false, null);
        }

        try
        {
            var table = await GetTableClientAsync(contactsTableName, cancellationToken).ConfigureAwait(false);
            var phone = Normalize(request.PhoneNumber);
            var email = Normalize(request.Email);
            if (string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(email))
            {
                return new CrmContactLookupResult(false, null);
            }

            var filters = new List<string>();
            if (!string.IsNullOrWhiteSpace(phone))
            {
                filters.Add($"Phone eq '{EscapeODataString(phone)}'");
            }

            if (!string.IsNullOrWhiteSpace(email))
            {
                filters.Add($"Email eq '{EscapeODataString(email)}'");
            }

            var filter = $"PartitionKey eq '{EscapeODataString(request.TenantId)}' and ({string.Join(" or ", filters)})";
            await foreach (var entity in table.QueryAsync<TableEntity>(filter, maxPerPage: 1, cancellationToken: cancellationToken))
            {
                return new CrmContactLookupResult(true, entity.RowKey);
            }

            return new CrmContactLookupResult(false, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new CrmContactLookupResult(false, null);
        }
    }

    public async Task<CrmContactUpsertResult> UpsertContactAsync(
        CrmContactUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return FailedUpsert("Azure Table CRM connection string is missing.", request.ProviderContactId);
        }

        try
        {
            var table = await GetTableClientAsync(contactsTableName, cancellationToken).ConfigureAwait(false);
            var rowKey = string.IsNullOrWhiteSpace(request.ProviderContactId)
                ? CreateContactRowKey(request)
                : request.ProviderContactId;

            var created = !await EntityExistsAsync(table, request.TenantId, rowKey, cancellationToken).ConfigureAwait(false);
            var entity = new TableEntity(request.TenantId, rowKey)
            {
                ["VerticalId"] = request.VerticalId,
                ["Phone"] = SafeValue(Normalize(request.PhoneNumber)),
                ["Email"] = SafeValue(Normalize(request.Email)),
                ["Name"] = SafeValue(request.Name),
                ["ZipCode"] = SafeValue(request.ZipCode),
                ["LeadStatus"] = SafeValue(request.LeadStatus),
                ["NeedsFollowUp"] = request.NeedsFollowUp,
                ["FollowUpReason"] = SafeValue(request.FollowUpReason),
                ["LastInteractionAt"] = request.LastInteractionAt,
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };
            AddIfPresent(entity, "FollowUpAt", request.FollowUpAt);
            if (created)
            {
                entity["CreatedAt"] = DateTimeOffset.UtcNow;
            }

            foreach (var attribute in request.Attributes)
            {
                if (!string.IsNullOrWhiteSpace(attribute.Key) && attribute.Value is not null)
                {
                    entity[$"Attr_{SanitizePropertyName(attribute.Key)}"] = attribute.Value;
                }
            }

            await table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);
            return new CrmContactUpsertResult(true, created, rowKey);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedUpsert("Azure Table CRM contact upsert failed.", request.ProviderContactId);
        }
    }

    public async Task<CrmOperationResult> AddInteractionNoteAsync(
        CrmInteractionNoteRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return FailedOperation("Azure Table CRM connection string is missing.");
        }

        try
        {
            var table = await GetTableClientAsync(notesTableName, cancellationToken).ConfigureAwait(false);
            var entity = new TableEntity(request.TenantId, CreateTimestampRowKey())
            {
                ["ProviderContactId"] = request.ProviderContactId,
                ["Note"] = request.Note,
                ["CreatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };

            await table.AddEntityAsync(entity, cancellationToken).ConfigureAwait(false);
            return new CrmOperationResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedOperation("Azure Table CRM note write failed.", CrmFailureReason.NoteFailed);
        }
    }

    public async Task<CrmOperationResult> ApplyTagsAsync(
        CrmTagRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return FailedOperation("Azure Table CRM connection string is missing.");
        }

        try
        {
            var table = await GetTableClientAsync(contactsTableName, cancellationToken).ConfigureAwait(false);
            var entity = new TableEntity(request.TenantId, request.ProviderContactId)
            {
                ["Tags"] = string.Join(",", request.Tags),
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };

            await table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);
            return new CrmOperationResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedOperation("Azure Table CRM tag write failed.", CrmFailureReason.TagsFailed);
        }
    }

    public async Task<CrmOperationResult> LinkBookingToContactAsync(
        CrmBookingLinkRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return FailedOperation("Azure Table CRM connection string is missing.");
        }

        try
        {
            var bookingsTable = await GetTableClientAsync(bookingsTableName, cancellationToken).ConfigureAwait(false);
            var bookingRowKey = CreateBookingRowKey(request);
            var bookingExists = await EntityExistsAsync(
                    bookingsTable,
                    request.TenantId,
                    bookingRowKey,
                    cancellationToken)
                .ConfigureAwait(false);
            var booking = new TableEntity(request.TenantId, bookingRowKey)
            {
                ["ProviderContactId"] = request.ProviderContactId,
                ["ProviderBookingId"] = request.ProviderBookingId,
                ["VerticalId"] = SafeValue(request.VerticalId),
                ["BookingProvider"] = SafeValue(request.BookingProvider),
                ["Source"] = SafeValue(request.Source),
                ["CustomerName"] = SafeValue(request.CustomerName),
                ["Phone"] = SafeValue(request.PhoneNumber),
                ["Email"] = SafeValue(request.Email),
                ["ServiceType"] = SafeValue(request.ServiceType),
                ["PropertyType"] = SafeValue(request.PropertyType),
                ["ServiceAddress"] = SafeValue(request.ServiceAddress),
                ["ZipCode"] = SafeValue(request.ZipCode),
                ["Urgency"] = SafeValue(request.Urgency),
                ["PreferredWindow"] = SafeValue(request.PreferredWindow),
                ["BookingLabel"] = SafeValue(request.BookingLabel),
                ["TimeZone"] = SafeValue(request.TimeZone),
                ["BookingState"] = SafeValue(request.BookingState),
                ["QualificationState"] = SafeValue(request.QualificationState),
                ["ServiceAreaState"] = SafeValue(request.ServiceAreaState),
                ["CorrelationId"] = request.CorrelationId,
                ["UpdatedAt"] = DateTimeOffset.UtcNow
            };
            AddIfPresent(booking, "StartsAt", request.StartsAt);
            AddIfPresent(booking, "EndsAt", request.EndsAt);
            if (!bookingExists)
            {
                booking["CreatedAt"] = DateTimeOffset.UtcNow;
            }

            await bookingsTable.UpsertEntityAsync(booking, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);

            var contactsTable = await GetTableClientAsync(contactsTableName, cancellationToken).ConfigureAwait(false);
            var contact = new TableEntity(request.TenantId, request.ProviderContactId)
            {
                ["LastProviderBookingId"] = request.ProviderBookingId,
                ["LeadStatus"] = CrmLeadStatuses.AppointmentScheduled,
                ["NeedsFollowUp"] = false,
                ["FollowUpReason"] = string.Empty,
                ["LastBookingState"] = SafeValue(request.BookingState),
                ["LastServiceType"] = SafeValue(request.ServiceType),
                ["LastUrgency"] = SafeValue(request.Urgency),
                ["LastServiceAddress"] = SafeValue(request.ServiceAddress),
                ["LastZipCode"] = SafeValue(request.ZipCode),
                ["LastInteractionAt"] = DateTimeOffset.UtcNow,
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };
            AddIfPresent(contact, "LastBookingStartsAt", request.StartsAt);
            AddIfPresent(contact, "LastBookingEndsAt", request.EndsAt);
            await contactsTable.UpsertEntityAsync(contact, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);
            return new CrmOperationResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedOperation("Azure Table CRM booking link write failed.", CrmFailureReason.BookingLinkFailed);
        }
    }

    public async Task<CrmOperationResult> AddTimelineEventAsync(
        CrmTimelineEventRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return FailedOperation("Azure Table CRM connection string is missing.");
        }

        try
        {
            var table = await GetTableClientAsync(timelineTableName, cancellationToken).ConfigureAwait(false);
            var entity = new TableEntity(request.TenantId, CreateTimestampRowKey())
            {
                ["ProviderContactId"] = SafeValue(request.ProviderContactId),
                ["ProviderBookingId"] = SafeValue(request.ProviderBookingId),
                ["EventType"] = SafeValue(request.EventType),
                ["Source"] = SafeValue(request.Source),
                ["Summary"] = SafeTableString(request.Summary),
                ["MetadataJson"] = SafeTableString(JsonSerializer.Serialize(request.Metadata)),
                ["CreatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };

            await table.AddEntityAsync(entity, cancellationToken).ConfigureAwait(false);
            return new CrmOperationResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedOperation("Azure Table CRM timeline write failed.", CrmFailureReason.AdapterFailure);
        }
    }

    public async Task<CrmOperationResult> MarkFollowUpRequiredAsync(
        CrmFollowUpRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return FailedOperation("Azure Table CRM connection string is missing.");
        }

        try
        {
            var table = await GetTableClientAsync(contactsTableName, cancellationToken).ConfigureAwait(false);
            var contact = new TableEntity(request.TenantId, request.ProviderContactId)
            {
                ["LeadStatus"] = SafeValue(request.LeadStatus),
                ["NeedsFollowUp"] = true,
                ["FollowUpReason"] = SafeValue(request.Reason),
                ["LastInteractionAt"] = request.LastInteractionAt,
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };
            AddIfPresent(contact, "FollowUpAt", request.FollowUpAt);

            await table.UpsertEntityAsync(contact, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);
            return new CrmOperationResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedOperation("Azure Table CRM follow-up update failed.", CrmFailureReason.AdapterFailure);
        }
    }

    private async Task<TableClient> GetTableClientAsync(string tableName, CancellationToken cancellationToken)
    {
        var client = new TableClient(connectionString!, tableName);
        await client.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    private static async Task<bool> EntityExistsAsync(
        TableClient table,
        string partitionKey,
        string rowKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await table.GetEntityAsync<TableEntity>(partitionKey, rowKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            return false;
        }
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

    private static string CreateContactRowKey(CrmContactUpsertRequest request)
    {
        var identifier = Normalize(request.Email)
            ?? Normalize(request.PhoneNumber)
            ?? $"{request.CorrelationId}:{DateTimeOffset.UtcNow:O}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier))).ToLowerInvariant();
    }

    private static string CreateTimestampRowKey() =>
        $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}";

    private static string CreateBookingRowKey(CrmBookingLinkRequest request)
    {
        var identifier = $"{request.BookingProvider}:{request.ProviderBookingId}";
        return $"booking-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier))).ToLowerInvariant()}";
    }

    private static void AddIfPresent(TableEntity entity, string propertyName, DateTimeOffset? value)
    {
        if (value.HasValue)
        {
            entity[propertyName] = value.Value;
        }
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }

    private static string SafeValue(string? value) => value?.Trim() ?? string.Empty;

    private static string SafeTableString(string? value)
    {
        var safeValue = SafeValue(value);
        return safeValue.Length <= MaxTableStringLength
            ? safeValue
            : safeValue[..MaxTableStringLength];
    }

    private static string EscapeODataString(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string SanitizePropertyName(string value)
    {
        var characters = value
            .Where(char.IsLetterOrDigit)
            .Take(48)
            .ToArray();
        return characters.Length == 0 ? "Value" : new string(characters);
    }

    private static CrmContactUpsertResult FailedUpsert(string message, string? providerContactId) =>
        new(false, Created: false, providerContactId, CrmFailureReason.ContactUpsertFailed, message);

    private static CrmOperationResult FailedOperation(
        string message,
        CrmFailureReason reason = CrmFailureReason.AdapterFailure) =>
        new(false, reason, message);
}
