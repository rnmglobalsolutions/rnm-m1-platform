using RNM.Platform.Domain.Tenancy;

namespace RNM.Platform.Domain.Configuration;

public sealed record TenantConfiguration(
    TenantId TenantId,
    VerticalId VerticalId,
    string BusinessName,
    string TimeZone,
    ServiceAreaConfiguration ServiceArea,
    ProviderConfiguration Providers,
    SecretNameConfiguration SecretNames,
    CommunicationConfiguration Communication,
    ReportingConfiguration? Reporting = null);

public sealed record ProviderConfiguration(
    string CrmProvider,
    string BookingProvider,
    string SmsProvider,
    string EmailProvider);

public sealed record SecretNameConfiguration(
    string CrmApiKey,
    string BookingApiKey,
    string VoiceWebhookSecret,
    string TwilioAccountSid,
    string TwilioAuthToken,
    string EmailConnectionString,
    string? CrmCredentials = null,
    string? BookingCredentials = null);

public sealed record CommunicationConfiguration(
    string SmsFromPhoneNumber,
    string? EmailFromAddress,
    ConfirmationTemplateConfiguration ConfirmationTemplates,
    string? BusinessNotificationEmail = null,
    string? BusinessNotificationPhoneNumber = null,
    bool NotifyBusinessBySmsForUrgentOnly = true);

public sealed record ConfirmationTemplateConfiguration(
    string SmsBodyTemplate,
    string? EmailSubjectTemplate = null,
    string? EmailBodyTemplate = null,
    string? BusinessSmsBodyTemplate = null,
    string? BusinessEmailSubjectTemplate = null,
    string? BusinessEmailBodyTemplate = null);

public sealed record ReportingConfiguration(
    decimal? CloseRate = null,
    decimal? AvgCommissionValue = null,
    ReportingBaselineConfiguration? Baseline = null);

public sealed record ReportingBaselineConfiguration(
    int? LeadsContactedPerWeek = null,
    int? AvgContactTimeSeconds = null,
    int? AppointmentsPerWeek = null)
{
    public bool IsSet =>
        LeadsContactedPerWeek.HasValue
        || AvgContactTimeSeconds.HasValue
        || AppointmentsPerWeek.HasValue;
}
