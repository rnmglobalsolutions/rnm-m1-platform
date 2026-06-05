using System.Net;
using System.Text;
using System.Text.Json;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Qualification;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using RNM.Platform.Infrastructure.Booking;
using RNM.Platform.Infrastructure.Secrets;
using Xunit;

namespace RNM.Platform.UnitTests.Infrastructure;

public sealed class GoHighLevelBookingAdapterTests
{
    [Fact]
    public async Task CreateBookingAsync_SendsAppointmentDetailsToGoHighLevel()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("""{"id":"appointment-123"}""")
        ]);
        var adapter = CreateAdapter(CreateCredentialsJson(), handler);

        var result = await adapter.CreateBookingAsync(CreateBookingRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("appointment-123", result.ProviderBookingId);
        Assert.Single(handler.Requests);
        Assert.Equal("/calendars/events/appointments", handler.Requests[0].RequestUri?.AbsolutePath);

        using var document = JsonDocument.Parse(handler.RequestBodies[0]);
        var root = document.RootElement;
        Assert.Equal("calendar-123", root.GetProperty("calendarId").GetString());
        Assert.Equal("location-123", root.GetProperty("locationId").GetString());
        Assert.Equal("contact-123", root.GetProperty("contactId").GetString());
        Assert.Equal("2026-05-11T15:00:00+00:00", root.GetProperty("startTime").GetString());
        Assert.Equal("2026-05-11T16:00:00+00:00", root.GetProperty("endTime").GetString());
        Assert.Equal("RNM booking - Jane Lead - Repair", root.GetProperty("title").GetString());
        Assert.Equal("123 Main Street, Addison, TX 75001", root.GetProperty("address").GetString());

        var notes = root.GetProperty("notes").GetString();
        Assert.Contains("Customer: Jane Lead", notes);
        Assert.Contains("Service: Repair", notes);
        Assert.Contains("Property type: residential", notes);
        Assert.Contains("Service address: 123 Main Street, Addison, TX 75001", notes);
        Assert.Contains("ZIP code: 75001", notes);
        Assert.Contains("Urgency: non_urgent", notes);
        Assert.Contains("Preferred time: Afternoon", notes);
        Assert.Contains("Correlation ID: corr-123", notes);
        Assert.Contains("Phone: +15551234567", notes);
        Assert.Contains("Email: lead@example.com", notes);
    }

    [Fact]
    public async Task CreateBookingAsync_AddsZipCodeToAppointmentAddress_WhenAddressDoesNotIncludeZip()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("""{"appointmentId":"appointment-123"}""")
        ]);
        var adapter = CreateAdapter(CreateCredentialsJson(), handler);

        var result = await adapter.CreateBookingAsync(
            CreateBookingRequest(serviceAddress: "7451 Houston, Texas", zipCode: "77002"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        using var document = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal("7451 Houston, Texas 77002", document.RootElement.GetProperty("address").GetString());
        Assert.Contains(
            "Service address: 7451 Houston, Texas 77002",
            document.RootElement.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task CreateBookingAsync_ReturnsSafeFailure_WhenProviderFails()
    {
        var adapter = CreateAdapter(
            CreateCredentialsJson(),
            new QueueHttpMessageHandler([
                new HttpResponseMessage(HttpStatusCode.BadGateway)
            ]));

        var result = await adapter.CreateBookingAsync(CreateBookingRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(BookingFailureReason.AdapterFailure, result.FailureReason);
    }

    [Fact]
    public async Task CreateBookingAsync_ReturnsSafeFailure_WhenCredentialsAreIncomplete()
    {
        var adapter = CreateAdapter(
            """{"accessToken":"token"}""",
            new QueueHttpMessageHandler([]));

        var result = await adapter.CreateBookingAsync(CreateBookingRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(BookingFailureReason.AdapterFailure, result.FailureReason);
    }

    private static GoHighLevelBookingAdapter CreateAdapter(
        string secretValue,
        HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://services.leadconnectorhq.com/")
        };

        return new GoHighLevelBookingAdapter(
            new StubTenantConfigurationProvider(),
            new StubSecretProvider(secretValue),
            httpClient);
    }

    private static CreateBookingRequest CreateBookingRequest(
        string serviceAddress = "123 Main Street, Addison, TX 75001",
        string zipCode = "75001")
    {
        var leadData = new QualifiedLeadData(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = "Jane Lead",
                ["email"] = "lead@example.com",
                ["propertyType"] = "residential",
                ["serviceAddress"] = serviceAddress,
                ["urgency"] = "non_urgent"
            },
            zipCode,
            "+15551234567");

        return new CreateBookingRequest(
            "tenant-a",
            "hvac",
            "corr-123",
            leadData,
            new AvailableSlot(
                "slot-1",
                new DateTimeOffset(2026, 5, 11, 15, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 11, 16, 0, 0, TimeSpan.Zero)),
            "Repair",
            "Afternoon",
            "contact-123");
    }

    private static string CreateCredentialsJson() =>
        """
        {
          "accessToken": "token",
          "locationId": "location-123",
          "calendarId": "calendar-123",
          "apiVersion": "2021-07-28"
        }
        """;

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

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
                new ServiceAreaConfiguration(["75001"], ["Addison"], null),
                new ProviderConfiguration("AzureTable", "GoHighLevelCalendar", "Twilio", "SendGrid"),
                new SecretNameConfiguration(
                    "crm",
                    "booking",
                    "vapi",
                    "sid",
                    "token",
                    "email",
                    CrmCredentials: "crm",
                    BookingCredentials: "booking"),
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
        private readonly string secretValue;

        public StubSecretProvider(string secretValue)
        {
            this.secretValue = secretValue;
        }

        public Task<string> GetSecretAsync(string secretName, CancellationToken cancellationToken)
        {
            return Task.FromResult(secretValue);
        }
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses;

        public QueueHttpMessageHandler(IEnumerable<HttpResponseMessage> responses)
        {
            this.responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return responses.Count > 0
                ? responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
        }
    }
}
