using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Observability;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using RNM.Platform.Infrastructure.Secrets;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class ManyChatLeadWebhookFunctionTests
{
    [Fact]
    public async Task RunAsync_RejectsInvalidTenantSecretBeforeProcessingPayload()
    {
        var function = CreateFunction(enabled: true);
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/webhooks/manychat/leads",
            "{}");
        request.Headers.Add("X-RNM-ManyChat-Secret", "wrong-secret");

        var response = (TestHttpResponseData)await function.RunAsync(
            request,
            "tenant-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RunAsync_RejectsTenantWhenIntegrationIsDisabled()
    {
        var function = CreateFunction(enabled: false);
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/webhooks/manychat/leads",
            "{}");
        request.Headers.Add("X-RNM-ManyChat-Secret", "expected-secret");

        var response = (TestHttpResponseData)await function.RunAsync(
            request,
            "tenant-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static ManyChatLeadWebhookFunction CreateFunction(bool enabled) =>
        new(
            intakeService: null!,
            new TenantProvider(enabled),
            new SecretProvider(),
            new ApiKeyRequestValidator(),
            new LimitedRequestBodyReader(),
            new CorrelationContextFactory(),
            new SafeErrorResponseFactory(),
            new SafeHttpResponseWriter(),
            new NullEventLogger());

    private sealed class TenantProvider(bool enabled) : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantConfiguration(
                new TenantId(tenantId),
                new VerticalId("life-insurance"),
                "Tenant",
                "America/Chicago",
                new ServiceAreaConfiguration([], ["United States"], null),
                new ProviderConfiguration("AzureTable", "GoogleCalendar", "Twilio", "SendGrid"),
                new SecretNameConfiguration(
                    "crm",
                    "booking",
                    "voice",
                    "twilio-sid",
                    "twilio-token",
                    "email",
                    ManyChatWebhookSecret: "manychat-secret"),
                new CommunicationConfiguration("+13055550100", null, new ConfirmationTemplateConfiguration("sms")),
                Integrations: new IntegrationConfiguration(new ManyChatIntegrationConfiguration(Enabled: enabled))));
    }

    private sealed class SecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(string secretName, CancellationToken cancellationToken) =>
            Task.FromResult("expected-secret");
    }

    private sealed class NullEventLogger : IEventLogger
    {
        public Task LogEventAsync(string eventName, IReadOnlyDictionary<string, string> properties, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
