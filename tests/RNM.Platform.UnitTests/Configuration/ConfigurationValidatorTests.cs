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
    public void ValidateTenant_ReturnsErrors_WhenConfirmationTemplateUsesUnsupportedToken()
    {
        var validator = new ConfigurationValidator();
        var configuration = CreateValidTenantConfiguration() with
        {
            Communication = CreateValidTenantConfiguration().Communication with
            {
                ConfirmationTemplates = CreateValidTenantConfiguration().Communication.ConfirmationTemplates with
                {
                    SmsBodyTemplate = "Booked for {{customerPhoneNumber}}"
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
                "crm-api-key",
                "booking-api-key",
                "vapi-webhook-secret",
                "twilio-account-sid",
                "twilio-auth-token",
                "email-connection-string"),
            new CommunicationConfiguration(
                "+15550001000",
                "booking@example.com",
                new ConfirmationTemplateConfiguration(
                    "SMS template {{bookingDate}}",
                    "Email subject {{bookingDate}}",
                    "Email body {{bookingStart}}")));
    }
}
