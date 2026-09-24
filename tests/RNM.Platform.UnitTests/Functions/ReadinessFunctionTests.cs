using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.FollowUps;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using RNM.Platform.Infrastructure.Booking;
using RNM.Platform.Infrastructure.Crm;
using RNM.Platform.Infrastructure.Providers;
using RNM.Platform.Infrastructure.Secrets;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class ReadinessFunctionTests
{
    [Fact]
    public async Task HandleAsync_ReturnsNotReady_WhenConfiguredProvidersAreUnsupported()
    {
        var function = new ReadinessFunction(
            new TenantConfigurationProvider(CreateTenant("UnknownCrm", "UnknownBooking")),
            new VerticalConfigurationProvider(),
            new SecretProvider(),
            new ApiKeyRequestValidator(),
            CreateRuntimeConfiguration(),
            [new BookingProviderAdapter("GoogleCalendar")],
            [new CrmProviderAdapter("GoHighLevel")]);
        var request = new TestHttpRequestData(
            "GET",
            "https://example.test/api/tenants/tenant-a/ready");
        request.Headers.Add("x-rnm-api-key", "internal-api-key");

        var response = await function.HandleAsync(
            request,
            "tenant-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = Assert.IsType<TestHttpResponseData>(response).ReadBody();
        Assert.Contains("\"name\":\"bookingProvider\",\"ready\":false", body);
        Assert.Contains("\"name\":\"crmProvider\",\"ready\":false", body);
        Assert.Contains("\"status\":\"blocked\"", body);
    }

    [Fact]
    public async Task HandleAsync_ReturnsReady_WhenRequiredChecksPass()
    {
        var previousStorage = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        var previousSendGrid = Environment.GetEnvironmentVariable("SENDGRID_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AzureWebJobsStorage", "UseDevelopmentStorage=true");
            Environment.SetEnvironmentVariable("SENDGRID_API_KEY", "sendgrid-key");
            var function = new ReadinessFunction(
                new TenantConfigurationProvider(CreateTenant(ProviderNames.AzureTable, ProviderNames.GoogleCalendar)),
                new VerticalConfigurationProvider(),
                new SecretProvider(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["vapi-secret"] = "voice-secret",
                    ["twilio-account-sid"] = "AC123",
                    ["twilio-auth-token"] = "token",
                    ["booking-secret"] = """
                    {
                      "calendarId": "primary",
                      "refreshToken": "refresh-token",
                      "clientId": "client-id",
                      "clientSecret": "client-secret",
                      "timeZone": "America/Chicago"
                    }
                    """
                }),
                new ApiKeyRequestValidator(),
                CreateRuntimeConfiguration(),
                [new BookingProviderAdapter(ProviderNames.GoogleCalendar)],
                [new CrmProviderAdapter(ProviderNames.AzureTable)]);
            var request = new TestHttpRequestData(
                "GET",
                "https://example.test/api/tenants/tenant-a/readiness");
            request.Headers.Add("x-rnm-api-key", "internal-api-key");

            var response = await function.HandleAsync(
                request,
                "tenant-a",
                CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = Assert.IsType<TestHttpResponseData>(response).ReadBody();
            Assert.Contains("\"status\":\"ready\"", body);
            Assert.Contains("\"name\":\"bookingCredentialsShape\",\"ready\":true", body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AzureWebJobsStorage", previousStorage);
            Environment.SetEnvironmentVariable("SENDGRID_API_KEY", previousSendGrid);
        }
    }

    [Fact]
    public async Task HandleAsync_ReturnsBlocked_WhenAutomationTenantIsNotActive()
    {
        var previousStorage = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        var previousSendGrid = Environment.GetEnvironmentVariable("SENDGRID_API_KEY");
        var previousActiveTenants = Environment.GetEnvironmentVariable("RNM_ACTIVE_TENANTS");
        try
        {
            Environment.SetEnvironmentVariable("AzureWebJobsStorage", "UseDevelopmentStorage=true");
            Environment.SetEnvironmentVariable("SENDGRID_API_KEY", "sendgrid-key");
            Environment.SetEnvironmentVariable("RNM_ACTIVE_TENANTS", "other-tenant");
            var function = new ReadinessFunction(
                new TenantConfigurationProvider(CreateTenant(ProviderNames.AzureTable, ProviderNames.GoogleCalendar, followUpsEnabled: true)),
                new VerticalConfigurationProvider(),
                new SecretProvider(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["vapi-secret"] = "voice-secret",
                    ["twilio-account-sid"] = "AC123",
                    ["twilio-auth-token"] = "token",
                    ["booking-secret"] = """
                    {
                      "calendarId": "primary",
                      "refreshToken": "refresh-token",
                      "clientId": "client-id",
                      "clientSecret": "client-secret"
                    }
                    """
                }),
                new ApiKeyRequestValidator(),
                CreateRuntimeConfiguration(),
                [new BookingProviderAdapter(ProviderNames.GoogleCalendar)],
                [new CrmProviderAdapter(ProviderNames.AzureTable)]);
            var request = new TestHttpRequestData(
                "GET",
                "https://example.test/api/tenants/tenant-a/readiness");
            request.Headers.Add("x-rnm-api-key", "internal-api-key");

            var response = await function.HandleAsync(
                request,
                "tenant-a",
                CancellationToken.None);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = Assert.IsType<TestHttpResponseData>(response).ReadBody();
            Assert.Contains("\"name\":\"automationActiveTenant\",\"ready\":false", body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AzureWebJobsStorage", previousStorage);
            Environment.SetEnvironmentVariable("SENDGRID_API_KEY", previousSendGrid);
            Environment.SetEnvironmentVariable("RNM_ACTIVE_TENANTS", previousActiveTenants);
        }
    }

    private static RnmRuntimeConfiguration CreateRuntimeConfiguration() =>
        new(
            "prod",
            "../../../config",
            "internal-api-key",
            "https://example-vault.vault.azure.net/",
            AllowEnvironmentSecretFallback: false,
            RequireInternalApiKey: true);

    private static TenantConfiguration CreateTenant(
        string crmProvider,
        string bookingProvider,
        bool followUpsEnabled = false) =>
        new(
            new TenantId("tenant-a"),
            new VerticalId("hvac"),
            "Tenant A HVAC",
            "America/Chicago",
            new ServiceAreaConfiguration(["75001"], [], null),
            new ProviderConfiguration(crmProvider, bookingProvider, "Twilio", "SendGrid"),
            new SecretNameConfiguration(
                "crm-secret",
                "booking-secret",
                "vapi-secret",
                "twilio-account-sid",
                "twilio-auth-token",
                "email-secret"),
            new CommunicationConfiguration(
                "+15550001000",
                "booking@example.com",
                new ConfirmationTemplateConfiguration(
                    "Appointment {{bookingDate}}",
                    "Appointment {{bookingDate}}",
                    "Appointment {{bookingStart}}"),
                BusinessNotificationEmail: "ops@example.com"),
            FollowUps: followUpsEnabled
                ? new FollowUpAutomationConfiguration(
                    Enabled: true,
                    Sequences:
                    [
                        new FollowUpSequenceConfiguration(
                            "lead-needs-follow-up",
                            FollowUpTriggers.LeadFollowUpRequired,
                            [
                                new FollowUpStepConfiguration(
                                    30,
                                    FollowUpChannels.Sms,
                                    SmsBodyTemplate: "Hi {{customerName}}.")
                            ])
                    ])
                : null);

    private sealed class TenantConfigurationProvider(TenantConfiguration tenant)
        : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(tenant);
    }

    private sealed class VerticalConfigurationProvider : IVerticalConfigurationProvider
    {
        public Task<VerticalConfiguration> GetVerticalConfigurationAsync(
            string verticalId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new VerticalConfiguration(
                new VerticalId(verticalId),
                "HVAC",
                ["serviceNeed"],
                ["inbound"],
                ServiceAreaFieldAliasConfiguration.Defaults()));
    }

    private sealed class SecretProvider : ISecretProvider
    {
        private readonly IReadOnlyDictionary<string, string> secrets;

        public SecretProvider()
            : this(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["crm-secret"] = "configured-secret",
                ["booking-secret"] = "configured-secret",
                ["vapi-secret"] = "configured-secret",
                ["twilio-account-sid"] = "configured-secret",
                ["twilio-auth-token"] = "configured-secret",
                ["email-secret"] = "configured-secret"
            })
        {
        }

        public SecretProvider(IReadOnlyDictionary<string, string> secrets)
        {
            this.secrets = secrets;
        }

        public Task<string> GetSecretAsync(
            string secretName,
            CancellationToken cancellationToken) =>
            secrets.TryGetValue(secretName, out var value)
                ? Task.FromResult(value)
                : throw new SecretRetrievalException("Secret was not found.");
    }

    private sealed class BookingProviderAdapter(string providerName) : IBookingProviderAdapter
    {
        public string ProviderName { get; } = providerName;

        public Task<BookingAvailabilityResult> CheckAvailabilityAsync(
            BookingAvailabilityRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CreateBookingResult> CreateBookingAsync(
            CreateBookingRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CrmProviderAdapter(string providerName) : ICrmProviderAdapter
    {
        public string ProviderName { get; } = providerName;

        public Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(
            CrmContactLookupRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CrmContactUpsertResult> UpsertContactAsync(
            CrmContactUpsertRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CrmOperationResult> AddInteractionNoteAsync(
            CrmInteractionNoteRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CrmOperationResult> ApplyTagsAsync(
            CrmTagRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CrmOperationResult> LinkBookingToContactAsync(
            CrmBookingLinkRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CrmOperationResult> AddTimelineEventAsync(
            CrmTimelineEventRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CrmOperationResult> MarkFollowUpRequiredAsync(
            CrmFollowUpRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CrmLeadQueryResult> GetLeadsByStatusAsync(
            CrmLeadQueryRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CrmLeadQueryResult> GetLeadsByCampaignAsync(
            CrmLeadQueryRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CrmNextLeadToCallResult> GetNextLeadToCallAsync(
            CrmNextLeadToCallRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CrmOperationResult> RecordOutboundAttemptAsync(
            CrmOutboundAttemptRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CrmOperationResult> MarkLeadReactivatedAsync(
            CrmLeadReactivationRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CrmOperationResult> MarkOptOutAsync(
            CrmOptOutRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
