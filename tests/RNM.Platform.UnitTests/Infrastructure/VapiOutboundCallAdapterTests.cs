using System.Net;
using System.Text;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Outbound;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using RNM.Platform.Infrastructure.Outbound;
using RNM.Platform.Infrastructure.Secrets;
using Xunit;

namespace RNM.Platform.UnitTests.Infrastructure;

public sealed class VapiOutboundCallAdapterTests
{
    [Fact]
    public async Task StartCallAsync_PostsCreateCallRequestWithoutLeakingSecretInBody()
    {
        var handler = new QueueHttpMessageHandler([
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"id\":\"call-123\",\"status\":\"queued\"}", Encoding.UTF8, "application/json")
            }
        ]);
        var adapter = CreateAdapter(handler);

        var result = await adapter.StartCallAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("call-123", result.ProviderCallId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.vapi.ai/call", request.RequestUri?.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("vapi-api-key-value", request.Headers.Authorization?.Parameter);
        var body = handler.RequestBodies.Single();
        Assert.Contains("\"assistantId\":\"assistant-1\"", body);
        Assert.Contains("\"phoneNumberId\":\"phone-1\"", body);
        Assert.Contains("15551234567", body);
        Assert.Contains("\"url\":\"https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/outbound", body);
        Assert.DoesNotContain("vapi-api-key-value", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartCallAsync_RetriesRetryableProviderFailures()
    {
        var handler = new QueueHttpMessageHandler([
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            new HttpResponseMessage(HttpStatusCode.BadGateway),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"id\":\"call-123\"}", Encoding.UTF8, "application/json")
            }
        ]);
        var adapter = CreateAdapter(handler);

        var result = await adapter.StartCallAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, handler.Requests.Count);
    }

    private static VapiOutboundCallAdapter CreateAdapter(HttpMessageHandler handler)
    {
        return new VapiOutboundCallAdapter(
            new StubTenantConfigurationProvider(),
            new StubSecretProvider(),
            new HttpClient(handler),
            (_, _) => Task.CompletedTask);
    }

    private static OutboundCallStartRequest CreateRequest()
    {
        return new OutboundCallStartRequest(
            "tenant-a",
            "corr-1",
            new CrmContactRecord(
                "tenant-a",
                "contact-1",
                "+15551234567",
                "lead@example.com",
                "Lead One",
                "77002",
                new Dictionary<string, string>
                {
                    [CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptIn
                }),
            "assistant-1",
            "phone-1",
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/outbound?contactId=contact-1");
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
                new CommunicationConfiguration("+15550001000", null, new ConfirmationTemplateConfiguration("sms")),
                new ReportingConfiguration(),
                new VoiceConfiguration(
                    new OutboundVoiceConfiguration(
                        "vapi-key-secret",
                        "https://api.vapi.ai/",
                        "assistant-1",
                        "phone-1",
                        "https://platform.example.com/"))));
        }
    }

    private sealed class StubSecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(string secretName, CancellationToken cancellationToken) =>
            Task.FromResult("vapi-api-key-value");
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses;

        public QueueHttpMessageHandler(IEnumerable<HttpResponseMessage> responses)
        {
            this.responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return responses.Dequeue();
        }
    }
}
