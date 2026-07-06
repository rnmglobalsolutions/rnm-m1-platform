using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Api.Voice;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Tenancy;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using RNM.Platform.Infrastructure.Secrets;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class VapiOutboundWebhookFunctionTests
{
    [Fact]
    public async Task HandleAsync_RejectsInvalidSignature()
    {
        var crmAdapter = new RecordingCrmAdapter();
        var function = CreateFunction(crmAdapter);
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/outbound?contactId=contact-1&correlationId=corr-1",
            "{\"message\":{\"type\":\"end-of-call-report\"}}");

        var response = (TestHttpResponseData)await function.HandleAsync(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(crmAdapter.OutboundAttempts);
    }

    [Fact]
    public async Task HandleAsync_RecordsOutboundAttempt_OnCompletionEvent()
    {
        var crmAdapter = new RecordingCrmAdapter();
        var function = CreateFunction(crmAdapter);
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/outbound?contactId=contact-1&correlationId=corr-1",
            "{\"message\":{\"type\":\"end-of-call-report\",\"call\":{\"endedReason\":\"customer-ended-call\"}}}");
        request.Headers.Add("Authorization", "Bearer webhook-secret");

        var response = (TestHttpResponseData)await function.HandleAsync(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var attempt = Assert.Single(crmAdapter.OutboundAttempts);
        Assert.Equal("tenant-a", attempt.TenantId);
        Assert.Equal("corr-1", attempt.CorrelationId);
        Assert.Equal("contact-1", attempt.ProviderContactId);
        Assert.Equal("contacted", attempt.Outcome);
    }

    private static VapiOutboundWebhookFunction CreateFunction(RecordingCrmAdapter crmAdapter)
    {
        return new VapiOutboundWebhookFunction(
            new TenantResolver(new StubTenantConfigurationProvider(), new StubVerticalConfigurationProvider()),
            new VapiWebhookValidator(),
            new StubSecretProvider(),
            new SafeErrorResponseFactory(),
            new SafeHttpResponseWriter(),
            new CorrelationContextFactory(),
            new StubEventLogger(),
            crmAdapter,
            new LimitedRequestBodyReader(),
            new VapiWebhookOptions());
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
                new SecretNameConfiguration("crm", "booking", "voice-webhook-secret", "sid", "token", "email"),
                new CommunicationConfiguration("+15550001000", null, new ConfirmationTemplateConfiguration("sms"))));
        }
    }

    private sealed class StubVerticalConfigurationProvider : IVerticalConfigurationProvider
    {
        public Task<VerticalConfiguration> GetVerticalConfigurationAsync(
            string verticalId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new VerticalConfiguration(
                new VerticalId(verticalId),
                "Real Estate",
                ["name"],
                ["outbound"],
                ServiceAreaFieldAliasConfiguration.Defaults()));
        }
    }

    private sealed class StubSecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(string secretName, CancellationToken cancellationToken) =>
            Task.FromResult("webhook-secret");
    }

    private sealed class StubEventLogger : IEventLogger
    {
        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingCrmAdapter : ICrmAdapter
    {
        public List<CrmOutboundAttemptRequest> OutboundAttempts { get; } = [];

        public Task<CrmOperationResult> RecordOutboundAttemptAsync(
            CrmOutboundAttemptRequest request,
            CancellationToken cancellationToken)
        {
            OutboundAttempts.Add(request);
            return Task.FromResult(new CrmOperationResult(true));
        }

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
        public Task<CrmOperationResult> MarkLeadReactivatedAsync(CrmLeadReactivationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkOptOutAsync(CrmOptOutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
