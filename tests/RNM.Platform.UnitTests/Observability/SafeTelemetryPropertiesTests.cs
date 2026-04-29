using RNM.Platform.Application.Observability;
using Xunit;

namespace RNM.Platform.UnitTests.Observability;

public sealed class SafeTelemetryPropertiesTests
{
    [Fact]
    public void Add_IncludesSafeProperties()
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", "corr-123")
            .Add("tenantId", "tenant-a")
            .Add("endpoint", "health")
            .ToDictionary();

        Assert.Equal("corr-123", properties["correlationId"]);
        Assert.Equal("tenant-a", properties["tenantId"]);
        Assert.Equal("health", properties["endpoint"]);
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("authHeader")]
    [InlineData("accessToken")]
    [InlineData("webhookSecret")]
    [InlineData("apiKey")]
    [InlineData("rawBody")]
    [InlineData("payload")]
    public void Add_DropsSensitiveProperties(string propertyName)
    {
        var properties = new SafeTelemetryProperties()
            .Add(propertyName, "sensitive-value")
            .ToDictionary();

        Assert.DoesNotContain(propertyName, properties.Keys);
    }

    [Fact]
    public void Add_DropsEmptyValues()
    {
        var properties = new SafeTelemetryProperties()
            .Add("tenantId", "")
            .Add("correlationId", null)
            .ToDictionary();

        Assert.Empty(properties);
    }

    [Fact]
    public void Add_SanitizesControlCharactersAndTrimsLongValues()
    {
        var longValue = $"  abc\r\ndef{new string('x', SafeTelemetryProperties.MaxValueLength + 20)}  ";

        var properties = new SafeTelemetryProperties()
            .Add("outcome", longValue)
            .ToDictionary();

        Assert.DoesNotContain('\r', properties["outcome"]);
        Assert.DoesNotContain('\n', properties["outcome"]);
        Assert.True(properties["outcome"].Length <= SafeTelemetryProperties.MaxValueLength);
    }

    [Theory]
    [InlineData("Bearer token-value")]
    [InlineData("sha256=signature-value")]
    [InlineData("Authorization: secret")]
    public void Add_RedactsValuesWithSensitiveMarkers(string value)
    {
        var properties = new SafeTelemetryProperties()
            .Add("outcome", value)
            .ToDictionary();

        Assert.Equal(SafeTelemetryProperties.RedactedValue, properties["outcome"]);
    }
}
