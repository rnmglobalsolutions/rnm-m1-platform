using RNM.Platform.Application.Qualification;
using Xunit;

namespace RNM.Platform.UnitTests.Qualification;

public sealed class ServiceAreaValidatorTests
{
    [Fact]
    public void IsInServiceArea_ReturnsTrue_WhenZipCodeIsConfigured()
    {
        var validator = new ServiceAreaValidator();

        var result = validator.IsInServiceArea("75001", ["75001", "75002"]);

        Assert.True(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("99999")]
    public void IsInServiceArea_ReturnsFalse_WhenZipCodeIsMissingOrNotConfigured(string? zipCode)
    {
        var validator = new ServiceAreaValidator();

        var result = validator.IsInServiceArea(zipCode, ["75001", "75002"]);

        Assert.False(result);
    }
}
