using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;
using RNM.Platform.Application.Crm;
using RNM.Platform.Infrastructure.Providers;

namespace RNM.Platform.Infrastructure.Crm;

public sealed class AzureTableCrmAdapter : ICrmProviderAdapter
{
    private const string DefaultContactsTableName = "RnmContacts";
    private const string DefaultNotesTableName = "RnmContactNotes";
    private const string DefaultBookingLinksTableName = "RnmBookingLinks";

    private readonly string? connectionString;
    private readonly string contactsTableName;
    private readonly string notesTableName;
    private readonly string bookingLinksTableName;

    public AzureTableCrmAdapter()
    {
        connectionString = GetConnectionString();
        contactsTableName = GetSetting("RNM_CRM_CONTACTS_TABLE_NAME", DefaultContactsTableName);
        notesTableName = GetSetting("RNM_CRM_CONTACT_NOTES_TABLE_NAME", DefaultNotesTableName);
        bookingLinksTableName = GetSetting("RNM_CRM_BOOKING_LINKS_TABLE_NAME", DefaultBookingLinksTableName);
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
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };

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
            var linksTable = await GetTableClientAsync(bookingLinksTableName, cancellationToken).ConfigureAwait(false);
            var link = new TableEntity(request.TenantId, CreateTimestampRowKey())
            {
                ["ProviderContactId"] = request.ProviderContactId,
                ["ProviderBookingId"] = request.ProviderBookingId,
                ["CreatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };
            await linksTable.AddEntityAsync(link, cancellationToken).ConfigureAwait(false);

            var contactsTable = await GetTableClientAsync(contactsTableName, cancellationToken).ConfigureAwait(false);
            var contact = new TableEntity(request.TenantId, request.ProviderContactId)
            {
                ["LastProviderBookingId"] = request.ProviderBookingId,
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };
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

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }

    private static string SafeValue(string? value) => value?.Trim() ?? string.Empty;

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
