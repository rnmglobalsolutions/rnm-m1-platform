using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Configuration;

public sealed class ConfigurationValidator : IConfigurationValidator
{
    private const int MaxSmsTemplateLength = 320;
    private const int MaxEmailSubjectTemplateLength = 120;
    private const int MaxEmailBodyTemplateLength = 2000;

    private static readonly HashSet<string> AllowedConfirmationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "tenantId",
        "verticalId",
        "serviceType",
        "bookingStart",
        "bookingEnd",
        "bookingDate",
        "bookingTime"
    };

    public ConfigurationValidationResult ValidateTenant(TenantConfiguration tenantConfiguration)
    {
        var errors = new List<string>();

        AddRequired(errors, tenantConfiguration.TenantId.Value, "tenantId");
        AddRequired(errors, tenantConfiguration.VerticalId.Value, "verticalId");
        AddRequired(errors, tenantConfiguration.BusinessName, "businessName");
        AddRequired(errors, tenantConfiguration.TimeZone, "timeZone");

        if (!string.IsNullOrWhiteSpace(tenantConfiguration.TimeZone))
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(tenantConfiguration.TimeZone);
            }
            catch (TimeZoneNotFoundException)
            {
                errors.Add("timeZone must be a valid IANA or system time zone identifier.");
            }
            catch (InvalidTimeZoneException)
            {
                errors.Add("timeZone must be a valid IANA or system time zone identifier.");
            }
        }

        if (tenantConfiguration.ServiceArea.ZipCodes.Count == 0 && tenantConfiguration.ServiceArea.Cities.Count == 0)
        {
            errors.Add("serviceArea must include at least one zip code or city.");
        }

        AddRequired(errors, tenantConfiguration.Providers.CrmProvider, "providers.crmProvider");
        AddRequired(errors, tenantConfiguration.Providers.BookingProvider, "providers.bookingProvider");
        AddRequired(errors, tenantConfiguration.Providers.SmsProvider, "providers.smsProvider");
        AddRequired(errors, tenantConfiguration.Providers.EmailProvider, "providers.emailProvider");

        AddRequired(errors, tenantConfiguration.SecretNames.CrmApiKey, "secretNames.crmApiKey");
        AddRequired(errors, tenantConfiguration.SecretNames.BookingApiKey, "secretNames.bookingApiKey");
        AddRequired(errors, tenantConfiguration.SecretNames.VoiceWebhookSecret, "secretNames.voiceWebhookSecret");
        AddRequired(errors, tenantConfiguration.SecretNames.TwilioAccountSid, "secretNames.twilioAccountSid");
        AddRequired(errors, tenantConfiguration.SecretNames.TwilioAuthToken, "secretNames.twilioAuthToken");
        AddRequired(errors, tenantConfiguration.SecretNames.EmailConnectionString, "secretNames.emailConnectionString");

        AddRequired(errors, tenantConfiguration.Communication.SmsFromPhoneNumber, "communication.smsFromPhoneNumber");
        AddRequired(errors, tenantConfiguration.Communication.ConfirmationTemplates.SmsBodyTemplate, "communication.confirmationTemplates.smsBodyTemplate");
        ValidateConfirmationTemplate(
            errors,
            tenantConfiguration.Communication.ConfirmationTemplates.SmsBodyTemplate,
            "communication.confirmationTemplates.smsBodyTemplate",
            MaxSmsTemplateLength);

        var emailSubjectTemplate = tenantConfiguration.Communication.ConfirmationTemplates.EmailSubjectTemplate;
        var emailBodyTemplate = tenantConfiguration.Communication.ConfirmationTemplates.EmailBodyTemplate;
        if (!string.IsNullOrWhiteSpace(emailSubjectTemplate) || !string.IsNullOrWhiteSpace(emailBodyTemplate))
        {
            AddRequired(errors, emailSubjectTemplate, "communication.confirmationTemplates.emailSubjectTemplate");
            AddRequired(errors, emailBodyTemplate, "communication.confirmationTemplates.emailBodyTemplate");
            ValidateConfirmationTemplate(
                errors,
                emailSubjectTemplate,
                "communication.confirmationTemplates.emailSubjectTemplate",
                MaxEmailSubjectTemplateLength);
            ValidateConfirmationTemplate(
                errors,
                emailBodyTemplate,
                "communication.confirmationTemplates.emailBodyTemplate",
                MaxEmailBodyTemplateLength);
        }

        return errors.Count == 0 ? ConfigurationValidationResult.Valid : new ConfigurationValidationResult(errors);
    }

    public ConfigurationValidationResult ValidateVertical(VerticalConfiguration verticalConfiguration)
    {
        var errors = new List<string>();

        AddRequired(errors, verticalConfiguration.VerticalId.Value, "verticalId");
        AddRequired(errors, verticalConfiguration.DisplayName, "displayName");

        if (verticalConfiguration.QualificationFields.Count == 0)
        {
            errors.Add("qualificationFields must include at least one field.");
        }

        if (verticalConfiguration.SupportedCallTypes.Count == 0)
        {
            errors.Add("supportedCallTypes must include at least one call type.");
        }

        if (verticalConfiguration.ServiceAreaFieldAliases.ZipCodeFields.Count == 0)
        {
            errors.Add("serviceAreaFieldAliases.zipCodeFields must include at least one field.");
        }

        if (verticalConfiguration.ServiceAreaFieldAliases.AddressFields.Count == 0)
        {
            errors.Add("serviceAreaFieldAliases.addressFields must include at least one field.");
        }

        return errors.Count == 0 ? ConfigurationValidationResult.Valid : new ConfigurationValidationResult(errors);
    }

    private static void AddRequired(ICollection<string> errors, string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{fieldName} is required.");
        }
    }

    private static void ValidateConfirmationTemplate(
        ICollection<string> errors,
        string? template,
        string fieldName,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return;
        }

        if (template.Length > maxLength)
        {
            errors.Add($"{fieldName} must be {maxLength} characters or fewer.");
        }

        var searchIndex = 0;
        while (searchIndex < template.Length)
        {
            var tokenStart = template.IndexOf("{{", searchIndex, StringComparison.Ordinal);
            if (tokenStart < 0)
            {
                return;
            }

            var tokenEnd = template.IndexOf("}}", tokenStart + 2, StringComparison.Ordinal);
            if (tokenEnd < 0)
            {
                errors.Add($"{fieldName} contains an unterminated template token.");
                return;
            }

            var token = template[(tokenStart + 2)..tokenEnd].Trim();
            if (!AllowedConfirmationTokens.Contains(token))
            {
                errors.Add($"{fieldName} contains unsupported template token '{token}'.");
            }

            searchIndex = tokenEnd + 2;
        }
    }
}
