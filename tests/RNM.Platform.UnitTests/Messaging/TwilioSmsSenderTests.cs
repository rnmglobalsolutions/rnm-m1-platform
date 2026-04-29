using System.Net;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using RNM.Platform.Infrastructure.Messaging;
using RNM.Platform.Infrastructure.Secrets;
using Xunit;

namespace RNM.Platform.UnitTests.Messaging;

public sealed class TwilioSmsSenderTests
{
    [Fact]
    public async Task SendSmsAsync_UsesTenantSenderAndSecrets()
    {
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"sid":"SM123"}""")
            });
        var sender = CreateSender(handler);

        var result = await sender.SendSmsAsync(
            new SmsMessageRequest(
                "tenant-a",
                "corr-123",
                "+15551234567",
                "Configured body"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("SM123", result.ProviderMessageId);
        Assert.Contains("/Accounts/account-sid-secret/Messages.json", handler.RequestPath);
        Assert.Equal("Basic", handler.AuthorizationScheme);
        Assert.Contains("From=%2B15550001000", handler.RequestBody);
        Assert.Contains("To=%2B15551234567", handler.RequestBody);
        Assert.Contains("Body=Configured+body", handler.RequestBody);
    }

    [Fact]
    public async Task SendSmsAsync_ReturnsFailure_WhenProviderRejectsMessage()
    {
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"message":"rejected"}""")
            });
        var sender = CreateSender(handler);

        var result = await sender.SendSmsAsync(
            new SmsMessageRequest(
                "tenant-a",
                "corr-123",
                "+15551234567",
                "Configured body"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.ProviderMessageId);
    }

    private static TwilioSmsSender CreateSender(HttpMessageHandler handler)
    {
        return new TwilioSmsSender(
            new StubTenantConfigurationProvider(),
            new StubSecretProvider(),
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.twilio.com/2010-04-01/")
            });
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;

        public RecordingHttpMessageHandler(HttpResponseMessage response)
        {
            this.response = response;
        }

        public string RequestPath { get; private set; } = string.Empty;

        public string? AuthorizationScheme { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri?.AbsolutePath ?? string.Empty;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return response;
        }
    }

    private sealed class StubTenantConfigurationProvider : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TenantConfiguration(
                new TenantId(tenantId),
                new VerticalId("hvac"),
                "Tenant A",
                "America/Chicago",
                new ServiceAreaConfiguration(["75001"], [], null),
                new ProviderConfiguration("GoHighLevel", "GoHighLevelCalendar", "Twilio", "AzureCommunicationServices"),
                new SecretNameConfiguration(
                    "crm-api-key",
                    "booking-api-key",
                    "vapi-webhook-secret",
                    "twilio-account-sid-secret-name",
                    "twilio-auth-token-secret-name",
                    "email-connection-string"),
                new CommunicationConfiguration(
                    "+15550001000",
                    "booking@example.com",
                    new ConfirmationTemplateConfiguration(
                        "SMS {{bookingDate}}",
                        "Email {{bookingDate}}",
                        "Email {{bookingStart}}"))));
        }
    }

    private sealed class StubSecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(
            string secretName,
            CancellationToken cancellationToken)
        {
            var value = secretName switch
            {
                "twilio-account-sid-secret-name" => "account-sid-secret",
                "twilio-auth-token-secret-name" => "auth-token-secret",
                _ => throw new SecretRetrievalException("Unexpected secret name.")
            };

            return Task.FromResult(value);
        }
    }
}
