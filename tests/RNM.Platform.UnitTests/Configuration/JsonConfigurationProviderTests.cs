using RNM.Platform.Application.Configuration;
using RNM.Platform.Infrastructure.Configuration;
using Xunit;

namespace RNM.Platform.UnitTests.Configuration;

public sealed class JsonConfigurationProviderTests : IDisposable
{
    private readonly string configRoot;

    public JsonConfigurationProviderTests()
    {
        configRoot = Path.Combine(Path.GetTempPath(), $"rnm-config-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(configRoot, "tenants"));
        Directory.CreateDirectory(Path.Combine(configRoot, "verticals"));
    }

    [Fact]
    public async Task GetTenantConfigurationAsync_LoadsAndValidatesTenantConfiguration()
    {
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "tenants", "tenant-a.json"),
            """
            {
              "tenantId": "tenant-a",
              "verticalId": "vertical-a",
              "businessName": "Tenant A",
              "timeZone": "America/Chicago",
              "serviceArea": {
                "zipCodes": ["75001"],
                "cities": [],
                "referralMessage": null
              },
              "providers": {
                "crmProvider": "GoHighLevel",
                "bookingProvider": "GoHighLevelCalendar",
                "smsProvider": "Twilio",
                "emailProvider": "AzureCommunicationServices"
              },
              "secretNames": {
                "crmApiKey": "tenant-a-crm-api-key",
                "bookingApiKey": "tenant-a-booking-api-key",
                "voiceWebhookSecret": "tenant-a-vapi-webhook-secret",
                "twilioAccountSid": "tenant-a-twilio-account-sid",
                "twilioAuthToken": "tenant-a-twilio-auth-token",
                "emailConnectionString": "tenant-a-email-connection-string"
              },
              "communication": {
                "smsFromPhoneNumber": "+15550001000",
                "emailFromAddress": "booking@example.com",
                "confirmationTemplates": {
                  "smsBodyTemplate": "Configured SMS {{bookingDate}}",
                  "emailSubjectTemplate": "Configured subject {{bookingDate}}",
                  "emailBodyTemplate": "Configured body {{bookingStart}}"
                }
              }
            }
            """);

        var provider = new JsonTenantConfigurationProvider(configRoot, new ConfigurationValidator());

        var configuration = await provider.GetTenantConfigurationAsync("tenant-a", CancellationToken.None);

        Assert.Equal("tenant-a", configuration.TenantId.Value);
        Assert.Equal("vertical-a", configuration.VerticalId.Value);
        Assert.Equal("Tenant A", configuration.BusinessName);
        Assert.Equal("Twilio", configuration.Providers.SmsProvider);
        Assert.Equal("tenant-a-vapi-webhook-secret", configuration.SecretNames.VoiceWebhookSecret);
        Assert.Equal("tenant-a-twilio-auth-token", configuration.SecretNames.TwilioAuthToken);
        Assert.Equal("+15550001000", configuration.Communication.SmsFromPhoneNumber);
        Assert.Equal("Configured SMS {{bookingDate}}", configuration.Communication.ConfirmationTemplates.SmsBodyTemplate);
    }

    [Fact]
    public async Task GetTenantConfigurationAsync_ThrowsConfigurationException_WhenConfigurationIsInvalid()
    {
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "tenants", "tenant-a.json"),
            """
            {
              "tenantId": "tenant-a",
              "verticalId": "vertical-a",
              "businessName": "",
              "timeZone": "America/Chicago",
              "serviceArea": { "zipCodes": [], "cities": [] },
              "providers": {},
              "secretNames": {}
            }
            """);

        var provider = new JsonTenantConfigurationProvider(configRoot, new ConfigurationValidator());

        await Assert.ThrowsAsync<ConfigurationException>(
            () => provider.GetTenantConfigurationAsync("tenant-a", CancellationToken.None));
    }

    [Fact]
    public async Task GetVerticalConfigurationAsync_LoadsAndValidatesVerticalConfiguration()
    {
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "verticals", "vertical-a.json"),
            """
            {
              "verticalId": "vertical-a",
              "displayName": "Vertical A",
              "qualificationFields": ["serviceNeed"],
              "supportedCallTypes": ["GeneralInquiry"]
            }
            """);

        var provider = new JsonVerticalConfigurationProvider(configRoot, new ConfigurationValidator());

        var configuration = await provider.GetVerticalConfigurationAsync("vertical-a", CancellationToken.None);

        Assert.Equal("vertical-a", configuration.VerticalId.Value);
        Assert.Equal("Vertical A", configuration.DisplayName);
        Assert.Contains("serviceNeed", configuration.QualificationFields);
    }

    public void Dispose()
    {
        if (Directory.Exists(configRoot))
        {
            Directory.Delete(configRoot, recursive: true);
        }
    }
}
