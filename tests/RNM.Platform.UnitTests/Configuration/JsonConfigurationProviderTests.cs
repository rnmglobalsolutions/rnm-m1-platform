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
                "crmApiKey": "rnm-tenant-a-crm-api-key",
                "bookingApiKey": "rnm-tenant-a-booking-api-key",
                "voiceWebhookSecret": "rnm-tenant-a-vapi-webhook-secret",
                "twilioAccountSid": "rnm-tenant-a-twilio-account-sid",
                "twilioAuthToken": "rnm-tenant-a-twilio-auth-token",
                "emailConnectionString": "rnm-tenant-a-email-connection-string"
              },
              "communication": {
                "smsFromPhoneNumber": "+15550001000",
                "emailFromAddress": "booking@example.com",
                "confirmationTemplates": {
                  "smsBodyTemplate": "Configured SMS {{bookingDate}}",
                  "emailSubjectTemplate": "Configured subject {{bookingDate}}",
                  "emailBodyTemplate": "Configured body {{bookingStart}}"
                },
                "appointmentReminders": {
                  "templates": null,
                  "reminderOffsetsMinutes": null,
                  "reminderStalenessCutoffMinutes": null
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
        Assert.Equal("rnm-tenant-a-vapi-webhook-secret", configuration.SecretNames.VoiceWebhookSecret);
        Assert.Equal("rnm-tenant-a-twilio-auth-token", configuration.SecretNames.TwilioAuthToken);
        Assert.Equal("+15550001000", configuration.Communication.SmsFromPhoneNumber);
        Assert.Equal("Configured SMS {{bookingDate}}", configuration.Communication.ConfirmationTemplates.SmsBodyTemplate);
    }

    [Fact]
    public async Task GetTenantConfigurationAsync_RejectsAppointmentRemindersWithOmittedFields()
    {
        var json = CreateTenantJson("tenant-a", "[\"75001\"]")
            .Replace("\"templates\": null,\n", string.Empty, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "tenants", "tenant-a.json"),
            json);
        var provider = new JsonTenantConfigurationProvider(configRoot, new ConfigurationValidator());

        var exception = await Assert.ThrowsAsync<ConfigurationException>(
            () => provider.GetTenantConfigurationAsync("tenant-a", CancellationToken.None));

        Assert.Contains("required schema", exception.Message, StringComparison.Ordinal);
        Assert.Contains("templates", exception.Message, StringComparison.OrdinalIgnoreCase);
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
                "crmApiKey": "rnm-tenant-a-crm",
                "bookingApiKey": "rnm-tenant-a-booking",
                "voiceWebhookSecret": "rnm-tenant-a-voice",
                "twilioAccountSid": "rnm-tenant-a-sid",
                "twilioAuthToken": "rnm-tenant-a-token",
                "emailConnectionString": "rnm-tenant-a-email",
                "manyChatWebhookSecret": "rnm-tenant-a-manychat-secret"
              },
              "communication": {
                "smsFromPhoneNumber": "+15550001000",
                "confirmationTemplates": { "smsBodyTemplate": "Received" },
                "appointmentReminders": {
                  "templates": null,
                  "reminderOffsetsMinutes": null,
                  "reminderStalenessCutoffMinutes": null
                }
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

        Assert.Equal("rnm-tenant-a-manychat-secret", configuration.SecretNames.ManyChatWebhookSecret);
        Assert.True(configuration.Integrations?.ManyChat?.EffectiveEnabled);
        Assert.False(configuration.Integrations?.ManyChat?.EffectiveScheduleFollowUp);
        Assert.Equal(75, configuration.Integrations?.ManyChat?.EffectiveMaxRequestsPerMinute);
        Assert.Equal("Book consultation", configuration.Integrations?.ManyChat?.RoutingActions?.For("consultation")?.Label);
        Assert.Equal("https://example.com/class", configuration.Integrations?.ManyChat?.RoutingActions?.For("master_class")?.Url);
        Assert.Equal("message", configuration.Integrations?.ManyChat?.RoutingActions?.For("follow_up")?.Type);
        Assert.Equal("none", configuration.Integrations?.ManyChat?.RoutingActions?.For("none")?.Type);
    }

    [Fact]
    public async Task GetTenantConfigurationAsync_LoadsRoutingActionForAnyRouteToken()
    {
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "tenants", "tenant-a.json"),
            ManyChatTenantJson("""
                  "routingActions": {
                    "nurture": { "type": "message", "message": "We will stay in touch." },
                    "master_class": { "type": "link", "url": "https://example.com/class" }
                  }
            """));
        var provider = new JsonTenantConfigurationProvider(configRoot, new ConfigurationValidator());

        var configuration = await provider.GetTenantConfigurationAsync("tenant-a", CancellationToken.None);

        Assert.Equal("We will stay in touch.", configuration.Integrations?.ManyChat?.RoutingActions?.For("nurture")?.Message);
        Assert.Equal("https://example.com/class", configuration.Integrations?.ManyChat?.RoutingActions?.For("master_class")?.Url);
    }

    [Fact]
    public async Task GetTenantConfigurationAsync_RejectsLegacyAndCanonicalKeyForSameRoute()
    {
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "tenants", "tenant-a.json"),
            ManyChatTenantJson("""
                  "routingActions": {
                    "masterClass": { "type": "link", "url": "https://example.com/a" },
                    "master_class": { "type": "link", "url": "https://example.com/b" }
                  }
            """));
        var provider = new JsonTenantConfigurationProvider(configRoot, new ConfigurationValidator());

        var exception = await Assert.ThrowsAsync<ConfigurationException>(
            () => provider.GetTenantConfigurationAsync("tenant-a", CancellationToken.None));

        Assert.Contains("'master_class' more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTenantConfigurationAsync_RejectsRoutingActionWithInvalidRouteKey()
    {
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "tenants", "tenant-a.json"),
            ManyChatTenantJson("""
                  "routingActions": {
                    "Book Now": { "type": "message", "message": "Hi" }
                  }
            """));
        var provider = new JsonTenantConfigurationProvider(configRoot, new ConfigurationValidator());

        var exception = await Assert.ThrowsAsync<ConfigurationException>(
            () => provider.GetTenantConfigurationAsync("tenant-a", CancellationToken.None));

        Assert.Contains("routingActions.Book Now must be keyed by a lowercase route token", exception.Message, StringComparison.Ordinal);
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
                "crmApiKey": "rnm-tenant-a-crm",
                "bookingApiKey": "rnm-tenant-a-booking",
                "voiceWebhookSecret": "rnm-tenant-a-vapi",
                "twilioAccountSid": "rnm-tenant-a-sid",
                "twilioAuthToken": "rnm-tenant-a-token",
                "emailConnectionString": "rnm-tenant-a-email"
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
                },
                "appointmentReminders": {
                  "templates": null,
                  "reminderOffsetsMinutes": null,
                  "reminderStalenessCutoffMinutes": null
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
                "crmApiKey": "rnm-tenant-a-crm",
                "bookingApiKey": "rnm-tenant-a-booking",
                "voiceWebhookSecret": "rnm-tenant-a-vapi",
                "twilioAccountSid": "rnm-tenant-a-sid",
                "twilioAuthToken": "rnm-tenant-a-token",
                "emailConnectionString": "rnm-tenant-a-email"
              },
              "communication": {
                "smsFromPhoneNumber": "+15550001000",
                "businessNotificationPhoneNumber": "+15557654321",
                "notifyBusinessBySmsForUrgentOnly": true,
                "confirmationTemplates": {
                  "smsBodyTemplate": "Configured SMS {{bookingDate}}",
                  "businessSmsBodyTemplate": "Business SMS {{urgency}}"
                },
                "appointmentReminders": {
                  "templates": null,
                  "reminderOffsetsMinutes": null,
                  "reminderStalenessCutoffMinutes": null
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

    private static string ManyChatTenantJson(string routingActions) =>
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
                "crmApiKey": "rnm-tenant-a-crm",
                "bookingApiKey": "rnm-tenant-a-booking",
                "voiceWebhookSecret": "rnm-tenant-a-voice",
                "twilioAccountSid": "rnm-tenant-a-sid",
                "twilioAuthToken": "rnm-tenant-a-token",
                "emailConnectionString": "rnm-tenant-a-email",
                "manyChatWebhookSecret": "rnm-tenant-a-manychat-secret"
              },
              "communication": {
                "smsFromPhoneNumber": "+15550001000",
                "confirmationTemplates": { "smsBodyTemplate": "Received" },
                "appointmentReminders": {
                  "templates": null,
                  "reminderOffsetsMinutes": null,
                  "reminderStalenessCutoffMinutes": null
                }
              },
              "integrations": {
                "manyChat": {
                  "enabled": true,
                  "scheduleFollowUp": false,
                  "maxRequestsPerMinute": 75,
                  {routingActions}
                }
              }
            }
            """.Replace("{routingActions}", routingActions, StringComparison.Ordinal);

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
            "crmApiKey": "rnm-tenant-a-crm",
            "bookingApiKey": "rnm-tenant-a-booking",
            "voiceWebhookSecret": "rnm-tenant-a-vapi",
            "twilioAccountSid": "rnm-tenant-a-sid",
            "twilioAuthToken": "rnm-tenant-a-token",
            "emailConnectionString": "rnm-tenant-a-email"
          },
          "communication": {
            "smsFromPhoneNumber": "+15550001000",
            "emailFromAddress": "booking@example.com",
            "confirmationTemplates": {
              "smsBodyTemplate": "Booked {{bookingDateToken}}"
            },
            "appointmentReminders": {
              "templates": null,
              "reminderOffsetsMinutes": null,
              "reminderStalenessCutoffMinutes": null
            }
          }
        }
        """;
    }
}
