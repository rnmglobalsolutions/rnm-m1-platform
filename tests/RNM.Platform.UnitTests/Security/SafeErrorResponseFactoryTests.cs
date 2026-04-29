using RNM.Platform.Api.Security;
using Xunit;

namespace RNM.Platform.UnitTests.Security;

public sealed class SafeErrorResponseFactoryTests
{
    [Fact]
    public void CreateUnauthorized_ReturnsGenericMessageAndCorrelationId()
    {
        var factory = new SafeErrorResponseFactory();

        var response = factory.CreateUnauthorized("corr-123");

        Assert.Equal("unauthorized", response.Code);
        Assert.Equal("The request is not authorized.", response.Message);
        Assert.Equal("corr-123", response.CorrelationId);
    }

    [Fact]
    public void CreateTenantViolation_DoesNotExposeTenantIdentifiers()
    {
        var factory = new SafeErrorResponseFactory();

        var response = factory.CreateTenantViolation("corr-123");

        Assert.Equal("tenant_violation", response.Code);
        Assert.DoesNotContain("tenant-a", response.Message, StringComparison.OrdinalIgnoreCase);
    }
}
