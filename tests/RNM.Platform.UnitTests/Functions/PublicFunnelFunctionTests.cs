using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Classes;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.LeadIntake;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.LeadIntake;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.UnitTests.Classes;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class PublicFunnelFunctionTests
{
    [Fact]
    public async Task ConsultationAsync_ValidRequest_CreatesLeadWithoutSecretsInBrowser()
    {
        var fixture = new Fixture();
        var request = CreateConsultationRequest("https://rnmglobalsolutions.com");

        var response = (TestHttpResponseData)await fixture.Function.ConsultationAsync(
            request,
            "tenant-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"received\":true", response.ReadBody());
        Assert.Equal("https://rnmglobalsolutions.com", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.NotNull(fixture.Crm.LastUpsertRequest);
        Assert.Equal("WebFunnel", fixture.Crm.LastUpsertRequest.Attributes[CrmContactAttributeNames.LeadSource]);
        Assert.Equal("consultation", fixture.Crm.LastUpsertRequest.Attributes["requestedNextStep"]);
    }

    [Fact]
    public async Task ConsultationAsync_RejectsDisallowedOrigin()
    {
        var fixture = new Fixture();
        var request = CreateConsultationRequest("https://evil.example");

        var response = (TestHttpResponseData)await fixture.Function.ConsultationAsync(
            request,
            "tenant-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(fixture.Crm.LastUpsertRequest);
    }

    [Fact]
    public async Task ConsultationAsync_EmailOnlyConsent_DoesNotGrantSmsConsent()
    {
        var fixture = new Fixture();
        var request = CreateConsultationRequest(
            "https://rnmglobalsolutions.com",
            consentSms: false,
            consentEmail: true);

        var response = (TestHttpResponseData)await fixture.Function.ConsultationAsync(
            request,
            "tenant-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CrmConsentStatuses.Unknown, fixture.Crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.SmsConsentStatus]);
        Assert.Equal(CrmConsentStatuses.OptIn, fixture.Crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.EmailConsentStatus]);
        Assert.Equal("consentEmail", fixture.Crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.EmailConsentSourceField]);
    }

    [Fact]
    public async Task MasterClassRegistrationAsync_ValidRequest_RegistersLeadWithoutClassSecret()
    {
        var session = CreateSession();
        var fixture = new Fixture(session);
        var request = CreateMasterClassRequest("https://rnmglobalsolutions.com");

        var response = (TestHttpResponseData)await fixture.Function.MasterClassRegistrationAsync(
            request,
            "tenant-a",
            session.SessionId,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"registered\":true", response.ReadBody());
        Assert.Equal("https://rnmglobalsolutions.com", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Single(fixture.Sms.Requests);
        Assert.Single(fixture.Email.Requests);
        Assert.Equal("class_registration", fixture.Crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.SourceFunnel]);
    }

    [Fact]
    public async Task MasterClassRegistrationAsync_HoneypotFilled_ReturnsSafeSuccessWithoutRegistering()
    {
        var session = CreateSession();
        var fixture = new Fixture(session);
        var request = CreateMasterClassRequest("https://rnmglobalsolutions.com", companyWebsiteConfirm: "bot");

        var response = (TestHttpResponseData)await fixture.Function.MasterClassRegistrationAsync(
            request,
            "tenant-a",
            session.SessionId,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"received\":true", response.ReadBody());
        Assert.Empty(fixture.Sms.Requests);
        Assert.Empty(fixture.Email.Requests);
    }

    [Fact]
    public async Task MasterClassRegistrationAsync_SmsOnlyConsent_DoesNotSendEmail()
    {
        var session = CreateSession();
        var fixture = new Fixture(session);
        var request = CreateMasterClassRequest(
            "https://rnmglobalsolutions.com",
            consentSms: true,
            consentEmail: false);

        var response = (TestHttpResponseData)await fixture.Function.MasterClassRegistrationAsync(
            request,
            "tenant-a",
            session.SessionId,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(fixture.Sms.Requests);
        Assert.Empty(fixture.Email.Requests);
        Assert.Equal(CrmConsentStatuses.OptIn, fixture.Crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.SmsConsentStatus]);
        Assert.Equal(CrmConsentStatuses.Unknown, fixture.Crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.EmailConsentStatus]);
    }

    private static TestHttpRequestData CreateConsultationRequest(
        string origin,
        bool consentSms = true,
        bool consentEmail = false)
    {
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/funnels/consultation",
            $$"""
            {
              "submissionId": "submission-a",
              "customerName": "Jane Lead",
              "customerPhoneNumber": "+15551234567",
              "customerEmail": "jane@example.com",
              "campaignId": "financial-video-v1",
              "funnelType": "financial_education",
              "primaryGoal": "family_protection",
              "timeline": "under_30_days",
              "state": "TX",
              "consentSms": {{consentSms.ToString().ToLowerInvariant()}},
              "consentEmail": {{consentEmail.ToString().ToLowerInvariant()}},
              "consentTextVersion": "web-funnel-v1",
              "consentDisclosureText": "I agree to receive messages from Yartex. Reply STOP to opt out."
            }
            """);
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static TestHttpRequestData CreateMasterClassRequest(
        string origin,
        string companyWebsiteConfirm = "",
        bool consentSms = true,
        bool consentEmail = true)
    {
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/funnels/masterclass/session-a/registrations",
            $$"""
            {
              "customerName": "Jane Lead",
              "customerPhoneNumber": "+15551234567",
              "customerEmail": "jane@example.com",
              "campaignId": "financial-video-v1",
              "funnelType": "financial_education",
              "primaryGoal": "family_protection",
              "timeline": "under_30_days",
              "state": "TX",
              "consentSms": {{consentSms.ToString().ToLowerInvariant()}},
              "consentEmail": {{consentEmail.ToString().ToLowerInvariant()}},
              "consentTextVersion": "web-funnel-v1",
              "consentDisclosureText": "I agree to receive messages from Yartex. Reply STOP to opt out.",
              "companyWebsiteConfirm": "{{companyWebsiteConfirm}}"
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
            new DateTimeOffset(2027, 6, 10, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 6, 11, 0, 0, 0, TimeSpan.Zero),
            "America/Chicago",
            "https://zoom.us/j/123",
            100,
            "financial-video-v1",
            new Dictionary<string, string>());

    private sealed class Fixture
    {
        public Fixture(params ClassSessionRecord[] sessions)
        {
            var logger = new FakeEventLogger();
            var tenantProvider = new FakeTenantConfigurationProvider();
            Crm = new FakeCrmAdapter();
            Sms = new FakeSmsSender();
            Email = new FakeEmailSender();
            var classStore = new FakeClassSessionStore(sessions);
            var retryScheduler = new FakeConfirmationRetryScheduler();
            var intakeService = new InboundLeadIntakeService(
                new FakeReceiptStore(),
                Crm,
                new CrmApplicationService(Crm, logger),
                tenantProvider,
                retryScheduler,
                RepositoryConfiguration.Classifier(tenantProvider, logger),
                logger);
            var classRegistrationService = new ClassRegistrationService(
                tenantProvider,
                classStore,
                Crm,
                new ClassNotificationService(Sms, Email, Crm, logger, new AllowingSmsEligibilityGate()),
                logger);

            Function = new PublicFunnelFunction(
                intakeService,
                classRegistrationService,
                tenantProvider,
                new LimitedRequestBodyReader(),
                new CorrelationContextFactory(),
                new SafeErrorResponseFactory(),
                new SafeHttpResponseWriter(),
                logger);
        }

        public PublicFunnelFunction Function { get; }
        public FakeCrmAdapter Crm { get; }
        public FakeSmsSender Sms { get; }
        public FakeEmailSender Email { get; }
    }

    private sealed class FakeReceiptStore : IInboundLeadReceiptStore
    {
        public Task<InboundLeadReceiptClaimResult> TryBeginAsync(
            InboundLeadReceiptClaimRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new InboundLeadReceiptClaimResult(InboundLeadReceiptClaimState.Acquired, "row-a", "lease-a"));

        public Task CompleteAsync(InboundLeadReceiptCompletionRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task FailAsync(InboundLeadReceiptFailureRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
