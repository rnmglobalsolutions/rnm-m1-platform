using Microsoft.Extensions.DependencyInjection;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Qualification;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using RNM.Platform.Infrastructure.Booking;
using RNM.Platform.Infrastructure.Crm;
using Xunit;

namespace RNM.Platform.UnitTests.Infrastructure;

public sealed class ProviderDispatcherTests
{
    [Fact]
    public async Task ConfiguredCrmAdapter_RoutesAzureTableProvider()
    {
        var azureTable = new RecordingCrmProviderAdapter("AzureTable", providerContactId: "azure-contact");
        var goHighLevel = new RecordingCrmProviderAdapter("GoHighLevel", providerContactId: "ghl-contact");
        var adapter = new ConfiguredCrmAdapter(
            new StubTenantConfigurationProvider(crmProvider: "AzureTable"),
            CreateServiceProvider(azureTable, goHighLevel));

        var result = await adapter.UpsertContactAsync(CreateCrmUpsertRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("azure-contact", result.ProviderContactId);
        Assert.Equal(1, azureTable.UpsertCallCount);
        Assert.Equal(0, goHighLevel.UpsertCallCount);
    }

    [Fact]
    public async Task ConfiguredCrmAdapter_RoutesGoHighLevelProvider()
    {
        var azureTable = new RecordingCrmProviderAdapter("AzureTable", providerContactId: "azure-contact");
        var goHighLevel = new RecordingCrmProviderAdapter("GoHighLevel", providerContactId: "ghl-contact");
        var adapter = new ConfiguredCrmAdapter(
            new StubTenantConfigurationProvider(crmProvider: "GoHighLevel"),
            CreateServiceProvider(azureTable, goHighLevel));

        var result = await adapter.UpsertContactAsync(CreateCrmUpsertRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("ghl-contact", result.ProviderContactId);
        Assert.Equal(0, azureTable.UpsertCallCount);
        Assert.Equal(1, goHighLevel.UpsertCallCount);
    }

    [Fact]
    public async Task ConfiguredCrmAdapter_ReturnsSafeFailureForUnknownProvider()
    {
        var adapter = new ConfiguredCrmAdapter(
            new StubTenantConfigurationProvider(crmProvider: "MissingCrm"),
            CreateServiceProvider(new RecordingCrmProviderAdapter("AzureTable", providerContactId: "azure-contact")));

        var result = await adapter.UpsertContactAsync(CreateCrmUpsertRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(CrmFailureReason.AdapterFailure, result.FailureReason);
    }

    [Fact]
    public async Task ConfiguredBookingAdapter_RoutesGoogleCalendarProvider()
    {
        var google = new RecordingBookingProviderAdapter("GoogleCalendar", providerBookingId: "google-booking");
        var goHighLevel = new RecordingBookingProviderAdapter("GoHighLevelCalendar", providerBookingId: "ghl-booking");
        var adapter = new ConfiguredBookingAdapter(
            new StubTenantConfigurationProvider(bookingProvider: "GoogleCalendar"),
            CreateServiceProvider(google, goHighLevel));

        var result = await adapter.CreateBookingAsync(CreateBookingRequest(providerContactId: "crm-contact"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("google-booking", result.ProviderBookingId);
        Assert.Equal(1, google.CreateCallCount);
        Assert.Equal(0, goHighLevel.CreateCallCount);
        Assert.Null(google.LastCreateBookingRequest?.ProviderContactId);
    }

    [Fact]
    public async Task ConfiguredBookingAdapter_RoutesGoHighLevelCalendarProvider()
    {
        var google = new RecordingBookingProviderAdapter("GoogleCalendar", providerBookingId: "google-booking");
        var goHighLevel = new RecordingBookingProviderAdapter("GoHighLevelCalendar", providerBookingId: "ghl-booking");
        var adapter = new ConfiguredBookingAdapter(
            new StubTenantConfigurationProvider(bookingProvider: "GoHighLevelCalendar"),
            CreateServiceProvider(google, goHighLevel));

        var result = await adapter.CreateBookingAsync(CreateBookingRequest(providerContactId: "crm-contact"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("ghl-booking", result.ProviderBookingId);
        Assert.Equal(0, google.CreateCallCount);
        Assert.Equal(1, goHighLevel.CreateCallCount);
        Assert.Equal("crm-contact", goHighLevel.LastCreateBookingRequest?.ProviderContactId);
    }

    [Fact]
    public async Task ConfiguredBookingAdapter_ReturnsSafeFailureForUnknownProvider()
    {
        var adapter = new ConfiguredBookingAdapter(
            new StubTenantConfigurationProvider(bookingProvider: "MissingBooking"),
            CreateServiceProvider(new RecordingBookingProviderAdapter("GoogleCalendar", providerBookingId: "google-booking")));

        var result = await adapter.CreateBookingAsync(CreateBookingRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(BookingFailureReason.AdapterFailure, result.FailureReason);
    }

    private static CrmContactUpsertRequest CreateCrmUpsertRequest() =>
        new(
            "tenant-a",
            "hvac",
            "corr-123",
            ProviderContactId: null,
            PhoneNumber: "+15551234567",
            Email: "lead@example.com",
            Name: "Jane Lead",
            ZipCode: "75001",
            new Dictionary<string, string>());

    private static IServiceProvider CreateServiceProvider(params ICrmProviderAdapter[] adapters)
    {
        var services = new ServiceCollection();
        foreach (var adapter in adapters)
        {
            services.AddSingleton(adapter);
        }

        return services.BuildServiceProvider();
    }

    private static IServiceProvider CreateServiceProvider(params IBookingProviderAdapter[] adapters)
    {
        var services = new ServiceCollection();
        foreach (var adapter in adapters)
        {
            services.AddSingleton(adapter);
        }

        return services.BuildServiceProvider();
    }

    private static CreateBookingRequest CreateBookingRequest(string? providerContactId = null)
    {
        var leadData = new QualifiedLeadData(
            new Dictionary<string, string>
            {
                ["name"] = "Jane Lead",
                ["email"] = "lead@example.com"
            },
            "75001",
            "+15551234567");

        return new CreateBookingRequest(
            "tenant-a",
            "hvac",
            "corr-123",
            leadData,
            new AvailableSlot(
                "slot-1",
                new DateTimeOffset(2026, 5, 1, 14, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 1, 15, 0, 0, TimeSpan.Zero)),
            "Repair",
            "Afternoon",
            providerContactId);
    }

    private sealed class StubTenantConfigurationProvider : ITenantConfigurationProvider
    {
        private readonly string crmProvider;
        private readonly string bookingProvider;

        public StubTenantConfigurationProvider(
            string crmProvider = "AzureTable",
            string bookingProvider = "GoogleCalendar")
        {
            this.crmProvider = crmProvider;
            this.bookingProvider = bookingProvider;
        }

        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TenantConfiguration(
                new TenantId(tenantId),
                new VerticalId("hvac"),
                "Tenant A",
                "America/Chicago",
                new ServiceAreaConfiguration(["75001"], ["Addison"], null),
                new ProviderConfiguration(crmProvider, bookingProvider, "Twilio", "SendGrid"),
                new SecretNameConfiguration("crm", "booking", "vapi", "sid", "token", "email"),
                new CommunicationConfiguration(
                    "+15550001000",
                    "booking@example.com",
                    new ConfirmationTemplateConfiguration("sms"))));
        }
    }

    private sealed class RecordingCrmProviderAdapter : ICrmProviderAdapter
    {
        private readonly string providerContactId;

        public RecordingCrmProviderAdapter(string providerName, string providerContactId)
        {
            ProviderName = providerName;
            this.providerContactId = providerContactId;
        }

        public string ProviderName { get; }

        public int UpsertCallCount { get; private set; }

        public Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(
            CrmContactLookupRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CrmContactLookupResult(false, null));

        public Task<CrmContactUpsertResult> UpsertContactAsync(
            CrmContactUpsertRequest request,
            CancellationToken cancellationToken)
        {
            UpsertCallCount++;
            return Task.FromResult(new CrmContactUpsertResult(true, Created: true, providerContactId));
        }

        public Task<CrmOperationResult> AddInteractionNoteAsync(
            CrmInteractionNoteRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CrmOperationResult(true));

        public Task<CrmOperationResult> ApplyTagsAsync(
            CrmTagRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CrmOperationResult(true));

        public Task<CrmOperationResult> LinkBookingToContactAsync(
            CrmBookingLinkRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CrmOperationResult(true));
    }

    private sealed class RecordingBookingProviderAdapter : IBookingProviderAdapter
    {
        private readonly string providerBookingId;

        public RecordingBookingProviderAdapter(string providerName, string providerBookingId)
        {
            ProviderName = providerName;
            this.providerBookingId = providerBookingId;
        }

        public string ProviderName { get; }

        public int CreateCallCount { get; private set; }

        public CreateBookingRequest? LastCreateBookingRequest { get; private set; }

        public Task<BookingAvailabilityResult> CheckAvailabilityAsync(
            BookingAvailabilityRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BookingAvailabilityResult(true, [CreateBookingRequest().Slot]));

        public Task<CreateBookingResult> CreateBookingAsync(
            CreateBookingRequest request,
            CancellationToken cancellationToken)
        {
            CreateCallCount++;
            LastCreateBookingRequest = request;
            return Task.FromResult(new CreateBookingResult(true, providerBookingId));
        }
    }
}
