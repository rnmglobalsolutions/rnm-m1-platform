using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Infrastructure.Configuration;

internal static class TenantSecretNameExtensions
{
    public static string GetCrmCredentialsSecretName(this TenantConfiguration tenantConfiguration)
    {
        return string.IsNullOrWhiteSpace(tenantConfiguration.SecretNames.CrmCredentials)
            ? tenantConfiguration.SecretNames.CrmApiKey
            : tenantConfiguration.SecretNames.CrmCredentials;
    }

    public static string GetBookingCredentialsSecretName(this TenantConfiguration tenantConfiguration)
    {
        return string.IsNullOrWhiteSpace(tenantConfiguration.SecretNames.BookingCredentials)
            ? tenantConfiguration.SecretNames.BookingApiKey
            : tenantConfiguration.SecretNames.BookingCredentials;
    }
}
