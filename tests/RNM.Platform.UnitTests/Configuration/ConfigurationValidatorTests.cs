using RNM.Platform.Application.Configuration;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.Configuration;

public sealed class ConfigurationValidatorTests
{
    [Fact]
    public void ValidateTenant_ReturnsValid_WhenRequiredFieldsArePresent()
    {
        var validator = new ConfigurationValidator();

        var result = validator.ValidateTenant(CreateValidTenantConfiguration());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateTenant_ReturnsErrors_WhenRequiredFieldsAreMissing()
    {
        var validator = new ConfigurationValidator();
        var configuration = CreateValidTenantConfiguration() with
        {
            BusinessName = "",
            ServiceArea = new ServiceAreaConfiguration([], [], null)
        };

        var result = validator.ValidateTenant(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("businessName", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("serviceArea", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTenant_ReturnsErrors_WhenAppointmentRemindersAreNotConfigured()
    {
        var validator = new ConfigurationValidator();
        var validConfiguration = CreateValidTenantConfiguration();
        var configuration = validConfiguration with
        {
            Communication = validConfiguration.Communication with
            {
                AppointmentReminders = null
            }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("communication.appointmentReminders", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTenant_ReturnsValid_WhenAppointmentRemindersAreExplicitlyDisabled()
    {
        var validator = new ConfigurationValidator();
        var validConfiguration = CreateValidTenantConfiguration();
        var configuration = validConfiguration with
        {
            Communication = validConfiguration.Communication with
            {
                AppointmentReminders = new AppointmentReminderConfiguration(null, null, null)
            }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateTenant_ReturnsErrors_WhenAppointmentRemindersArePartiallyConfigured()
    {
        var validator = new ConfigurationValidator();
        var validConfiguration = CreateValidTenantConfiguration();
        var configuration = validConfiguration with
        {
            Communication = validConfiguration.Communication with
            {
                AppointmentReminders = new AppointmentReminderConfiguration(
                    Templates: null,
                    ReminderOffsetsMinutes: [1440, 60],
                    ReminderStalenessCutoffMinutes: null)
            }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("appointmentReminders.templates", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("reminderStalenessCutoffMinutes", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTenant_ReturnsErrors_WhenConfirmationTemplateUsesUnsupportedToken()
    {
        var validator = new ConfigurationValidator();
        var configuration = CreateValidTenantConfiguration() with
        {
            Communication = CreateValidTenantConfiguration().Communication with
            {
                ConfirmationTemplates = CreateValidTenantConfiguration().Communication.ConfirmationTemplates with
                {
                    SmsBodyTemplate = "Booked for {{unsupportedToken}}"
                }
            }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("unsupported template token", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTenant_AllowsDynamicAttributeTemplateTokens()
    {
        var validator = new ConfigurationValidator();
        var validConfiguration = CreateValidTenantConfiguration();
        var configuration = validConfiguration with
        {
            Communication = validConfiguration.Communication with
            {
                ConfirmationTemplates = validConfiguration.Communication.ConfirmationTemplates with
                {
                    SmsBodyTemplate = "Lead {{attr.intent}} {{attr.targetPropertyAddress}}"
                }
            }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateTenant_AllowsCampaignIdInConfirmationTemplates()
    {
        var validator = new ConfigurationValidator();
        var validConfiguration = CreateValidTenantConfiguration();
        var configuration = validConfiguration with
        {
            Communication = validConfiguration.Communication with
            {
                BusinessNotificationEmail = "office@example.com",
                ConfirmationTemplates = validConfiguration.Communication.ConfirmationTemplates with
                {
                    BusinessEmailSubjectTemplate = "New lead {{campaignId}}",
                    BusinessEmailBodyTemplate = "Campaign: {{campaignId}}"
                }
            }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateTenant_ReturnsErrors_WhenDynamicAttributeTokenIsInvalid()
    {
        var validator = new ConfigurationValidator();
        var validConfiguration = CreateValidTenantConfiguration();
        var configuration = validConfiguration with
        {
            Communication = validConfiguration.Communication with
            {
                ConfirmationTemplates = validConfiguration.Communication.ConfirmationTemplates with
                {
                    SmsBodyTemplate = "Lead {{attr.intent value}}"
                }
            }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("unsupported template token", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTenant_ReturnsErrors_WhenSmsConfirmationTemplateIsTooLong()
    {
        var validator = new ConfigurationValidator();
        var configuration = CreateValidTenantConfiguration() with
        {
            Communication = CreateValidTenantConfiguration().Communication with
            {
                ConfirmationTemplates = CreateValidTenantConfiguration().Communication.ConfirmationTemplates with
                {
                    SmsBodyTemplate = new string('x', 321)
                }
            }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("320 characters or fewer", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTenant_ReturnsErrors_WhenOnlyOneEmailConfirmationTemplateIsConfigured()
    {
        var validator = new ConfigurationValidator();
        var configuration = CreateValidTenantConfiguration() with
        {
            Communication = CreateValidTenantConfiguration().Communication with
            {
                ConfirmationTemplates = CreateValidTenantConfiguration().Communication.ConfirmationTemplates with
                {
                    EmailSubjectTemplate = "Appointment confirmed",
                    EmailBodyTemplate = null
                }
            }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("emailBodyTemplate", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTenant_ReturnsErrors_WhenBusinessEmailRecipientConfiguredWithoutBusinessTemplates()
    {
        var validator = new ConfigurationValidator();
        var configuration = CreateValidTenantConfiguration() with
        {
            Communication = CreateValidTenantConfiguration().Communication with
            {
                BusinessNotificationEmail = "office@example.com"
            }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("businessEmailSubjectTemplate", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("businessEmailBodyTemplate", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTenant_ReturnsValid_WhenBusinessSmsNotificationIsConditional()
    {
        var validator = new ConfigurationValidator();
        var validConfiguration = CreateValidTenantConfiguration();
        var configuration = validConfiguration with
        {
            Communication = validConfiguration.Communication with
            {
                BusinessSmsNotification = BusinessSmsNotificationConfiguration.Conditional(
                    "leadStatus",
                    ["qualified", "reactivated"])
            }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("tenant-a-twilio-auth-token", "must start with 'rnm-tenant-'")]
    [InlineData("rnm-tenant-a_twilio_token", "must be a valid Key Vault secret name")]
    public void ValidateTenant_ReturnsErrors_WhenSecretNameBreaksTheNamingRule(string secretName, string expectedError)
    {
        var validator = new ConfigurationValidator();
        var configuration = CreateValidTenantConfiguration() with
        {
            SecretNames = CreateValidTenantConfiguration().SecretNames with { TwilioAuthToken = secretName }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.StartsWith("secretNames.twilioAuthToken", StringComparison.Ordinal)
            && error.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTenant_ChecksOptionalSecretNamesWhenConfigured()
    {
        var validator = new ConfigurationValidator();
        var configuration = CreateValidTenantConfiguration() with
        {
            SecretNames = CreateValidTenantConfiguration().SecretNames with { ManyChatWebhookSecret = "manychat-secret" }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.Contains(result.Errors, error =>
            error == "secretNames.manyChatWebhookSecret must start with 'rnm-tenant-'.");
    }

    [Fact]
    public void ValidateTenant_ReturnsErrors_WhenManyChatRoutingUrlIsInvalid()
    {
        var validator = new ConfigurationValidator();
        var configuration = CreateValidTenantConfiguration() with
        {
            SecretNames = CreateValidTenantConfiguration().SecretNames with
            {
                ManyChatWebhookSecret = "rnm-tenant-a-manychat-secret"
            },
            Integrations = new IntegrationConfiguration(
                new ManyChatIntegrationConfiguration(
                    Enabled: true,
                    RoutingActions: new ManyChatRoutingActionsConfiguration(
                        new Dictionary<string, ManyChatRoutingActionConfiguration>
                        {
                            ["consultation"] = new(
                                "link",
                                "Book",
                                "not-a-url",
                                "Book a consultation.")
                        })))
        };

        var result = validator.ValidateTenant(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("routingActions.consultation.url", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTenant_ReturnsErrors_WhenConditionalBusinessSmsNotificationHasNoCondition()
    {
        var validator = new ConfigurationValidator();
        var validConfiguration = CreateValidTenantConfiguration();
        var configuration = validConfiguration with
        {
            Communication = validConfiguration.Communication with
            {
                BusinessSmsNotification = new BusinessSmsNotificationConfiguration(
                    BusinessSmsNotificationConfiguration.ConditionalMode)
            }
        };

        var result = validator.ValidateTenant(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("businessSmsNotification.condition", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateVertical_ReturnsValid_WhenRequiredFieldsArePresent()
    {
        var validator = new ConfigurationValidator();
        var configuration = new VerticalConfiguration(
            new VerticalId("any-vertical"),
            "Any Vertical",
            ["serviceNeed"],
            ["GeneralInquiry"],
            ServiceAreaFieldAliasConfiguration.Defaults());

        var result = validator.ValidateVertical(configuration);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateVertical_ReturnsErrors_WhenCollectionsAreEmpty()
    {
        var validator = new ConfigurationValidator();
        var configuration = new VerticalConfiguration(
            new VerticalId("any-vertical"),
            "Any Vertical",
            [],
            [],
            ServiceAreaFieldAliasConfiguration.Defaults());

        var result = validator.ValidateVertical(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("qualificationFields", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("supportedCallTypes", StringComparison.Ordinal));
    }

    private static TenantConfiguration CreateValidTenantConfiguration()
    {
        return new TenantConfiguration(
            new TenantId("tenant-a"),
            new VerticalId("vertical-a"),
            "Tenant A",
            "America/Chicago",
            new ServiceAreaConfiguration(["75001"], [], null),
            new ProviderConfiguration("Crm", "Booking", "Sms", "Email"),
            new SecretNameConfiguration(
                "rnm-tenant-a-crm-api-key",
                "rnm-tenant-a-booking-api-key",
                "rnm-tenant-a-vapi-webhook-secret",
                "rnm-tenant-a-twilio-account-sid",
                "rnm-tenant-a-twilio-auth-token",
                "rnm-tenant-a-email-connection-string"),
            new CommunicationConfiguration(
                "+15550001000",
                "booking@example.com",
                new ConfirmationTemplateConfiguration(
                    "SMS template {{bookingDate}}",
                    "Email subject {{bookingDate}}",
                    "Email body {{bookingStart}}"),
                AppointmentReminders: new AppointmentReminderConfiguration(null, null, null)));
    }
}
