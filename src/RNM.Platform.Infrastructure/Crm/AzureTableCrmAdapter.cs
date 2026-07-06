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

    public async Task<CrmLeadQueryResult> GetLeadsByStatusAsync(
        CrmLeadQueryRequest request,
        CancellationToken cancellationToken)
    {
        return await QueryContactsByAttributeAsync(
                request,
                CrmContactAttributeNames.LeadStatus,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CrmLeadQueryResult> GetLeadsByCampaignAsync(
        CrmLeadQueryRequest request,
        CancellationToken cancellationToken)
    {
        return await QueryContactsByAttributeAsync(
                request,
                CrmContactAttributeNames.CampaignId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CrmNextLeadToCallResult> GetNextLeadToCallAsync(
        CrmNextLeadToCallRequest request,
        CancellationToken cancellationToken)
    {
        var queryResult = await GetLeadsByCampaignAsync(
                new CrmLeadQueryRequest(request.TenantId, request.CorrelationId, request.CampaignId),
                cancellationToken)
            .ConfigureAwait(false);
        if (!queryResult.Succeeded)
        {
            return new CrmNextLeadToCallResult(
                false,
                null,
                queryResult.FailureReason,
                queryResult.Message);
        }

        var lead = SelectNextLeadToCall(
            queryResult.Leads,
            request.MaxOutboundAttempts,
            request.Now,
            request.ExcludedProviderContactIds);

        return new CrmNextLeadToCallResult(true, lead);
    }

    public async Task<CrmOperationResult> RecordOutboundAttemptAsync(
        CrmOutboundAttemptRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return FailedOperation("Azure Table CRM connection string is missing.");
        }

        try
        {
            var table = await GetTableClientAsync(contactsTableName, cancellationToken).ConfigureAwait(false);
            var existing = await TryGetContactEntityAsync(
                    table,
                    request.TenantId,
                    request.ProviderContactId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                return FailedOperation("Azure Table CRM contact was not found.", CrmFailureReason.ContactNotFound);
            }

            if (!CanRecordOutboundInteraction(GetAttribute(existing, CrmContactAttributeNames.ConsentStatus)))
            {
                return FailedOperation("Contact has opted out and cannot receive outbound interaction.", CrmFailureReason.ConsentOptedOut);
            }

            var attemptCount = ReadIntAttribute(existing, CrmContactAttributeNames.OutboundAttemptCount) + 1;
            var contact = new TableEntity(request.TenantId, request.ProviderContactId)
            {
                [AttributePropertyName(CrmContactAttributeNames.OutboundAttemptCount)] = attemptCount.ToString(),
                [AttributePropertyName(CrmContactAttributeNames.LastContactedAt)] = request.AttemptedAt.ToUniversalTime().ToString("O"),
                [AttributePropertyName(CrmContactAttributeNames.LeadStatus)] = CrmOutboundLeadStatuses.Contacted,
                ["LastInteractionAt"] = request.AttemptedAt,
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };

            await table.UpsertEntityAsync(contact, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);

            var summary = string.IsNullOrWhiteSpace(request.Note)
                ? $"Outbound attempt recorded: {request.Outcome}"
                : request.Note;
            await TryAddTimelineEventAsync(
                    request.TenantId,
                    request.CorrelationId,
                    request.ProviderContactId,
                    CrmTimelineEventTypes.OutboundAttemptRecorded,
                    summary,
                    new Dictionary<string, string>
                    {
                        ["outcome"] = request.Outcome,
                        ["outboundAttemptCount"] = attemptCount.ToString()
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return new CrmOperationResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedOperation("Azure Table CRM outbound attempt update failed.", CrmFailureReason.AdapterFailure);
        }
    }

    public async Task<CrmOperationResult> MarkLeadReactivatedAsync(
        CrmLeadReactivationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return FailedOperation("Azure Table CRM connection string is missing.");
        }

        try
        {
            var table = await GetTableClientAsync(contactsTableName, cancellationToken).ConfigureAwait(false);
            var existing = await TryGetContactEntityAsync(
                    table,
                    request.TenantId,
                    request.ProviderContactId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                return FailedOperation("Azure Table CRM contact was not found.", CrmFailureReason.ContactNotFound);
            }

            var contact = new TableEntity(request.TenantId, request.ProviderContactId)
            {
                [AttributePropertyName(CrmContactAttributeNames.LeadStatus)] = CrmOutboundLeadStatuses.Reactivated,
                ["Tags"] = MergeTags(ReadString(existing, "Tags"), "reactivated"),
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };

            await table.UpsertEntityAsync(contact, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);
            await TryAddTimelineEventAsync(
                    request.TenantId,
                    request.CorrelationId,
                    request.ProviderContactId,
                    CrmTimelineEventTypes.LeadReactivated,
                    "Lead reactivated.",
                    new Dictionary<string, string>
                    {
                        ["leadStatus"] = CrmOutboundLeadStatuses.Reactivated
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return new CrmOperationResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedOperation("Azure Table CRM lead reactivation update failed.", CrmFailureReason.AdapterFailure);
        }
    }

    public async Task<CrmOperationResult> MarkOptOutAsync(
        CrmOptOutRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return FailedOperation("Azure Table CRM connection string is missing.");
        }

        try
        {
            var table = await GetTableClientAsync(contactsTableName, cancellationToken).ConfigureAwait(false);
            var providerContactId = await ResolveOptOutContactIdAsync(
                    table,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(providerContactId))
            {
                return FailedOperation(
                    "Azure Table CRM opt-out requires contact id, phone number, or email.",
                    CrmFailureReason.MissingContactIdentifier);
            }

            var contact = new TableEntity(request.TenantId, providerContactId)
            {
                [AttributePropertyName(CrmContactAttributeNames.ConsentStatus)] = CrmConsentStatuses.OptedOut,
                ["UpdatedAt"] = DateTimeOffset.UtcNow,
                ["CorrelationId"] = request.CorrelationId
            };
            AddIfPresent(contact, "Phone", Normalize(request.PhoneNumber));
            AddIfPresent(contact, "Email", Normalize(request.Email));

            await table.UpsertEntityAsync(contact, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);
            await TryAddTimelineEventAsync(
                    request.TenantId,
                    request.CorrelationId,
                    providerContactId,
                    CrmTimelineEventTypes.ConsentOptedOut,
                    "Contact opted out.",
                    new Dictionary<string, string>
                    {
                        ["source"] = request.Source,
                        ["reason"] = request.Reason ?? string.Empty,
                        ["consentStatus"] = CrmConsentStatuses.OptedOut
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return new CrmOperationResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedOperation("Azure Table CRM opt-out update failed.", CrmFailureReason.AdapterFailure);
        }
    }

    private async Task<string?> ResolveOptOutContactIdAsync(
        TableClient contactsTable,
        CrmOptOutRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.ProviderContactId))
        {
            return request.ProviderContactId;
        }

        var lookupResult = await FindContactByPhoneOrEmailAsync(
                new CrmContactLookupRequest(
                    request.TenantId,
                    request.CorrelationId,
                    request.PhoneNumber,
                    request.Email),
                cancellationToken)
            .ConfigureAwait(false);
        if (lookupResult.Found && !string.IsNullOrWhiteSpace(lookupResult.ProviderContactId))
        {
            return lookupResult.ProviderContactId;
        }

        var identifier = Normalize(request.Email)
            ?? Normalize(request.PhoneNumber);
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var rowKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier))).ToLowerInvariant();
        var entity = new TableEntity(request.TenantId, rowKey)
        {
            ["Phone"] = SafeValue(Normalize(request.PhoneNumber)),
            ["Email"] = SafeValue(Normalize(request.Email)),
            ["CreatedAt"] = DateTimeOffset.UtcNow,
            ["UpdatedAt"] = DateTimeOffset.UtcNow,
            ["CorrelationId"] = request.CorrelationId
        };
        await contactsTable.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken).ConfigureAwait(false);
        return rowKey;
    }

    private async Task<CrmLeadQueryResult> QueryContactsByAttributeAsync(
        CrmLeadQueryRequest request,
        string attributeName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new CrmLeadQueryResult(
                false,
                [],
                CrmFailureReason.AdapterFailure,
                "Azure Table CRM connection string is missing.");
        }

        try
        {
            var table = await GetTableClientAsync(contactsTableName, cancellationToken).ConfigureAwait(false);
            var propertyName = AttributePropertyName(attributeName);
            var filter =
                $"PartitionKey eq '{EscapeODataString(request.TenantId)}' and {propertyName} eq '{EscapeODataString(request.Value)}'";
            var contacts = new List<CrmContactRecord>();

            await foreach (var entity in table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
            {
                contacts.Add(MaterializeContact(entity));
            }

            return new CrmLeadQueryResult(true, contacts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new CrmLeadQueryResult(
                false,
                [],
                CrmFailureReason.AdapterFailure,
                "Azure Table CRM contact query failed.");
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

    private static async Task<TableEntity?> TryGetContactEntityAsync(
        TableClient table,
        string tenantId,
        string providerContactId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await table.GetEntityAsync<TableEntity>(
                    tenantId,
                    providerContactId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return response.Value;
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            return null;
        }
    }

    private async Task TryAddTimelineEventAsync(
        string tenantId,
        string correlationId,
        string providerContactId,
        string eventType,
        string summary,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            await AddTimelineEventAsync(
                    new CrmTimelineEventRequest(
                        tenantId,
                        correlationId,
                        providerContactId,
                        ProviderBookingId: null,
                        eventType,
                        ProviderName,
                        summary,
                        metadata),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The contact update is the critical write. Timeline failures are captured by storage telemetry.
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

    private static CrmContactRecord MaterializeContact(TableEntity entity)
    {
        var attributes = entity
            .Where(property => property.Key.StartsWith("Attr_", StringComparison.Ordinal))
            .Where(property => property.Value is not null)
            .ToDictionary(
                property => property.Key["Attr_".Length..],
                property => property.Value.ToString() ?? string.Empty,
                StringComparer.Ordinal);

        return new CrmContactRecord(
            entity.PartitionKey,
            entity.RowKey,
            ReadString(entity, "Phone"),
            ReadString(entity, "Email"),
            ReadString(entity, "Name"),
            ReadString(entity, "ZipCode"),
            attributes);
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

    private static void AddIfPresent(TableEntity entity, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            entity[propertyName] = value;
        }
    }

    private static string AttributePropertyName(string attributeName) =>
        $"Attr_{SanitizePropertyName(attributeName)}";

    private static string? GetAttribute(TableEntity entity, string attributeName) =>
        ReadString(entity, AttributePropertyName(attributeName));

    private static int ReadIntAttribute(TableEntity entity, string attributeName) =>
        int.TryParse(GetAttribute(entity, attributeName), out var value) ? value : 0;

    private static string? ReadString(TableEntity entity, string propertyName) =>
        entity.TryGetValue(propertyName, out var value) ? value?.ToString() : null;

    private static string MergeTags(string? existingTags, string newTag)
    {
        var tags = (existingTags ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        tags.Add(newTag);
        return string.Join(",", tags.Order(StringComparer.OrdinalIgnoreCase));
    }

    internal static bool CanRecordOutboundInteraction(string? consentStatus) =>
        !string.Equals(consentStatus, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase);

    internal static CrmContactRecord? SelectNextLeadToCall(
        IEnumerable<CrmContactRecord> leads,
        int maxOutboundAttempts,
        DateTimeOffset now,
        IReadOnlyCollection<string>? excludedProviderContactIds = null)
    {
        var excluded = excludedProviderContactIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : excludedProviderContactIds.ToHashSet(StringComparer.Ordinal);

        return leads
            .Where(lead => CanRecordOutboundInteraction(lead.ConsentStatus))
            .Where(lead => !excluded.Contains(lead.ProviderContactId))
            .Where(lead => lead.OutboundAttemptCount < maxOutboundAttempts)
            .Where(lead => lead.NextFollowUpAt is null || lead.NextFollowUpAt <= now)
            .OrderBy(lead => lead.NextFollowUpAt ?? DateTimeOffset.MinValue)
            .ThenBy(lead => lead.OutboundAttemptCount)
            .ThenBy(lead => lead.ProviderContactId, StringComparer.Ordinal)
            .FirstOrDefault();
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
