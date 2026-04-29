using RNM.Platform.SharedKernel.Correlation;
using Xunit;

namespace RNM.Platform.UnitTests.Correlation;

public sealed class CorrelationContextTests
{
    [Fact]
    public void FromStringOrNew_UsesProvidedCorrelationId_WhenHeaderValueIsPresent()
    {
        var context = CorrelationContext.FromStringOrNew(" corr-123:abc_456 ");

        Assert.Equal("corr-123:abc_456", context.Value);
    }

    [Fact]
    public void FromStringOrNew_GeneratesCorrelationId_WhenHeaderValueIsMissing()
    {
        var context = CorrelationContext.FromStringOrNew(null);

        Assert.False(string.IsNullOrWhiteSpace(context.Value));
    }

    [Fact]
    public void HeaderName_UsesExpectedIncomingHeader()
    {
        Assert.Equal("x-correlation-id", CorrelationId.HeaderName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad value")]
    [InlineData("bad\r\nvalue")]
    [InlineData("bad/value")]
    public void FromStringOrNew_RegeneratesCorrelationId_WhenHeaderValueIsMalformed(string value)
    {
        var context = CorrelationContext.FromStringOrNew(value);

        Assert.NotEqual(value, context.Value);
        Assert.True(CorrelationId.TryNormalize(context.Value, out _));
    }

    [Fact]
    public void FromStringOrNew_RegeneratesCorrelationId_WhenHeaderValueIsTooLarge()
    {
        var context = CorrelationContext.FromStringOrNew(new string('a', CorrelationId.MaxLength + 1));

        Assert.True(CorrelationId.TryNormalize(context.Value, out _));
        Assert.Equal(32, context.Value.Length);
    }
}
