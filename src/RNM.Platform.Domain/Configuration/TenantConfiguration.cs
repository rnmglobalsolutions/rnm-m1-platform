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
    CommunicationConfiguration Communication);

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
    ConfirmationTemplateConfiguration ConfirmationTemplates);

public sealed record ConfirmationTemplateConfiguration(
    string SmsBodyTemplate,
    string? EmailSubjectTemplate = null,
    string? EmailBodyTemplate = null);
