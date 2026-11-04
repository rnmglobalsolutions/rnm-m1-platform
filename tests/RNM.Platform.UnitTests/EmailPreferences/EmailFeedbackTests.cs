using System.Net;
using System.Security.Cryptography;
using System.Text;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.EmailPreferences;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Infrastructure.Secrets;
using RNM.Platform.UnitTests.Classes;
using RNM.Platform.UnitTests.Functions;
using Xunit;

namespace RNM.Platform.UnitTests.EmailPreferences;

public sealed class EmailFeedbackTests
{
    private const string SigningKey = "unsubscribe-signing-key-for-tests";
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnsubscribeToken_IsBoundToTenantAndAddress()
    {
        var token = EmailUnsubscribeToken.Create(SigningKey, "tenant-a", "Lead@Example.com ");

        Assert.True(EmailUnsubscribeToken.IsValid(SigningKey, "tenant-a", "lead@example.com", token));
        Assert.False(EmailUnsubscribeToken.IsValid(SigningKey, "tenant-b", "lead@example.com", token));
        Assert.False(EmailUnsubscribeToken.IsValid(SigningKey, "tenant-a", "other@example.com", token));
        Assert.False(EmailUnsubscribeToken.IsValid("another-key", "tenant-a", "lead@example.com", token));
        Assert.Equal("lead@example.com", EmailUnsubscribeToken.DecodeEmail(EmailUnsubscribeToken.EncodeEmail("Lead@Example.com")));
        Assert.Null(EmailUnsubscribeToken.DecodeEmail("not base64 !"));
    }

    [Fact]
    public void WebhookSignature_AcceptsSendGridFormatAndRejectsTamperingAndReplay()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var body = Encoding.UTF8.GetBytes("[{\"event\":\"spamreport\"}]");
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        var signature = Sign(key, timestamp, body);
        var validator = new SendGridEventWebhookValidator();

        Assert.True(validator.IsValid(publicKey, signature, timestamp, body, Now));
        Assert.True(validator.IsValid($"-----BEGIN PUBLIC KEY-----\n{publicKey}\n-----END PUBLIC KEY-----", signature, timestamp, body, Now));
        Assert.False(validator.IsValid(publicKey, signature, timestamp, Encoding.UTF8.GetBytes("[{\"event\":\"delivered\"}]"), Now));
        Assert.False(validator.IsValid(publicKey, signature, timestamp, body, Now.AddMinutes(11)));
        Assert.False(validator.IsValid(publicKey, "not-a-signature", timestamp, body, Now));
        Assert.False(validator.IsValid(publicKey, null, timestamp, body, Now));
    }

    [Fact]
    public async Task EventWebhook_AppliesComplaintsUnsubscribesAndHardBouncesPerTenant()
    {
        var fixture = new WebhookFixture();
        var body = """
            [
              {"event":"spamreport","email":"a@example.com","rnmTenantId":"tenant-a","rnmCorrelationId":"corr-a"},
              {"event":"unsubscribe","email":"b@example.com","rnmTenantId":"tenant-a"},
              {"event":"bounce","type":"bounce","email":"c@example.com","rnmTenantId":"tenant-a","reason":"550 mailbox unavailable"},
              {"event":"bounce","type":"blocked","email":"d@example.com","rnmTenantId":"tenant-a"},
              {"event":"delivered","email":"e@example.com","rnmTenantId":"tenant-a"},
              {"event":"spamreport","email":"f@example.com","rnmTenantId":"ghost"},
              {"event":"spamreport","email":"g@example.com"}
            ]
            """;

        var response = await fixture.SendAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Collection(
            fixture.Crm.OptOuts,
            optOut =>
            {
                Assert.Equal(ConsentChannel.Email, optOut.Channel);
                Assert.Equal("a@example.com", optOut.Email);
                Assert.Equal("corr-a", optOut.CorrelationId);
                Assert.Equal("spamreport", optOut.Reason);
            },
            optOut => Assert.Equal("b@example.com", optOut.Email));
        var bounce = Assert.Single(fixture.Crm.TimelineEvents, item => item.EventType == CrmTimelineEventTypes.EmailBounced);
        Assert.Equal("550 mailbox unavailable", bounce.Metadata["reason"]);
        Assert.Contains("\"applied\":3", response.ReadBody());
        Assert.Contains("\"ignored\":4", response.ReadBody());
    }

    [Fact]
    public async Task EventWebhook_RejectsInvalidSignature()
    {
        var fixture = new WebhookFixture();

        var response = await fixture.SendAsync("[]", signatureOverride: Convert.ToBase64String(new byte[64]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(fixture.Crm.OptOuts);
    }

    [Fact]
    public async Task EventWebhook_ReturnsRetryableErrorWhenCrmFails()
    {
        var fixture = new WebhookFixture(new FakeCrmAdapter { OptOutResult = new CrmOperationResult(false) });

        var response = await fixture.SendAsync("""[{"event":"spamreport","email":"a@example.com","rnmTenantId":"tenant-a"}]""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task UnsubscribeLink_GetConfirmsAndPostUnsubscribes()
    {
        var crm = new FakeCrmAdapter();
        var function = CreateUnsubscribeFunction(crm);
        var url = UnsubscribeUrl("tenant-a", "lead@example.com");

        var get = (TestHttpResponseData)await function.RunAsync(new TestHttpRequestData("GET", url), "tenant-a", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Contains("<form method=\"post\">", get.ReadBody());
        Assert.Empty(crm.OptOuts);

        var post = (TestHttpResponseData)await function.RunAsync(new TestHttpRequestData("POST", url, "List-Unsubscribe=One-Click"), "tenant-a", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var optOut = Assert.Single(crm.OptOuts);
        Assert.Equal(ConsentChannel.Email, optOut.Channel);
        Assert.Equal("lead@example.com", optOut.Email);
        Assert.Equal("tenant-a", optOut.TenantId);
    }

    [Fact]
    public async Task UnsubscribeLink_RejectsATokenIssuedForAnotherTenant()
    {
        var crm = new FakeCrmAdapter();
        var function = CreateUnsubscribeFunction(crm);
        var otherTenantUrl = UnsubscribeUrl("tenant-b", "lead@example.com").Replace("/tenants/tenant-b/", "/tenants/tenant-a/", StringComparison.Ordinal);

        var response = (TestHttpResponseData)await function.RunAsync(new TestHttpRequestData("POST", otherTenantUrl), "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(crm.OptOuts);
    }

    private static string UnsubscribeUrl(string tenantId, string email) =>
        $"https://m1.example.com/api/tenants/{tenantId}/email/unsubscribe?e={EmailUnsubscribeToken.EncodeEmail(email)}&t={EmailUnsubscribeToken.Create(SigningKey, tenantId, email)}";

    private static EmailUnsubscribeFunction CreateUnsubscribeFunction(FakeCrmAdapter crm) =>
        new(
            new FakeTenantConfigurationProvider(),
            new StubSecretProvider(SigningKey),
            new EmailPreferenceService(crm, new FakeEventLogger()),
            new CorrelationContextFactory());

    private static string Sign(ECDsa key, string timestamp, byte[] body) =>
        Convert.ToBase64String(key.SignData(
            [.. Encoding.UTF8.GetBytes(timestamp), .. body],
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

    private sealed class WebhookFixture
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public WebhookFixture(FakeCrmAdapter? crm = null)
        {
            Crm = crm ?? new FakeCrmAdapter
            {
                LookupResult = new CrmContactLookupResult(true, "contact-c")
            };
        }

        public FakeCrmAdapter Crm { get; }

        public async Task<TestHttpResponseData> SendAsync(string body, string? signatureOverride = null)
        {
            var timestamp = Now.ToUnixTimeSeconds().ToString();
            var request = new TestHttpRequestData("POST", "https://m1.example.com/api/webhooks/sendgrid/events", body);
            request.Headers.Add(SendGridEventWebhookValidator.TimestampHeader, timestamp);
            request.Headers.Add(
                SendGridEventWebhookValidator.SignatureHeader,
                signatureOverride ?? Sign(key, timestamp, Encoding.UTF8.GetBytes(body)));
            var function = new SendGridEventWebhookFunction(
                new SendGridEventWebhookValidator(),
                new StubSecretProvider(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())),
                new KnownTenantsProvider("tenant-a"),
                new EmailPreferenceService(Crm, new FakeEventLogger()),
                new LimitedRequestBodyReader(),
                new CorrelationContextFactory(),
                new SafeErrorResponseFactory(),
                new SafeHttpResponseWriter(),
                new FakeEventLogger(),
                new FixedTimeProvider(Now));
            return (TestHttpResponseData)await function.RunAsync(request, CancellationToken.None);
        }
    }

    private sealed class KnownTenantsProvider(params string[] tenants) : ITenantConfigurationProvider
    {
        private readonly FakeTenantConfigurationProvider inner = new();

        public Task<TenantConfiguration> GetTenantConfigurationAsync(string tenantId, CancellationToken cancellationToken) =>
            tenants.Contains(tenantId)
                ? inner.GetTenantConfigurationAsync(tenantId, cancellationToken)
                : throw new ConfigurationException($"Tenant configuration '{tenantId}' was not found.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
