using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Classes;
using RNM.Platform.UnitTests.Classes;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class ClassRegistrationFunctionTests
{
    [Fact]
    public async Task RegisterAsync_AllowsConfiguredPublicOriginWithoutApiKey()
    {
        var session = CreateSession();
        var store = new FakeClassSessionStore(session);
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var function = CreateFunction(store, sms, email);
        var request = CreateRegistrationRequest("https://example.com");

        var response = (TestHttpResponseData)await function.RegisterAsync(
            request,
            "tenant-a",
            session.SessionId,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://example.com", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Single(sms.Requests);
        Assert.Single(email.Requests);
    }

    [Fact]
    public async Task RegisterAsync_RejectsUnconfiguredPublicOriginWithoutSendingNotifications()
    {
        var session = CreateSession();
        var store = new FakeClassSessionStore(session);
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var function = CreateFunction(store, sms, email);
        var request = CreateRegistrationRequest("https://evil.example");

        var response = (TestHttpResponseData)await function.RegisterAsync(
            request,
            "tenant-a",
            session.SessionId,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Access-Control-Allow-Origin", out _));
        Assert.Empty(sms.Requests);
        Assert.Empty(email.Requests);
    }

    private static ClassRegistrationFunction CreateFunction(
        FakeClassSessionStore store,
        FakeSmsSender sms,
        FakeEmailSender email)
    {
        var logger = new FakeEventLogger();
        return new ClassRegistrationFunction(
            new ClassRegistrationService(
                new FakeTenantConfigurationProvider(),
                store,
                new FakeCrmAdapter(),
                new ClassNotificationService(sms, email, new FakeCrmAdapter(), logger),
                logger),
            new FakeTenantConfigurationProvider(),
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

    private static TestHttpRequestData CreateRegistrationRequest(string origin)
    {
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/classes/session-a/registrations",
            """
            {
              "customerName": "Jane Lead",
              "customerPhoneNumber": "+15551234567",
              "customerEmail": "jane@example.com",
              "marketingConsentGranted": true,
              "attributes": {
                "intent": "masterclass"
              }
            }
            """);
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static ClassSessionRecord CreateSession() =>
        new(
            "tenant-a",
            "session-a",
            "Financial Master Class",
            new DateTimeOffset(2026, 6, 10, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero),
            "America/Chicago",
            "https://zoom.us/j/123",
            100,
            "campaign-a",
            new Dictionary<string, string>());
}
