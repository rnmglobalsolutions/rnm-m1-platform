using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using RNM.Platform.Infrastructure.Messaging;
using Xunit;

namespace RNM.Platform.UnitTests.Messaging;

public sealed class SendGridEmailSenderTests
{
    [Fact]
    public async Task SendEmailAsync_ReturnsSafeFailure_WhenApiKeyIsMissing()
    {
        using var _ = TemporaryEnvironmentVariable.Set("SENDGRID_API_KEY", null);
        var transport = new FakeSendGridEmailTransport();
        var sender = new SendGridEmailSender(new FakeTenantConfigurationProvider(), transport);

        var result = await sender.SendEmailAsync(CreateRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("missing_api_key", result.Message);
        Assert.Equal(0, transport.SendCallCount);
    }

    [Fact]
    public async Task SendEmailAsync_ReturnsSuccess_WhenSendGridAcceptsMessage()
    {
        using var _ = TemporaryEnvironmentVariable.Set("SENDGRID_API_KEY", "sendgrid-key");
        var transport = new FakeSendGridEmailTransport
        {
            Response = new SendGridEmailSendResponse(true, "sg-message-123")
        };
        var sender = new SendGridEmailSender(new FakeTenantConfigurationProvider(), transport);

        var result = await sender.SendEmailAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("sg-message-123", result.ProviderMessageId);
        Assert.Equal("booking@example.com", transport.LastMessage?.FromEmail);
        Assert.Equal("Example Business", transport.LastMessage?.FromName);
    }

    [Fact]
    public async Task SendEmailAsync_ReturnsSafeFailure_WhenSendGridRejectsMessage()
    {
        using var _ = TemporaryEnvironmentVariable.Set("SENDGRID_API_KEY", "sendgrid-key");
        var sender = new SendGridEmailSender(
            new FakeTenantConfigurationProvider(),
            new FakeSendGridEmailTransport
            {
                Response = new SendGridEmailSendResponse(false)
            });

        var result = await sender.SendEmailAsync(CreateRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("provider_failure", result.Message);
        Assert.DoesNotContain("lead@example.com", result.Message);
        Assert.DoesNotContain("Email body", result.Message);
    }

    [Fact]
    public async Task SendEmailAsync_DoesNotSwallowCancellation()
    {
        using var _ = TemporaryEnvironmentVariable.Set("SENDGRID_API_KEY", "sendgrid-key");
        var sender = new SendGridEmailSender(
            new FakeTenantConfigurationProvider(),
            new FakeSendGridEmailTransport
            {
                ExceptionToThrow = new OperationCanceledException()
            });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sender.SendEmailAsync(CreateRequest(), CancellationToken.None));
    }

    private static EmailMessageRequest CreateRequest() =>
        new(
            "tenant-a",
            "corr-123",
            "lead@example.com",
            "Email subject",
            "Email body");

    private sealed class FakeSendGridEmailTransport : ISendGridEmailTransport
    {
        public SendGridEmailSendResponse Response { get; init; } = new(true, "sg-message-id");

        public Exception? ExceptionToThrow { get; init; }

        public int SendCallCount { get; private set; }

        public SendGridEmailMessage? LastMessage { get; private set; }

        public Task<SendGridEmailSendResponse> SendAsync(
            string apiKey,
            SendGridEmailMessage message,
            CancellationToken cancellationToken)
        {
            SendCallCount++;
            LastMessage = message;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(Response);
        }
    }

    private sealed class FakeTenantConfigurationProvider : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TenantConfiguration(
                new TenantId(tenantId),
                new VerticalId("hvac"),
                "Example Business",
                "America/Chicago",
                new ServiceAreaConfiguration(["12345"], ["Austin"], null),
                new ProviderConfiguration("Crm", "Booking", "Sms", "SendGrid"),
                new SecretNameConfiguration(
                    "crm-secret",
                    "booking-secret",
                    "vapi-secret",
                    "twilio-sid",
                    "twilio-token",
                    "unused-email-secret"),
                new CommunicationConfiguration(
                    "+15550001000",
                    "booking@example.com",
                    new ConfirmationTemplateConfiguration(
                        "Sms body",
                        "Email subject",
                        "Email body"))));
        }
    }

    private sealed class TemporaryEnvironmentVariable : IDisposable
    {
        private readonly string name;
        private readonly string? previousValue;

        private TemporaryEnvironmentVariable(string name, string? value)
        {
            this.name = name;
            previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public static TemporaryEnvironmentVariable Set(string name, string? value) =>
            new(name, value);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(name, previousValue);
        }
    }
}
