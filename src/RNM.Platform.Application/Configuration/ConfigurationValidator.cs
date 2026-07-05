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
        "correlationId",
        "customerName",
        "customerPhoneNumber",
        "customerEmail",
        "serviceType",
        "propertyType",
        "serviceAddress",
        "zipCode",
        "urgency",
        "providerBookingId",
        "bookingLabel",
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

        var businessSmsTemplate = tenantConfiguration.Communication.ConfirmationTemplates.BusinessSmsBodyTemplate;
        if (!string.IsNullOrWhiteSpace(tenantConfiguration.Communication.BusinessNotificationPhoneNumber))
        {
            AddRequired(errors, businessSmsTemplate, "communication.confirmationTemplates.businessSmsBodyTemplate");
        }

        ValidateConfirmationTemplate(
            errors,
            businessSmsTemplate,
            "communication.confirmationTemplates.businessSmsBodyTemplate",
            MaxSmsTemplateLength);

        var businessEmailSubjectTemplate = tenantConfiguration.Communication.ConfirmationTemplates.BusinessEmailSubjectTemplate;
        var businessEmailBodyTemplate = tenantConfiguration.Communication.ConfirmationTemplates.BusinessEmailBodyTemplate;
        if (!string.IsNullOrWhiteSpace(tenantConfiguration.Communication.BusinessNotificationEmail)
            || !string.IsNullOrWhiteSpace(businessEmailSubjectTemplate)
            || !string.IsNullOrWhiteSpace(businessEmailBodyTemplate))
        {
            AddRequired(errors, businessEmailSubjectTemplate, "communication.confirmationTemplates.businessEmailSubjectTemplate");
            AddRequired(errors, businessEmailBodyTemplate, "communication.confirmationTemplates.businessEmailBodyTemplate");
            ValidateConfirmationTemplate(
                errors,
                businessEmailSubjectTemplate,
                "communication.confirmationTemplates.businessEmailSubjectTemplate",
                MaxEmailSubjectTemplateLength);
            ValidateConfirmationTemplate(
                errors,
                businessEmailBodyTemplate,
                "communication.confirmationTemplates.businessEmailBodyTemplate",
                MaxEmailBodyTemplateLength);
        }

        ValidateReporting(errors, tenantConfiguration.Reporting);

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

    private static void ValidateReporting(
        ICollection<string> errors,
        ReportingConfiguration? reporting)
    {
        if (reporting is null)
        {
            return;
        }

        if (reporting.CloseRate is < 0 or > 1)
        {
            errors.Add("reporting.closeRate must be between 0 and 1.");
        }

        if (reporting.AvgCommissionValue is < 0)
        {
            errors.Add("reporting.avgCommissionValue must be zero or greater.");
        }

        if (reporting.Baseline?.LeadsContactedPerWeek is < 0)
        {
            errors.Add("reporting.baseline.leadsContactedPerWeek must be zero or greater.");
        }

        if (reporting.Baseline?.AvgContactTimeSeconds is < 0)
        {
            errors.Add("reporting.baseline.avgContactTimeSeconds must be zero or greater.");
        }

        if (reporting.Baseline?.AppointmentsPerWeek is < 0)
        {
            errors.Add("reporting.baseline.appointmentsPerWeek must be zero or greater.");
        }
    }
}
