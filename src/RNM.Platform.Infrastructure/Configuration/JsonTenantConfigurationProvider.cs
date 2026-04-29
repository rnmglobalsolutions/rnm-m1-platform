using System.Text.Json;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;

namespace RNM.Platform.Infrastructure.Configuration;

public sealed class JsonTenantConfigurationProvider : ITenantConfigurationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string configRoot;
    private readonly IConfigurationValidator configurationValidator;

    public JsonTenantConfigurationProvider(
        string configRoot,
        IConfigurationValidator configurationValidator)
    {
        this.configRoot = string.IsNullOrWhiteSpace(configRoot)
            ? throw new ArgumentException("Config root is required.", nameof(configRoot))
            : configRoot;
        this.configurationValidator = configurationValidator;
    }

    public async Task<TenantConfiguration> GetTenantConfigurationAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ConfigurationException("Tenant id is required.");
        }

        var path = Path.Combine(configRoot, "tenants", $"{tenantId}.json");
        if (!File.Exists(path))
        {
            throw new ConfigurationException($"Tenant configuration '{tenantId}' was not found.");
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<TenantConfigurationDto>(json, JsonOptions)
            ?? throw new ConfigurationException($"Tenant configuration '{tenantId}' is empty or invalid JSON.");

        var configuration = dto.ToDomain();
        var validation = configurationValidator.ValidateTenant(configuration);
        if (!validation.IsValid)
        {
            throw new ConfigurationException(
                $"Tenant configuration '{tenantId}' is invalid: {string.Join(" ", validation.Errors)}");
        }

        return configuration;
    }

    private sealed record TenantConfigurationDto(
        string? TenantId,
        string? VerticalId,
        string? BusinessName,
        string? TimeZone,
        ServiceAreaConfigurationDto? ServiceArea,
        ProviderConfigurationDto? Providers,
        SecretNameConfigurationDto? SecretNames,
        CommunicationConfigurationDto? Communication)
    {
        public TenantConfiguration ToDomain()
        {
            return new TenantConfiguration(
                new TenantId(TenantId ?? string.Empty),
                new VerticalId(VerticalId ?? string.Empty),
                BusinessName ?? string.Empty,
                TimeZone ?? string.Empty,
                new ServiceAreaConfiguration(
                    ServiceArea?.ZipCodes ?? [],
                    ServiceArea?.Cities ?? [],
                    ServiceArea?.ReferralMessage),
                new ProviderConfiguration(
                    Providers?.CrmProvider ?? string.Empty,
                    Providers?.BookingProvider ?? string.Empty,
                    Providers?.SmsProvider ?? string.Empty,
                    Providers?.EmailProvider ?? string.Empty),
                new SecretNameConfiguration(
                    SecretNames?.CrmApiKey ?? string.Empty,
                    SecretNames?.BookingApiKey ?? string.Empty,
                    SecretNames?.VoiceWebhookSecret ?? SecretNames?.VapiWebhookSecret ?? string.Empty,
                    SecretNames?.TwilioAccountSid ?? string.Empty,
                    SecretNames?.TwilioAuthToken ?? string.Empty,
                    SecretNames?.EmailConnectionString ?? string.Empty),
                new CommunicationConfiguration(
                    Communication?.SmsFromPhoneNumber ?? string.Empty,
                    Communication?.EmailFromAddress,
                    new ConfirmationTemplateConfiguration(
                        Communication?.ConfirmationTemplates?.SmsBodyTemplate ?? string.Empty,
                        Communication?.ConfirmationTemplates?.EmailSubjectTemplate,
                        Communication?.ConfirmationTemplates?.EmailBodyTemplate)));
        }
    }

    private sealed record ServiceAreaConfigurationDto(
        IReadOnlyCollection<string> ZipCodes,
        IReadOnlyCollection<string> Cities,
        string? ReferralMessage);

    private sealed record ProviderConfigurationDto(
        string? CrmProvider,
        string? BookingProvider,
        string? SmsProvider,
        string? EmailProvider);

    private sealed record SecretNameConfigurationDto(
        string? CrmApiKey,
        string? BookingApiKey,
        string? VoiceWebhookSecret,
        string? VapiWebhookSecret,
        string? TwilioAccountSid,
        string? TwilioAuthToken,
        string? EmailConnectionString);

    private sealed record CommunicationConfigurationDto(
        string? SmsFromPhoneNumber,
        string? EmailFromAddress,
        ConfirmationTemplateConfigurationDto? ConfirmationTemplates);

    private sealed record ConfirmationTemplateConfigurationDto(
        string? SmsBodyTemplate,
        string? EmailSubjectTemplate,
        string? EmailBodyTemplate);
}
