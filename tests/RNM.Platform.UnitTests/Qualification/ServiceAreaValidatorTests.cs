using RNM.Platform.Application.Qualification;
using RNM.Platform.Domain.Configuration;
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
    [InlineData("75001")]
    [InlineData("99999")]
    public void IsInServiceArea_ReturnsTrue_WhenWildcardZipCodeIsConfigured(string zipCode)
    {
        var validator = new ServiceAreaValidator();

        var result = validator.IsInServiceArea(zipCode, ["*"]);

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

    [Theory]
    [InlineData("TX")]
    [InlineData("texas")]
    [InlineData(" tx ")]
    public void Validate_StateMode_ReturnsInServiceArea_WhenStateIsAllowed(string state)
    {
        var decision = new ServiceAreaValidator().Validate(
            ServiceAreaModes.State, null, [], state, ["CA", "TX"]);

        Assert.Equal(ServiceAreaDecisionState.InServiceArea, decision.State);
    }

    [Fact]
    public void Validate_StateMode_ReturnsOutOfServiceArea_WhenStateIsNotAllowed()
    {
        var decision = new ServiceAreaValidator().Validate(
            ServiceAreaModes.State, null, [], "NY", ["CA", "TX"]);

        Assert.Equal(ServiceAreaDecisionState.OutOfServiceArea, decision.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_StateMode_ReturnsMissingState_WhenStateIsBlank(string? state)
    {
        var decision = new ServiceAreaValidator().Validate(
            ServiceAreaModes.State, "75001", ["75001"], state, ["TX"]);

        Assert.Equal(ServiceAreaDecisionState.MissingState, decision.State);
    }

    [Theory]
    [InlineData("ZZ")]
    [InlineData("Narnia")]
    public void Validate_StateMode_ReturnsInvalidState_WhenStateIsNotRecognized(string state)
    {
        var decision = new ServiceAreaValidator().Validate(
            ServiceAreaModes.State, null, [], state, ["TX"]);

        Assert.Equal(ServiceAreaDecisionState.InvalidState, decision.State);
    }

    [Fact]
    public void Validate_StateMode_ReturnsNeedsEscalation_WhenNoStatesAreConfigured()
    {
        var decision = new ServiceAreaValidator().Validate(
            ServiceAreaModes.State, null, [], "TX", []);

        Assert.Equal(ServiceAreaDecisionState.NeedsEscalation, decision.State);
    }

    [Fact]
    public void Validate_StateMode_DoesNotNeedZip()
    {
        var decision = new ServiceAreaValidator().Validate(
            ServiceAreaModes.State, null, [], "CA", ["CA"]);

        Assert.Equal(ServiceAreaDecisionState.InServiceArea, decision.State);
    }

    [Fact]
    public void Validate_NoneMode_ReturnsNotRequired_WithoutZipOrState()
    {
        var decision = new ServiceAreaValidator().Validate(
            ServiceAreaModes.None, null, [], null, []);

        Assert.Equal(ServiceAreaDecisionState.NotRequired, decision.State);
    }

    [Fact]
    public void Validate_ZipMode_BehavesLikeTheOriginalOverload()
    {
        var validator = new ServiceAreaValidator();

        Assert.Equal(
            ServiceAreaDecisionState.MissingZipCode,
            validator.Validate(ServiceAreaModes.Zip, null, ["75001"], "TX", ["TX"]).State);
        Assert.Equal(
            ServiceAreaDecisionState.OutOfServiceArea,
            validator.Validate(ServiceAreaModes.Zip, "99999", ["75001"], "TX", ["TX"]).State);
        Assert.Equal(
            ServiceAreaDecisionState.NeedsEscalation,
            validator.Validate(ServiceAreaModes.Zip, "75001", [], null, []).State);
    }
}
