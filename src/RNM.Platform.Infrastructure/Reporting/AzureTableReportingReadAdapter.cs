using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Ports.Reporting;
using RNM.Platform.Application.Reporting;

namespace RNM.Platform.Infrastructure.Reporting;

public sealed class AzureTableReportingReadAdapter : IReportingReadAdapter
{
    private const string DefaultContactsTableName = "RnmContacts";
    private const string DefaultBookingsTableName = "RnmBookings";
    private const string DefaultTimelineTableName = "RnmTimelineEvents";

    private readonly string? connectionString;
    private readonly string contactsTableName;
    private readonly string bookingsTableName;
    private readonly string timelineTableName;

    public AzureTableReportingReadAdapter()
    {
        connectionString = GetConnectionString();
        contactsTableName = GetSetting("RNM_CRM_CONTACTS_TABLE_NAME", DefaultContactsTableName);
        bookingsTableName = GetSetting(
            "RNM_CRM_BOOKINGS_TABLE_NAME",
            GetSetting("RNM_CRM_BOOKING_LINKS_TABLE_NAME", DefaultBookingsTableName));
        timelineTableName = GetSetting("RNM_CRM_TIMELINE_TABLE_NAME", DefaultTimelineTableName);
    }

    public async Task<ReportingDataSet> GetPilotReportingDataAsync(
        PilotReportRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new ReportingDataSet([], [], []);
        }

        var contacts = await QueryContactsAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        var bookings = await QueryBookingsAsync(request.TenantId, request.From, request.To, cancellationToken).ConfigureAwait(false);
        var timelineEvents = await QueryTimelineEventsAsync(request.TenantId, request.From, request.To, cancellationToken).ConfigureAwait(false);
        return new ReportingDataSet(contacts, bookings, timelineEvents);
    }

    private async Task<IReadOnlyCollection<ReportingContactRecord>> QueryContactsAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            var table = GetTableClient(contactsTableName);
            var contacts = new List<ReportingContactRecord>();
            await foreach (var entity in table.QueryAsync<TableEntity>(
                               $"PartitionKey eq '{EscapeODataString(tenantId)}'",
                               cancellationToken: cancellationToken))
            {
                contacts.Add(new ReportingContactRecord(
                    entity.PartitionKey,
                    entity.RowKey,
                    ReadDateTimeOffset(entity, "CreatedAt"),
                    ReadAttributeDateTimeOffset(entity, CrmContactAttributeNames.LastContactedAt),
                    ReadAttributeInt(entity, CrmContactAttributeNames.OutboundAttemptCount),
                    ReadAttributeString(entity, CrmContactAttributeNames.LeadStatus),
                    ReadString(entity, "Tags")));
            }

            return contacts;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return [];
        }
    }

    private async Task<IReadOnlyCollection<ReportingBookingRecord>> QueryBookingsAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        try
        {
            var table = GetTableClient(bookingsTableName);
            var bookings = new List<ReportingBookingRecord>();
            await foreach (var entity in table.QueryAsync<TableEntity>(
                               $"PartitionKey eq '{EscapeODataString(tenantId)}'",
                               cancellationToken: cancellationToken))
            {
                var createdAt = ReadDateTimeOffset(entity, "CreatedAt");
                if (!IsInRange(createdAt, from, to))
                {
                    continue;
                }

                bookings.Add(new ReportingBookingRecord(
                    entity.PartitionKey,
                    ReadString(entity, "ProviderContactId") ?? string.Empty,
                    createdAt,
                    ReadDateTimeOffset(entity, "StartsAt"),
                    ReadString(entity, "BookingState")));
            }

            return bookings;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return [];
        }
    }

    private async Task<IReadOnlyCollection<ReportingTimelineEventRecord>> QueryTimelineEventsAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        try
        {
            var table = GetTableClient(timelineTableName);
            var events = new List<ReportingTimelineEventRecord>();
            await foreach (var entity in table.QueryAsync<TableEntity>(
                               $"PartitionKey eq '{EscapeODataString(tenantId)}'",
                               cancellationToken: cancellationToken))
            {
                var createdAt = ReadDateTimeOffset(entity, "CreatedAt");
                if (!IsInRange(createdAt, from, to))
                {
                    continue;
                }

                events.Add(new ReportingTimelineEventRecord(
                    entity.PartitionKey,
                    ReadString(entity, "ProviderContactId") ?? string.Empty,
                    createdAt,
                    ReadString(entity, "EventType") ?? string.Empty,
                    ReadMetadata(entity)));
            }

            return events;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return [];
        }
    }

    private TableClient GetTableClient(string tableName) => new(connectionString!, tableName);

    private static bool IsInRange(DateTimeOffset? value, DateTimeOffset from, DateTimeOffset to) =>
        value.HasValue && value.Value >= from && value.Value <= to;

    private static DateTimeOffset? ReadAttributeDateTimeOffset(TableEntity entity, string attributeName) =>
        DateTimeOffset.TryParse(ReadAttributeString(entity, attributeName), out var value) ? value : null;

    private static int ReadAttributeInt(TableEntity entity, string attributeName) =>
        int.TryParse(ReadAttributeString(entity, attributeName), out var value) ? value : 0;

    private static string? ReadAttributeString(TableEntity entity, string attributeName) =>
        ReadString(entity, $"Attr_{SanitizePropertyName(attributeName)}");

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

    private static string? ReadString(TableEntity entity, string propertyName) =>
        entity.TryGetValue(propertyName, out var value) ? value?.ToString() : null;

    private static IReadOnlyDictionary<string, string> ReadMetadata(TableEntity entity)
    {
        var json = ReadString(entity, "MetadataJson");
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
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

    private static string EscapeODataString(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string SanitizePropertyName(string value)
    {
        var characters = value
            .Where(char.IsLetterOrDigit)
            .Take(48)
            .ToArray();
        return characters.Length == 0 ? "Value" : new string(characters);
    }
}
