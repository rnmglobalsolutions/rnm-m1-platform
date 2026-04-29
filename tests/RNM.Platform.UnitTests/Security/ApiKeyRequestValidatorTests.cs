using RNM.Platform.Api.Security;
using Xunit;

namespace RNM.Platform.UnitTests.Security;

public sealed class ApiKeyRequestValidatorTests
{
    [Fact]
    public void IsValid_ReturnsTrue_WhenProvidedKeyMatchesExpectedKey()
    {
        var validator = new ApiKeyRequestValidator();

        var result = validator.IsValid("expected-key", "expected-key");

        Assert.True(result);
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenExpectedKeyIsMissing()
    {
        var validator = new ApiKeyRequestValidator();

        var result = validator.IsValid("provided-key", "");

        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-key")]
    public void IsValid_ReturnsFalse_WhenProvidedKeyIsMissingOrDifferent(string? providedApiKey)
    {
        var validator = new ApiKeyRequestValidator();

        var result = validator.IsValid(providedApiKey, "expected-key");

        Assert.False(result);
    }
}
