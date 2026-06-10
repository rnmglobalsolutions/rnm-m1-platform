using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using RNM.Platform.Infrastructure.Booking;
using RNM.Platform.Infrastructure.Crm;
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
        string bookingProvider) =>
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
                    "Appointment {{bookingStart}}")));

    private sealed class TenantConfigurationProvider(TenantConfiguration tenant)
        : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(tenant);
    }

    private sealed class SecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(
            string secretName,
            CancellationToken cancellationToken) =>
            Task.FromResult("configured-secret");
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
    }
}
