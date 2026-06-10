using System.Net;
using System.Text;
using System.Text.Json;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using RNM.Platform.Infrastructure.Crm;
using RNM.Platform.Infrastructure.Secrets;
using Xunit;

namespace RNM.Platform.UnitTests.Infrastructure;

public sealed class GoHighLevelCrmAdapterTests
{
    [Fact]
    public async Task AddInteractionNoteAsync_PostsNoteToContact()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.Created);
        var adapter = CreateAdapter(handler);

        var result = await adapter.AddInteractionNoteAsync(
            new CrmInteractionNoteRequest("tenant-a", "corr-123", "contact-123", "Booked by AI."),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("/contacts/contact-123/notes", handler.RequestUri?.AbsolutePath);
        using var document = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("Booked by AI.", document.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task ApplyTagsAsync_PostsTagsToContact()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.Created);
        var adapter = CreateAdapter(handler);

        var result = await adapter.ApplyTagsAsync(
            new CrmTagRequest("tenant-a", "corr-123", "contact-123", ["Inbound Call", "AI Booked"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("/contacts/contact-123/tags", handler.RequestUri?.AbsolutePath);
        using var document = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(2, document.RootElement.GetProperty("tags").GetArrayLength());
    }

    private static GoHighLevelCrmAdapter CreateAdapter(HttpMessageHandler handler)
    {
        return new GoHighLevelCrmAdapter(
            new StubTenantConfigurationProvider(),
            new StubSecretProvider(),
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://services.leadconnectorhq.com/")
            });
    }

    private sealed class StubTenantConfigurationProvider : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TenantConfiguration(
                new TenantId(tenantId),
                new VerticalId("hvac"),
                "Tenant A",
                "America/Chicago",
                new ServiceAreaConfiguration(["75001"], [], null),
                new ProviderConfiguration("GoHighLevel", "GoHighLevelCalendar", "Twilio", "SendGrid"),
                new SecretNameConfiguration(
                    "crm",
                    "booking",
                    "vapi",
                    "sid",
                    "token",
                    "email",
                    CrmCredentials: "crm"),
                new CommunicationConfiguration(
                    "+15550001000",
                    "booking@example.com",
                    new ConfirmationTemplateConfiguration("sms"))));
    }

    private sealed class StubSecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(
            string secretName,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                """
                {
                  "accessToken": "token",
                  "locationId": "location-123",
                  "calendarId": "calendar-123",
                  "apiVersion": "2021-07-28"
                }
                """);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode statusCode;

        public RecordingHttpMessageHandler(HttpStatusCode statusCode)
        {
            this.statusCode = statusCode;
        }

        public Uri? RequestUri { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
