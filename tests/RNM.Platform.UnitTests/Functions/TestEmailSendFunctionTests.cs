using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Ports.Messaging;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class TestEmailSendFunctionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task Handle_RejectsMissingOrInvalidApiKey(string? providedApiKey)
    {
        using var secretName = TemporaryEnvironmentVariable.Set(ApiSecretNames.InternalApiKeySecretNameSetting, "internal-api-key");
        using var environmentName = TemporaryEnvironmentVariable.Set("RNM_ENVIRONMENT", "dev");
        var emailSender = new FakeEmailSender();
        var function = CreateFunction(emailSender);
        var request = CreateRequest();
        if (providedApiKey is not null)
        {
            request.Headers.Add("x-rnm-api-key", providedApiKey);
        }

        var response = (TestHttpResponseData)await function.Handle(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, emailSender.SendCallCount);
    }

    [Fact]
    public async Task Handle_SendsEmail_WhenAuthorized()
    {
        using var secretName = TemporaryEnvironmentVariable.Set(ApiSecretNames.InternalApiKeySecretNameSetting, "internal-api-key");
        using var environmentName = TemporaryEnvironmentVariable.Set("RNM_ENVIRONMENT", "dev");
        var emailSender = new FakeEmailSender
        {
            Result = new EmailSendResult(true, "provider-message-id")
        };
        var function = CreateFunction(emailSender);
        var request = CreateRequest();
        request.Headers.Add("x-rnm-api-key", "expected-api-key");

        var response = (TestHttpResponseData)await function.Handle(request, CancellationToken.None);
        var body = response.ReadBody();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"sent\":true", body);
        Assert.Contains("\"providerMessageId\":\"provider-message-id\"", body);
        Assert.Equal(1, emailSender.SendCallCount);
        Assert.Equal("test@example.com", emailSender.LastRequest?.ToEmail);
        Assert.Equal("RNM test email", emailSender.LastRequest?.Subject);
    }

    [Fact]
    public async Task Handle_ReturnsSafeFailure_WhenEmailSenderFails()
    {
        using var secretName = TemporaryEnvironmentVariable.Set(ApiSecretNames.InternalApiKeySecretNameSetting, "internal-api-key");
        using var environmentName = TemporaryEnvironmentVariable.Set("RNM_ENVIRONMENT", "dev");
        var emailSender = new FakeEmailSender
        {
            Result = new EmailSendResult(
                false,
                Message: "provider rejected test@example.com with body This is a test email from RNM Platform.")
        };
        var function = CreateFunction(emailSender);
        var request = CreateRequest();
        request.Headers.Add("x-rnm-api-key", "expected-api-key");

        var response = (TestHttpResponseData)await function.Handle(request, CancellationToken.None);
        var body = response.ReadBody();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("\"sent\":false", body);
        Assert.Contains("\"failureReason\":\"email_send_failed\"", body);
        Assert.DoesNotContain("test@example.com", body);
        Assert.DoesNotContain("This is a test email", body);
    }

    private static TestEmailSendFunction CreateFunction(FakeEmailSender emailSender) =>
        new(
            emailSender,
            new ApiKeyRequestValidator(),
            new StubSecretProvider("expected-api-key"),
            new SafeErrorResponseFactory(),
            new SafeHttpResponseWriter(),
            new CorrelationContextFactory(),
            new LimitedRequestBodyReader());

    private static TestHttpRequestData CreateRequest() =>
        new(
            "POST",
            "https://platform.example.com/api/test/email/send",
            """
            {
              "toEmail": "test@example.com",
              "subject": "RNM test email",
              "body": "This is a test email from RNM Platform."
            }
            """);

    private sealed class FakeEmailSender : IEmailSender
    {
        public EmailSendResult Result { get; init; } = new(true, "email-provider-id");

        public int SendCallCount { get; private set; }

        public EmailMessageRequest? LastRequest { get; private set; }

        public Task<EmailSendResult> SendEmailAsync(
            EmailMessageRequest request,
            CancellationToken cancellationToken)
        {
            SendCallCount++;
            LastRequest = request;
            return Task.FromResult(Result);
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
