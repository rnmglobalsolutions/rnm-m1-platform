using RNM.Platform.Api.Http;
using Xunit;

namespace RNM.Platform.UnitTests.Http;

public sealed class FormUrlEncodedBodyParserTests
{
    [Fact]
    public void Parse_ReturnsDecodedFormValues()
    {
        var parser = new FormUrlEncodedBodyParser();

        var values = parser.Parse("MessageSid=SM123&MessageStatus=delivered&To=%2B15551234567");

        Assert.Contains(values, pair => pair.Key == "MessageSid" && pair.Value == "SM123");
        Assert.Contains(values, pair => pair.Key == "MessageStatus" && pair.Value == "delivered");
        Assert.Contains(values, pair => pair.Key == "To" && pair.Value == "+15551234567");
    }

    [Fact]
    public void Parse_ReturnsEmptyCollection_WhenBodyIsEmpty()
    {
        var parser = new FormUrlEncodedBodyParser();

        var values = parser.Parse("");

        Assert.Empty(values);
    }
}
