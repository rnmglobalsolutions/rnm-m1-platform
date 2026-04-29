using RNM.Platform.Api.Functions;
using Xunit;

namespace RNM.Platform.IntegrationTests.Webhooks;

public sealed class HealthFunctionTests
{
    [Fact]
    public void HealthFunction_TypeExists()
    {
        Assert.NotNull(typeof(HealthFunction));
    }
}
