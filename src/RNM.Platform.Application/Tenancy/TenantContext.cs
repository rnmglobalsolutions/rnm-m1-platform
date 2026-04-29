namespace RNM.Platform.Application.Tenancy;

public sealed record TenantContext(
    string TenantId,
    string VerticalId,
    string BusinessName,
    string TimeZone,
    IReadOnlyCollection<string> ServiceAreaZipCodes,
    IReadOnlyCollection<string> ServiceAreaCities,
    TenantSecretNames SecretNames);

public sealed record TenantSecretNames(
    string CrmApiKey,
    string BookingApiKey,
    string VoiceWebhookSecret,
    string TwilioAccountSid,
    string TwilioAuthToken,
    string EmailConnectionString);
