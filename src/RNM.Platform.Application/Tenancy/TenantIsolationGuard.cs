namespace RNM.Platform.Application.Tenancy;

public sealed class TenantIsolationGuard
{
    public void EnsureSameTenant(string expectedTenantId, string actualTenantId)
    {
        if (!IsSameTenant(expectedTenantId, actualTenantId))
        {
            throw new TenantIsolationException("Tenant boundary violation.");
        }
    }

    public bool IsSameTenant(string expectedTenantId, string actualTenantId)
    {
        return !string.IsNullOrWhiteSpace(expectedTenantId)
            && !string.IsNullOrWhiteSpace(actualTenantId)
            && string.Equals(expectedTenantId, actualTenantId, StringComparison.Ordinal);
    }
}
