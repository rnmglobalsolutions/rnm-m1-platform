using RNM.Platform.Application.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.Tenancy;

public sealed class TenantIsolationGuardTests
{
    [Fact]
    public void EnsureSameTenant_DoesNotThrow_WhenTenantsMatch()
    {
        var guard = new TenantIsolationGuard();

        guard.EnsureSameTenant("tenant-a", "tenant-a");
    }

    [Theory]
    [InlineData("tenant-a", "tenant-b")]
    [InlineData("", "tenant-a")]
    [InlineData("tenant-a", "")]
    public void EnsureSameTenant_ThrowsTenantIsolationException_WhenTenantsDoNotMatch(
        string expectedTenantId,
        string actualTenantId)
    {
        var guard = new TenantIsolationGuard();

        Assert.Throws<TenantIsolationException>(
            () => guard.EnsureSameTenant(expectedTenantId, actualTenantId));
    }
}
