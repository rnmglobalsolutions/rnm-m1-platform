using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.LeadImport;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class LeadCsvImportFunctionTests
{
    [Fact]
    public async Task HandleAsync_RejectsMissingApiKey()
    {
        var function = CreateFunction();
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/crm/campaigns/campaign-a/leads/import-csv",
            "firstName,lastName,phone\nJane,Seller,3052445176");

        var response = (TestHttpResponseData)await function.HandleAsync(
            request,
            "tenant-a",
            "campaign-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_ImportsCsv_WhenAuthorized()
    {
        var function = CreateFunction();
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/crm/campaigns/campaign-a/leads/import-csv",
            "firstName,lastName,phone,consentStatus\nJane,Seller,3052445176,opt_in");
        request.Headers.Add("x-rnm-api-key", "secret");

        var response = (TestHttpResponseData)await function.HandleAsync(
            request,
            "tenant-a",
            "campaign-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = response.ReadBody();
        Assert.Contains("\"tenantId\":\"tenant-a\"", body);
        Assert.Contains("\"campaignId\":\"campaign-a\"", body);
        Assert.Contains("\"created\":1", body);
    }

    private static LeadCsvImportFunction CreateFunction()
    {
        var service = new LeadCsvImportService(
            new StubTenantConfigurationProvider(),
            new InMemoryCrmAdapter(),
            new StubEventLogger());

        return new LeadCsvImportFunction(
            service,
            new ApiKeyRequestValidator(),
            new RnmRuntimeConfiguration(
                "Development",
                "../../../config",
                "secret",
                KeyVaultUri: null,
                AllowEnvironmentSecretFallback: true,
                RequireInternalApiKey: true),
            new SafeErrorResponseFactory(),
            new SafeHttpResponseWriter(),
            new CorrelationContextFactory(),
            new LimitedRequestBodyReader());
    }

    private sealed class StubTenantConfigurationProvider : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TenantConfiguration(
                new TenantId(tenantId),
                new VerticalId("real-estate"),
                "Tenant",
                "America/Chicago",
                new ServiceAreaConfiguration(["*"], [], null),
                new ProviderConfiguration("AzureTable", "GoogleCalendar", "Twilio", "SendGrid"),
                new SecretNameConfiguration("crm", "booking", "voice", "sid", "token", "email"),
                new CommunicationConfiguration("+15550001000", null, new ConfirmationTemplateConfiguration("sms"))));
        }
    }

    private sealed class StubEventLogger : IEventLogger
    {
        public Task LogEventAsync(string eventName, IReadOnlyDictionary<string, string> properties, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class InMemoryCrmAdapter : ICrmAdapter
    {
        private readonly Dictionary<string, CrmContactRecord> contacts = new(StringComparer.Ordinal);

        public Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(CrmContactLookupRequest request, CancellationToken cancellationToken)
        {
            var contact = contacts.Values.FirstOrDefault(item => item.TenantId == request.TenantId && item.PhoneNumber == request.PhoneNumber);
            return contact is null
                ? Task.FromResult(new CrmContactLookupResult(false, null))
                : Task.FromResult(new CrmContactLookupResult(true, contact.ProviderContactId) { Contact = contact });
        }

        public Task<CrmContactUpsertResult> UpsertContactAsync(CrmContactUpsertRequest request, CancellationToken cancellationToken)
        {
            var id = request.ProviderContactId ?? $"contact-{contacts.Count + 1}";
            var created = !contacts.ContainsKey(id);
            contacts[id] = new CrmContactRecord(
                request.TenantId,
                id,
                request.PhoneNumber,
                request.Email,
                request.Name,
                request.ZipCode,
                request.Attributes);
            return Task.FromResult(new CrmContactUpsertResult(true, created, id));
        }

        public Task<CrmOperationResult> AddTimelineEventAsync(CrmTimelineEventRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CrmOperationResult(true));

        public Task<CrmOperationResult> AddInteractionNoteAsync(CrmInteractionNoteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> ApplyTagsAsync(CrmTagRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> LinkBookingToContactAsync(CrmBookingLinkRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkFollowUpRequiredAsync(CrmFollowUpRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmLeadQueryResult> GetLeadsByStatusAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmLeadQueryResult> GetLeadsByCampaignAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmNextLeadToCallResult> GetNextLeadToCallAsync(CrmNextLeadToCallRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> RecordOutboundAttemptAsync(CrmOutboundAttemptRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkLeadReactivatedAsync(CrmLeadReactivationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkOptOutAsync(CrmOptOutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
