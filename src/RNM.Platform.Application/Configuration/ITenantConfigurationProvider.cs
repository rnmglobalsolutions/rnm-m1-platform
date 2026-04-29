using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Configuration;

public interface ITenantConfigurationProvider
{
    Task<TenantConfiguration> GetTenantConfigurationAsync(
        string tenantId,
        CancellationToken cancellationToken);
}
