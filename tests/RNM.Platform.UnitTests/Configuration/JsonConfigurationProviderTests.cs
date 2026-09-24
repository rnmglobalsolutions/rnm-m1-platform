using RNM.Platform.Application.Configuration;
using RNM.Platform.Domain.Configuration;
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
                "emailProvider": "SendGrid"
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
    public async Task GetTenantConfigurationAsync_LoadsManyChatIntegrationAndSecret()
    {
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "tenants", "tenant-a.json"),
            """
            {
              "tenantId": "tenant-a",
              "verticalId": "vertical-a",
              "businessName": "Tenant A",
              "timeZone": "America/Chicago",
              "serviceArea": { "zipCodes": ["75001"], "cities": [] },
              "providers": {
                "crmProvider": "AzureTable",
                "bookingProvider": "GoogleCalendar",
                "smsProvider": "Twilio",
                "emailProvider": "SendGrid"
              },
              "secretNames": {
                "crmApiKey": "crm",
                "bookingApiKey": "booking",
                "voiceWebhookSecret": "voice",
                "twilioAccountSid": "sid",
                "twilioAuthToken": "token",
                "emailConnectionString": "email",
                "manyChatWebhookSecret": "tenant-a-manychat-secret"
              },
              "communication": {
                "smsFromPhoneNumber": "+15550001000",
                "confirmationTemplates": { "smsBodyTemplate": "Received" }
              },
              "integrations": {
                "manyChat": {
                  "enabled": true,
                  "scheduleFollowUp": false,
                  "maxRequestsPerMinute": 75,
                  "routingActions": {
                    "consultation": {
                      "type": "link",
                      "label": "Book consultation",
                      "url": "https://example.com/book",
                      "message": "Book a 1:1 consultation."
                    },
                    "masterClass": {
                      "type": "link",
                      "label": "Join class",
                      "url": "https://example.com/class"
                    },
                    "followUp": {
                      "type": "message",
                      "message": "We will follow up."
                    },
                    "none": {
                      "type": "none",
                      "message": "No routing."
                    }
                  }
                }
              }
            }
            """);

        var provider = new JsonTenantConfigurationProvider(configRoot, new ConfigurationValidator());

        var configuration = await provider.GetTenantConfigurationAsync("tenant-a", CancellationToken.None);

        Assert.Equal("tenant-a-manychat-secret", configuration.SecretNames.ManyChatWebhookSecret);
        Assert.True(configuration.Integrations?.ManyChat?.EffectiveEnabled);
        Assert.False(configuration.Integrations?.ManyChat?.EffectiveScheduleFollowUp);
        Assert.Equal(75, configuration.Integrations?.ManyChat?.EffectiveMaxRequestsPerMinute);
        Assert.Equal("Book consultation", configuration.Integrations?.ManyChat?.RoutingActions?.Consultation?.Label);
        Assert.Equal("https://example.com/class", configuration.Integrations?.ManyChat?.RoutingActions?.MasterClass?.Url);
        Assert.Equal("message", configuration.Integrations?.ManyChat?.RoutingActions?.FollowUp?.Type);
        Assert.Equal("none", configuration.Integrations?.ManyChat?.RoutingActions?.None?.Type);
    }

    [Fact]
    public async Task GetTenantConfigurationAsync_LoadsBusinessSmsNotificationRule()
    {
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "tenants", "tenant-a.json"),
            """
            {
              "tenantId": "tenant-a",
              "verticalId": "vertical-a",
              "businessName": "Tenant A",
              "timeZone": "America/Chicago",
              "serviceArea": { "zipCodes": ["75001"], "cities": [] },
              "providers": {
                "crmProvider": "GoHighLevel",
                "bookingProvider": "GoHighLevelCalendar",
                "smsProvider": "Twilio",
                "emailProvider": "SendGrid"
              },
              "secretNames": {
                "crmApiKey": "crm",
                "bookingApiKey": "booking",
                "voiceWebhookSecret": "vapi",
                "twilioAccountSid": "sid",
                "twilioAuthToken": "token",
                "emailConnectionString": "email"
              },
              "communication": {
                "smsFromPhoneNumber": "+15550001000",
                "businessNotificationPhoneNumber": "+15557654321",
                "businessSmsNotification": {
                  "mode": "conditional",
                  "condition": {
                    "attribute": "leadStatus",
                    "equalsAny": ["qualified", "reactivated"]
                  }
                },
                "confirmationTemplates": {
                  "smsBodyTemplate": "Configured SMS {{attr.intent}}",
                  "businessSmsBodyTemplate": "Business SMS {{attr.leadStatus}}"
                }
              }
            }
            """);

        var provider = new JsonTenantConfigurationProvider(configRoot, new ConfigurationValidator());

        var configuration = await provider.GetTenantConfigurationAsync("tenant-a", CancellationToken.None);

        Assert.Equal(BusinessSmsNotificationConfiguration.ConditionalMode, configuration.Communication.BusinessSmsNotification?.Mode);
        Assert.Equal("leadStatus", configuration.Communication.BusinessSmsNotification?.Condition?.Attribute);
        Assert.Equal(["qualified", "reactivated"], configuration.Communication.BusinessSmsNotification?.Condition?.EqualsAny);
    }

    [Fact]
    public async Task GetTenantConfigurationAsync_MapsLegacyUrgentOnlyBusinessSmsFlagToConditionalRule()
    {
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "tenants", "tenant-a.json"),
            """
            {
              "tenantId": "tenant-a",
              "verticalId": "vertical-a",
              "businessName": "Tenant A",
              "timeZone": "America/Chicago",
              "serviceArea": { "zipCodes": ["75001"], "cities": [] },
              "providers": {
                "crmProvider": "GoHighLevel",
                "bookingProvider": "GoHighLevelCalendar",
                "smsProvider": "Twilio",
                "emailProvider": "SendGrid"
              },
              "secretNames": {
                "crmApiKey": "crm",
                "bookingApiKey": "booking",
                "voiceWebhookSecret": "vapi",
                "twilioAccountSid": "sid",
                "twilioAuthToken": "token",
                "emailConnectionString": "email"
              },
              "communication": {
                "smsFromPhoneNumber": "+15550001000",
                "businessNotificationPhoneNumber": "+15557654321",
                "notifyBusinessBySmsForUrgentOnly": true,
                "confirmationTemplates": {
                  "smsBodyTemplate": "Configured SMS {{bookingDate}}",
                  "businessSmsBodyTemplate": "Business SMS {{urgency}}"
                }
              }
            }
            """);

        var provider = new JsonTenantConfigurationProvider(configRoot, new ConfigurationValidator());

        var configuration = await provider.GetTenantConfigurationAsync("tenant-a", CancellationToken.None);

        Assert.Equal(BusinessSmsNotificationConfiguration.ConditionalMode, configuration.Communication.BusinessSmsNotification?.Mode);
        Assert.Equal("urgency", configuration.Communication.BusinessSmsNotification?.Condition?.Attribute);
        Assert.Contains("urgent", configuration.Communication.BusinessSmsNotification?.Condition?.EqualsAny ?? []);
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
    public async Task GetTenantConfigurationAsync_RejectsMismatchedTenantId()
    {
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "tenants", "tenant-a.json"),
            CreateTenantJson("tenant-b", "[\"75001\"]"));
        var provider = new JsonTenantConfigurationProvider(configRoot, new ConfigurationValidator());

        await Assert.ThrowsAsync<ConfigurationException>(
            () => provider.GetTenantConfigurationAsync("tenant-a", CancellationToken.None));
    }

    [Fact]
    public async Task GetTenantConfigurationAsync_RejectsWildcardServiceArea_WhenDisabled()
    {
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "tenants", "tenant-a.json"),
            CreateTenantJson("tenant-a", "[\"*\"]"));
        var provider = new JsonTenantConfigurationProvider(
            configRoot,
            new ConfigurationValidator(),
            allowWildcardServiceArea: false);

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

    [Fact]
    public async Task GetVerticalConfigurationAsync_RejectsMismatchedVerticalId()
    {
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "verticals", "vertical-a.json"),
            """
            {
              "verticalId": "vertical-b",
              "displayName": "Vertical B",
              "qualificationFields": ["serviceNeed"],
              "supportedCallTypes": ["GeneralInquiry"]
            }
            """);
        var provider = new JsonVerticalConfigurationProvider(configRoot, new ConfigurationValidator());

        await Assert.ThrowsAsync<ConfigurationException>(
            () => provider.GetVerticalConfigurationAsync("vertical-a", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(configRoot))
        {
            Directory.Delete(configRoot, recursive: true);
        }
    }

    private static string CreateTenantJson(string tenantId, string zipCodes)
    {
        const string bookingDateToken = "{{bookingDate}}";

        return $$"""
        {
          "tenantId": "{{tenantId}}",
          "verticalId": "vertical-a",
          "businessName": "Tenant A",
          "timeZone": "America/Chicago",
          "serviceArea": {
            "zipCodes": {{zipCodes}},
            "cities": []
          },
          "providers": {
            "crmProvider": "GoHighLevel",
            "bookingProvider": "GoHighLevelCalendar",
            "smsProvider": "Twilio",
            "emailProvider": "SendGrid"
          },
          "secretNames": {
            "crmApiKey": "crm",
            "bookingApiKey": "booking",
            "voiceWebhookSecret": "vapi",
            "twilioAccountSid": "sid",
            "twilioAuthToken": "token",
            "emailConnectionString": "email"
          },
          "communication": {
            "smsFromPhoneNumber": "+15550001000",
            "emailFromAddress": "booking@example.com",
            "confirmationTemplates": {
              "smsBodyTemplate": "Booked {{bookingDateToken}}"
            }
          }
        }
        """;
    }
}
