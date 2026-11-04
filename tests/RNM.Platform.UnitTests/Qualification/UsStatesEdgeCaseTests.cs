using RNM.Platform.Application.Qualification;
using Xunit;

namespace RNM.Platform.UnitTests.Qualification;

public sealed class UsStatesEdgeCaseTests
{
    [Theory]
    [InlineData("tx.", "TX")]
    [InlineData("NE ", "NE")]
    [InlineData("  new   york ", "NY")]
    [InlineData("Washington DC", "DC")]
    public void TryNormalize_AcceptsEdgeCaseInput(string input, string expected)
    {
        Assert.True(UsStates.TryNormalize(input, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData("Northeast")]
    [InlineData("Texas 75001")]
    [InlineData("Nebraska, USA")]
    [InlineData("T X")]
    [InlineData("N.E.B.")]
    public void TryNormalize_RejectsEdgeCaseInput(string input)
    {
        Assert.False(UsStates.TryNormalize(input, out _));
    }
}
