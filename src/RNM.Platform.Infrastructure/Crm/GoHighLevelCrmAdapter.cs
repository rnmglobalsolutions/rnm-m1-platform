using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Infrastructure.Configuration;
using RNM.Platform.Infrastructure.GoHighLevel;
using RNM.Platform.Infrastructure.Providers;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Infrastructure.Crm;

public sealed class GoHighLevelCrmAdapter : ICrmProviderAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ISecretProvider secretProvider;
    private readonly HttpClient httpClient;

    public GoHighLevelCrmAdapter(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISecretProvider secretProvider,
        HttpClient httpClient)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.secretProvider = secretProvider;
        this.httpClient = httpClient;
    }

    public string ProviderName => ProviderNames.GoHighLevel;

    public async Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(
        CrmContactLookupRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await GetCredentialsAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
            if (credentials is null || string.IsNullOrWhiteSpace(credentials.LocationId))
            {
                return new CrmContactLookupResult(false, null);
            }

            var payload = new GoHighLevelContactSearchRequestDto(
                credentials.LocationId,
                request.PhoneNumber,
                request.Email);
            using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            using var message = CreateRequest(HttpMethod.Post, "contacts/search", credentials);
            message.Content = content;

            using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new CrmContactLookupResult(false, null);
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var contact = TryReadContact(responseJson, request.TenantId);
            var contactId = contact?.ProviderContactId ?? TryReadContactId(responseJson);
            return string.IsNullOrWhiteSpace(contactId)
                ? new CrmContactLookupResult(false, null)
                : new CrmContactLookupResult(true, contactId)
                {
                    Contact = contact
                };
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
        try
        {
            var credentials = await GetCredentialsAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
            if (credentials is null || string.IsNullOrWhiteSpace(credentials.LocationId))
            {
                return FailedUpsert("GoHighLevel CRM credentials are incomplete.", request.ProviderContactId);
            }

            var payload = new GoHighLevelContactUpsertRequestDto(
                credentials.LocationId,
                request.ProviderContactId,
                request.PhoneNumber,
                request.Email,
                request.Name,
                request.ZipCode);
            using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            using var message = CreateRequest(HttpMethod.Post, "contacts/upsert", credentials);
            message.Content = content;

            using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return FailedUpsert("GoHighLevel contact upsert failed.", request.ProviderContactId);
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var contactId = TryReadContactId(responseJson);
            return string.IsNullOrWhiteSpace(contactId)
                ? FailedUpsert("GoHighLevel contact response did not include a contact id.", request.ProviderContactId)
                : new CrmContactUpsertResult(
                    true,
                    string.IsNullOrWhiteSpace(request.ProviderContactId),
                    contactId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedUpsert("GoHighLevel contact upsert failed.", request.ProviderContactId);
        }
    }

    public async Task<CrmOperationResult> AddInteractionNoteAsync(
        CrmInteractionNoteRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await GetCredentialsAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
            if (credentials is null)
            {
                return FailedOperation(CrmFailureReason.NoteFailed, "GoHighLevel CRM credentials are incomplete.");
            }

            var payload = new GoHighLevelCreateNoteRequestDto(request.Note);
            return await PostContactOperationAsync(
                    $"contacts/{Uri.EscapeDataString(request.ProviderContactId)}/notes",
                    payload,
                    credentials,
                    CrmFailureReason.NoteFailed,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedOperation(CrmFailureReason.NoteFailed, "GoHighLevel note sync failed.");
        }
    }

    public async Task<CrmOperationResult> ApplyTagsAsync(
        CrmTagRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await GetCredentialsAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
            if (credentials is null)
            {
                return FailedOperation(CrmFailureReason.TagsFailed, "GoHighLevel CRM credentials are incomplete.");
            }

            var payload = new GoHighLevelAddTagsRequestDto(request.Tags);
            return await PostContactOperationAsync(
                    $"contacts/{Uri.EscapeDataString(request.ProviderContactId)}/tags",
                    payload,
                    credentials,
                    CrmFailureReason.TagsFailed,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedOperation(CrmFailureReason.TagsFailed, "GoHighLevel tag sync failed.");
        }
    }

    public Task<CrmOperationResult> LinkBookingToContactAsync(
        CrmBookingLinkRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new CrmOperationResult(
            true,
            Message: "The GoHighLevel appointment is linked during creation through contactId."));
    }

    public Task<CrmOperationResult> AddTimelineEventAsync(
        CrmTimelineEventRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new CrmOperationResult(
            false,
            CrmFailureReason.AdapterFailure,
            "RNM Native CRM timeline events are not persisted by the GoHighLevel CRM adapter."));
    }

    public Task<CrmOperationResult> MarkFollowUpRequiredAsync(
        CrmFollowUpRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new CrmOperationResult(
            false,
            CrmFailureReason.AdapterFailure,
            "RNM Native CRM follow-up state is not persisted by the GoHighLevel CRM adapter."));
    }

    public Task<CrmLeadQueryResult> GetLeadsByStatusAsync(
        CrmLeadQueryRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(UnsupportedLeadQuery());
    }

    public Task<CrmLeadQueryResult> GetLeadsByCampaignAsync(
        CrmLeadQueryRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(UnsupportedLeadQuery());
    }

    public Task<CrmNextLeadToCallResult> GetNextLeadToCallAsync(
        CrmNextLeadToCallRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new CrmNextLeadToCallResult(
            false,
            null,
            CrmFailureReason.AdapterFailure,
            "Outbound lead lists are only supported by RNM Native CRM."));
    }

    public Task<CrmOperationResult> RecordOutboundAttemptAsync(
        CrmOutboundAttemptRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(FailedOperation(
            CrmFailureReason.AdapterFailure,
            "Outbound attempts are only supported by RNM Native CRM."));
    }

    public Task<CrmOperationResult> MarkLeadReactivatedAsync(
        CrmLeadReactivationRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(FailedOperation(
            CrmFailureReason.AdapterFailure,
            "Lead reactivation state is only supported by RNM Native CRM."));
    }

    public Task<CrmOperationResult> MarkOptOutAsync(
        CrmOptOutRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(FailedOperation(
            CrmFailureReason.AdapterFailure,
            "Opt-out state is only supported by RNM Native CRM."));
    }

    private async Task<CrmOperationResult> PostContactOperationAsync<TPayload>(
        string path,
        TPayload payload,
        GoHighLevelCredentials credentials,
        CrmFailureReason failureReason,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");
        using var message = CreateRequest(HttpMethod.Post, path, credentials);
        message.Content = content;

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? new CrmOperationResult(true)
            : FailedOperation(failureReason, "GoHighLevel contact operation failed.");
    }

    private async Task<GoHighLevelCredentials?> GetCredentialsAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        var tenantConfiguration = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);
        var secretValue = await secretProvider
            .GetSecretAsync(tenantConfiguration.GetCrmCredentialsSecretName(), cancellationToken)
            .ConfigureAwait(false);

        return GoHighLevelCredentials.TryParse(secretValue, out var credentials)
            ? credentials
            : null;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        GoHighLevelCredentials credentials)
    {
        var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        message.Headers.TryAddWithoutValidation("Version", credentials.ApiVersion);
        return message;
    }

    private static string? TryReadContactId(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            return ReadString(root, "id")
                ?? ReadString(root, "contactId")
                ?? ReadNestedString(root, "contact", "id")
                ?? ReadFirstContactId(root);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CrmContactRecord? TryReadContact(string responseJson, string tenantId)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            var contact = TryGetContactElement(root);
            if (contact is null)
            {
                return null;
            }

            var contactId = ReadString(contact.Value, "id") ?? ReadString(contact.Value, "contactId");
            if (string.IsNullOrWhiteSpace(contactId))
            {
                return null;
            }

            var attributes = ReadNormalizedAttributes(contact.Value);
            var zipCode = ReadString(contact.Value, "postalCode")
                ?? ReadString(contact.Value, "zipCode")
                ?? ReadString(contact.Value, "zip");
            AddAttribute(attributes, CrmContactAttributeNames.ConsentStatus, ReadString(contact.Value, "consentStatus"));
            AddAttribute(attributes, CrmContactAttributeNames.LeadSource, ReadString(contact.Value, "leadSource") ?? ReadString(contact.Value, "source"));
            AddAttribute(attributes, CrmContactAttributeNames.CampaignId, ReadString(contact.Value, "campaignId"));
            AddAttribute(attributes, CrmContactAttributeNames.LeadStatus, ReadString(contact.Value, "leadStatus"));
            AddAttribute(attributes, CrmContactAttributeNames.Intent, ReadString(contact.Value, "intent"));
            AddAttribute(attributes, CrmContactAttributeNames.TargetPropertyAddress, ReadString(contact.Value, "targetPropertyAddress"));
            AddAttribute(attributes, CrmContactAttributeNames.AssignedAgent, ReadString(contact.Value, "assignedAgent") ?? ReadString(contact.Value, "assignedTo"));
            AddAttribute(attributes, "estimatedValue", ReadString(contact.Value, "estimatedValue"));
            AddAttribute(attributes, "serviceNeed", ReadString(contact.Value, "serviceNeed"));
            AddAttribute(attributes, "serviceAddress", ReadString(contact.Value, "serviceAddress"));
            AddAttribute(attributes, "propertyType", ReadString(contact.Value, "propertyType"));
            AddAttribute(attributes, "urgency", ReadString(contact.Value, "urgency"));
            AddAttribute(attributes, "zipCode", zipCode);

            return new CrmContactRecord(
                tenantId,
                contactId,
                ReadString(contact.Value, "phone") ?? ReadString(contact.Value, "phoneNumber"),
                ReadString(contact.Value, "email"),
                ReadContactName(contact.Value),
                zipCode,
                attributes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? TryGetContactElement(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("contact", out var contact)
            && contact.ValueKind is JsonValueKind.Object)
        {
            return contact;
        }

        if (root.TryGetProperty("contacts", out var contacts)
            && contacts.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in contacts.EnumerateArray())
            {
                if (item.ValueKind is JsonValueKind.Object)
                {
                    return item;
                }
            }
        }

        return root;
    }

    private static Dictionary<string, string> ReadNormalizedAttributes(JsonElement contact)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!contact.TryGetProperty("customFields", out var customFields))
        {
            return attributes;
        }

        if (customFields.ValueKind is JsonValueKind.Object)
        {
            foreach (var field in customFields.EnumerateObject())
            {
                AddAttributeAliases(attributes, field.Name, ReadJsonValue(field.Value));
            }
        }

        if (customFields.ValueKind is JsonValueKind.Array)
        {
            foreach (var field in customFields.EnumerateArray())
            {
                if (field.ValueKind is not JsonValueKind.Object)
                {
                    continue;
                }

                var value = ReadString(field, "value") ?? ReadString(field, "fieldValue");
                var key = ReadString(field, "name")
                    ?? ReadString(field, "key")
                    ?? ReadString(field, "fieldKey")
                    ?? ReadString(field, "id")
                    ?? ReadString(field, "fieldId");
                AddAttributeAliases(attributes, key, value);
            }
        }

        return attributes;
    }

    private static string? ReadContactName(JsonElement contact)
    {
        var name = ReadString(contact, "name") ?? ReadString(contact, "fullName");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var firstName = ReadString(contact, "firstName");
        var lastName = ReadString(contact, "lastName");
        return string.Join(" ", new[] { firstName, lastName }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static void AddAttributeAliases(
        IDictionary<string, string> attributes,
        string? key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        AddAttribute(attributes, key, value);

        var trimmedKey = key.Trim();
        var lastSegment = trimmedKey.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        AddAttribute(attributes, lastSegment, value);
    }

    private static void AddAttribute(
        IDictionary<string, string> attributes,
        string? key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
        {
            attributes[key.Trim()] = value.Trim();
        }
    }

    private static string? ReadJsonValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

    private static string? ReadFirstContactId(JsonElement root)
    {
        if (!root.TryGetProperty("contacts", out var contacts)
            || contacts.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        foreach (var contact in contacts.EnumerateArray())
        {
            var contactId = ReadString(contact, "id") ?? ReadString(contact, "contactId");
            if (!string.IsNullOrWhiteSpace(contactId))
            {
                return contactId;
            }
        }

        return null;
    }

    private static string? ReadNestedString(JsonElement root, string parentPropertyName, string propertyName)
    {
        return root.ValueKind is JsonValueKind.Object
            && root.TryGetProperty(parentPropertyName, out var parent)
            ? ReadString(parent, propertyName)
            : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind is JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static CrmContactUpsertResult FailedUpsert(string message, string? providerContactId) =>
        new(
            false,
            Created: false,
            providerContactId,
            CrmFailureReason.ContactUpsertFailed,
            message);

    private static CrmOperationResult FailedOperation(
        CrmFailureReason reason,
        string message) =>
        new(false, reason, message);

    private static CrmLeadQueryResult UnsupportedLeadQuery() =>
        new(
            false,
            [],
            CrmFailureReason.AdapterFailure,
            "Outbound lead lists are only supported by RNM Native CRM.");
}

internal sealed record GoHighLevelContactSearchRequestDto(
    string LocationId,
    string? Phone,
    string? Email);

internal sealed record GoHighLevelContactUpsertRequestDto(
    string LocationId,
    string? ContactId,
    string? Phone,
    string? Email,
    string? Name,
    string? PostalCode);

internal sealed record GoHighLevelCreateNoteRequestDto(string Body);

internal sealed record GoHighLevelAddTagsRequestDto(IReadOnlyCollection<string> Tags);
