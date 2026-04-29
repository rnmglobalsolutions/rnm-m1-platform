using RNM.Platform.Application.Configuration;

namespace RNM.Platform.Application.Tenancy;

public sealed class TenantResolver
{
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly IVerticalConfigurationProvider verticalConfigurationProvider;

    public TenantResolver(
        ITenantConfigurationProvider tenantConfigurationProvider,
        IVerticalConfigurationProvider verticalConfigurationProvider)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.verticalConfigurationProvider = verticalConfigurationProvider;
    }

    public async Task<TenantContext> ResolveAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new TenantResolutionException("Tenant id is required.");
        }

        var tenantConfiguration = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        await verticalConfigurationProvider
            .GetVerticalConfigurationAsync(tenantConfiguration.VerticalId.Value, cancellationToken)
            .ConfigureAwait(false);

        return new TenantContext(
            tenantConfiguration.TenantId.Value,
            tenantConfiguration.VerticalId.Value,
            tenantConfiguration.BusinessName,
            tenantConfiguration.TimeZone,
            tenantConfiguration.ServiceArea.ZipCodes,
            tenantConfiguration.ServiceArea.Cities,
            new TenantSecretNames(
                tenantConfiguration.SecretNames.CrmApiKey,
                tenantConfiguration.SecretNames.BookingApiKey,
                tenantConfiguration.SecretNames.VoiceWebhookSecret,
                tenantConfiguration.SecretNames.TwilioAccountSid,
                tenantConfiguration.SecretNames.TwilioAuthToken,
                tenantConfiguration.SecretNames.EmailConnectionString));
    }
}
