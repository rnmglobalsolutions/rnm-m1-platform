using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using RNM.Platform.Application.Observability;
using RNM.Platform.Infrastructure.Observability;
using Xunit;

namespace RNM.Platform.UnitTests.Observability;

public sealed class ApplicationInsightsEventLoggerTests
{
    [Fact]
    public async Task LogEventAsync_InsideCallScope_AddsCallId()
    {
        var (logger, channel) = CreateLogger();

        using (CallTelemetryContext.BeginCall("call-abc"))
        {
            await logger.LogEventAsync("voice.test", Properties(), CancellationToken.None);
        }

        var telemetry = Assert.Single(channel.Events);
        Assert.Equal("call-abc", telemetry.Properties["callId"]);
        Assert.Equal("corr-1", telemetry.Properties["correlationId"]);
    }

    [Fact]
    public async Task LogEventAsync_OutsideCallScope_HasNoCallId()
    {
        var (logger, channel) = CreateLogger();

        await logger.LogEventAsync("voice.test", Properties(), CancellationToken.None);

        var telemetry = Assert.Single(channel.Events);
        Assert.False(telemetry.Properties.ContainsKey("callId"));
    }

    [Fact]
    public async Task LogEventAsync_InvalidCallId_IsNotAdded()
    {
        var (logger, channel) = CreateLogger();

        using (CallTelemetryContext.BeginCall("bearer abc"))
        {
            await logger.LogEventAsync("voice.test", Properties(), CancellationToken.None);
        }

        var telemetry = Assert.Single(channel.Events);
        Assert.False(telemetry.Properties.ContainsKey("callId"));
    }

    private static Dictionary<string, string> Properties() => new()
    {
        ["correlationId"] = "corr-1",
        ["tenantId"] = "tenant-a"
    };

    private static (ApplicationInsightsEventLogger Logger, CapturingChannel Channel) CreateLogger()
    {
        var channel = new CapturingChannel();
        var configuration = new TelemetryConfiguration
        {
            ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000",
            TelemetryChannel = channel
        };
        var client = new TelemetryClient(configuration);

        return (
            new ApplicationInsightsEventLogger(client, NullLogger<ApplicationInsightsEventLogger>.Instance),
            channel);
    }

    private sealed class CapturingChannel : ITelemetryChannel
    {
        public List<EventTelemetry> Events { get; } = [];

        public bool? DeveloperMode { get; set; }

        public string EndpointAddress { get; set; } = string.Empty;

        public void Send(ITelemetry item)
        {
            if (item is EventTelemetry eventTelemetry)
            {
                Events.Add(eventTelemetry);
            }
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
