using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Outbound;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.Outbound;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class OutboundCampaignRunFunctionTests
{
    [Fact]
    public async Task HandleAsync_RejectsMissingApiKey()
    {
        var function = CreateFunction();
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/outbound/campaigns/campaign-a/run");

        var response = (TestHttpResponseData)await function.HandleAsync(
            request,
            "tenant-a",
            "campaign-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_UsesRouteTenantAndCampaign_WhenAuthorized()
    {
        var function = CreateFunction();
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/outbound/campaigns/campaign-a/run");
        request.Headers.Add("x-rnm-api-key", "secret");

        var response = (TestHttpResponseData)await function.HandleAsync(
            request,
            "tenant-a",
            "campaign-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = response.ReadBody();
        Assert.Contains("\"tenantId\":\"tenant-a\"", body);
        Assert.Contains("\"campaignId\":\"campaign-a\"", body);
        Assert.Contains("\"failureReason\":\"missing_configuration\"", body);
    }

    private static OutboundCampaignRunFunction CreateFunction()
    {
        var service = new OutboundCampaignRunService(
            new StubTenantConfigurationProvider(),
            new StubCrmAdapter(),
            new StubOutboundCallAdapter(),
            new StubEventLogger(),
            () => new DateTimeOffset(2026, 7, 5, 15, 0, 0, TimeSpan.Zero),
            (_, _) => Task.CompletedTask);

        return new OutboundCampaignRunFunction(
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
            new CorrelationContextFactory());
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

    private sealed class StubCrmAdapter : ICrmAdapter
    {
        public Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(CrmContactLookupRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmContactUpsertResult> UpsertContactAsync(CrmContactUpsertRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> AddInteractionNoteAsync(CrmInteractionNoteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> ApplyTagsAsync(CrmTagRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> LinkBookingToContactAsync(CrmBookingLinkRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> AddTimelineEventAsync(CrmTimelineEventRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkFollowUpRequiredAsync(CrmFollowUpRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmLeadQueryResult> GetLeadsByStatusAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmLeadQueryResult> GetLeadsByCampaignAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmNextLeadToCallResult> GetNextLeadToCallAsync(CrmNextLeadToCallRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> RecordOutboundAttemptAsync(CrmOutboundAttemptRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkLeadReactivatedAsync(CrmLeadReactivationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkOptOutAsync(CrmOptOutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubOutboundCallAdapter : IOutboundCallAdapter
    {
        public Task<OutboundCallStartResult> StartCallAsync(
            OutboundCallStartRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubEventLogger : IEventLogger
    {
        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
