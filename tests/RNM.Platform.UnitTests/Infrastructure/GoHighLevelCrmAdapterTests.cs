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
    public async Task FindContactByPhoneOrEmailAsync_ReturnsNormalizedAttributesFromCustomFields()
    {
        var handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {
              "contacts": [
                {
                  "id": "contact-123",
                  "phone": "+13052445176",
                  "email": "lead@example.com",
                  "firstName": "Jane",
                  "lastName": "Seller",
                  "postalCode": "33131",
                  "source": "zillow",
                  "customFields": [
                    { "fieldKey": "contact.intent", "value": "seller" },
                    { "name": "targetPropertyAddress", "value": "123 Market St" },
                    { "key": "assignedAgent", "value": "Alex Agent" }
                  ]
                }
              ]
            }
            """);
        var adapter = CreateAdapter(handler);

        var result = await adapter.FindContactByPhoneOrEmailAsync(
            new CrmContactLookupRequest("tenant-a", "corr-123", "+13052445176", "lead@example.com"),
            CancellationToken.None);

        Assert.True(result.Found);
        Assert.Equal("contact-123", result.ProviderContactId);
        Assert.NotNull(result.Contact);
        Assert.Equal("Jane Seller", result.Contact.Name);
        Assert.Equal("33131", result.Contact.ZipCode);
        Assert.Equal("zillow", result.Contact.Attributes[CrmContactAttributeNames.LeadSource]);
        Assert.Equal("seller", result.Contact.Attributes[CrmContactAttributeNames.Intent]);
        Assert.Equal("123 Market St", result.Contact.Attributes[CrmContactAttributeNames.TargetPropertyAddress]);
        Assert.Equal("Alex Agent", result.Contact.Attributes[CrmContactAttributeNames.AssignedAgent]);
    }

    [Fact]
    public async Task FindContactByPhoneOrEmailAsync_ReturnsEmptyAttributes_WhenGoHighLevelHasNoCustomFields()
    {
        var handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {
              "contact": {
                "id": "contact-123",
                "phone": "+13052445176",
                "email": "lead@example.com"
              }
            }
            """);
        var adapter = CreateAdapter(handler);

        var result = await adapter.FindContactByPhoneOrEmailAsync(
            new CrmContactLookupRequest("tenant-a", "corr-123", "+13052445176", "lead@example.com"),
            CancellationToken.None);

        Assert.True(result.Found);
        Assert.NotNull(result.Contact);
        Assert.Empty(result.Contact.Attributes);
    }

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
        private readonly string responseBody;

        public RecordingHttpMessageHandler(HttpStatusCode statusCode, string responseBody = "{}")
        {
            this.statusCode = statusCode;
            this.responseBody = responseBody;
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
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
