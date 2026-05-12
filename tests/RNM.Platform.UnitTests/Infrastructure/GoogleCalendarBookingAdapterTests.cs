using System.Net;
using System.Text;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Qualification;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using RNM.Platform.Infrastructure.Booking;
using RNM.Platform.Infrastructure.Secrets;
using Xunit;

namespace RNM.Platform.UnitTests.Infrastructure;

public sealed class GoogleCalendarBookingAdapterTests
{
    [Fact]
    public async Task CheckAvailabilityAsync_ReturnsAvailability_WhenFreeBusyHasOpenTime()
    {
        var adapter = CreateAdapter(
            secretValue: CreateCredentialsJson(),
            handler: new QueueHttpMessageHandler([
                JsonResponse("""{"calendars":{"primary":{"busy":[]}}}""")
            ]));

        var result = await adapter.CheckAvailabilityAsync(CreateAvailabilityRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.HasAvailability);
        Assert.NotEmpty(result.Slots);
    }

    [Fact]
    public async Task CheckAvailabilityAsync_ReturnsNoAvailability_WhenFreeBusyBlocksBusinessHours()
    {
        var adapter = CreateAdapter(
            secretValue: CreateCredentialsJson(lookAheadDays: 1),
            handler: new QueueHttpMessageHandler([
                JsonResponse("""
                {
                  "calendars": {
                    "primary": {
                      "busy": [
                        { "start": "2000-01-01T00:00:00Z", "end": "2100-01-01T00:00:00Z" }
                      ]
                    }
                  }
                }
                """)
            ]));

        var result = await adapter.CheckAvailabilityAsync(CreateAvailabilityRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.HasAvailability);
    }

    [Fact]
    public async Task CreateBookingAsync_CreatesCalendarEvent_WhenSlotIsStillOpen()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("""{"calendars":{"primary":{"busy":[]}}}"""),
            JsonResponse("""{"id":"event-123"}""")
        ]);
        var adapter = CreateAdapter(CreateCredentialsJson(), handler);

        var result = await adapter.CreateBookingAsync(CreateBookingRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("event-123", result.ProviderBookingId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/calendar/v3/freeBusy", handler.Requests[0].RequestUri?.AbsolutePath);
        Assert.EndsWith("/calendar/v3/calendars/primary/events", handler.Requests[1].RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task CreateBookingAsync_ReturnsSafeFailure_WhenProviderFails()
    {
        var adapter = CreateAdapter(
            CreateCredentialsJson(),
            new QueueHttpMessageHandler([
                JsonResponse("""{"calendars":{"primary":{"busy":[]}}}"""),
                new HttpResponseMessage(HttpStatusCode.BadGateway)
            ]));

        var result = await adapter.CreateBookingAsync(CreateBookingRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(BookingFailureReason.AdapterFailure, result.FailureReason);
    }

    [Fact]
    public async Task CreateBookingAsync_ReturnsSafeFailure_WhenCredentialsAreMissing()
    {
        var adapter = CreateAdapter(
            secretValue: "{}",
            handler: new QueueHttpMessageHandler([]));

        var result = await adapter.CreateBookingAsync(CreateBookingRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(BookingFailureReason.AdapterFailure, result.FailureReason);
    }

    private static GoogleCalendarBookingAdapter CreateAdapter(
        string secretValue,
        HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://www.googleapis.com/calendar/v3/")
        };

        return new GoogleCalendarBookingAdapter(
            new StubTenantConfigurationProvider(),
            new StubSecretProvider(secretValue),
            httpClient);
    }

    private static BookingAvailabilityRequest CreateAvailabilityRequest() =>
        new(
            "tenant-a",
            "hvac",
            "corr-123",
            "Repair",
            "Afternoon",
            "America/Chicago");

    private static CreateBookingRequest CreateBookingRequest()
    {
        var leadData = new QualifiedLeadData(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
                new DateTimeOffset(2026, 5, 11, 15, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 11, 16, 0, 0, TimeSpan.Zero)),
            "Repair",
            "Afternoon");
    }

    private static string CreateCredentialsJson(int lookAheadDays = 14) =>
        $$"""
        {
          "calendarId": "primary",
          "accessToken": "token",
          "timeZone": "America/Chicago",
          "businessStart": "09:00:00",
          "businessEnd": "17:00:00",
          "appointmentMinutes": 60,
          "slotStepMinutes": 60,
          "lookAheadDays": {{lookAheadDays}},
          "includeWeekends": true
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
                new ProviderConfiguration("AzureTable", "GoogleCalendar", "Twilio", "SendGrid"),
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
                    new ConfirmationTemplateConfiguration("sms"))));
        }
    }

    private sealed class StubSecretProvider : ISecretProvider
    {
        private readonly string secretValue;

        public StubSecretProvider(string secretValue)
        {
            this.secretValue = secretValue;
        }

        public Task<string> GetSecretAsync(string secretName, CancellationToken cancellationToken) =>
            Task.FromResult(secretValue);
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses;

        public QueueHttpMessageHandler(IEnumerable<HttpResponseMessage> responses)
        {
            this.responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responses.Count == 0
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responses.Dequeue());
        }
    }
}
