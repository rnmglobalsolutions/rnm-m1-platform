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
            var contactId = TryReadContactId(responseJson);
            return string.IsNullOrWhiteSpace(contactId)
                ? new CrmContactLookupResult(false, null)
                : new CrmContactLookupResult(true, contactId);
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

    public Task<CrmOperationResult> AddInteractionNoteAsync(
        CrmInteractionNoteRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new CrmOperationResult(true, Message: "GoHighLevel note sync is not required for M1."));
    }

    public Task<CrmOperationResult> ApplyTagsAsync(
        CrmTagRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new CrmOperationResult(true, Message: "GoHighLevel tag sync is not required for M1."));
    }

    public Task<CrmOperationResult> LinkBookingToContactAsync(
        CrmBookingLinkRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new CrmOperationResult(true, Message: "GoHighLevel booking link sync is not required for M1."));
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

internal sealed record GoHighLevelAppointmentLinkRequestDto(
    string ContactId,
    string AppointmentId);
