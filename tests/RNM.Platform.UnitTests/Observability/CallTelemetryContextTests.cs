using RNM.Platform.Application.Observability;
using Xunit;

namespace RNM.Platform.UnitTests.Observability;

public sealed class CallTelemetryContextTests
{
    [Fact]
    public void CurrentCallId_OutsideScope_IsNull()
    {
        Assert.Null(CallTelemetryContext.CurrentCallId);
    }

    [Fact]
    public void BeginCall_SetsCallIdAndDisposeClearsIt()
    {
        using (CallTelemetryContext.BeginCall("call-1"))
        {
            Assert.Equal("call-1", CallTelemetryContext.CurrentCallId);
        }

        Assert.Null(CallTelemetryContext.CurrentCallId);
    }

    [Fact]
    public void BeginCall_NestedScopesRestorePreviousValue()
    {
        using (CallTelemetryContext.BeginCall("outer"))
        {
            using (CallTelemetryContext.BeginCall("inner"))
            {
                Assert.Equal("inner", CallTelemetryContext.CurrentCallId);
            }

            Assert.Equal("outer", CallTelemetryContext.CurrentCallId);
        }

        Assert.Null(CallTelemetryContext.CurrentCallId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("call {1}")]
    [InlineData("call 1")]
    [InlineData("{call}")]
    [InlineData("+15551234567")]
    public void BeginCall_InvalidValue_IsIgnored(string? callId)
    {
        using (CallTelemetryContext.BeginCall(callId))
        {
            Assert.Null(CallTelemetryContext.CurrentCallId);
        }
    }

    [Fact]
    public void BeginCall_TooLongValue_IsIgnored()
    {
        using (CallTelemetryContext.BeginCall(new string('a', 129)))
        {
            Assert.Null(CallTelemetryContext.CurrentCallId);
        }

        using (CallTelemetryContext.BeginCall(new string('a', 128)))
        {
            Assert.Equal(128, CallTelemetryContext.CurrentCallId?.Length);
        }
    }

    [Fact]
    public void BeginCall_InvalidNestedValue_KeepsOuterScope()
    {
        using (CallTelemetryContext.BeginCall("outer"))
        using (CallTelemetryContext.BeginCall("bad value"))
        {
            Assert.Equal("outer", CallTelemetryContext.CurrentCallId);
        }
    }

    [Fact]
    public void BeginCall_TrimsValue()
    {
        using (CallTelemetryContext.BeginCall("  call-1  "))
        {
            Assert.Equal("call-1", CallTelemetryContext.CurrentCallId);
        }
    }

    [Fact]
    public void Enrich_InsideScope_AddsCallId()
    {
        var properties = new Dictionary<string, string> { ["tenantId"] = "tenant-a" };

        using (CallTelemetryContext.BeginCall("call-1"))
        {
            var enriched = CallTelemetryContext.Enrich(properties);

            Assert.Equal("call-1", enriched["callId"]);
            Assert.Equal("tenant-a", enriched["tenantId"]);
        }

        Assert.False(properties.ContainsKey("callId"));
    }

    [Fact]
    public void Enrich_ExplicitCallId_IsKept()
    {
        var properties = new Dictionary<string, string> { ["callId"] = "explicit" };

        using (CallTelemetryContext.BeginCall("call-1"))
        {
            Assert.Equal("explicit", CallTelemetryContext.Enrich(properties)["callId"]);
        }
    }

    [Fact]
    public void Enrich_OutsideScope_ReturnsInputUnchanged()
    {
        var properties = new Dictionary<string, string> { ["tenantId"] = "tenant-a" };

        var enriched = CallTelemetryContext.Enrich(properties);

        Assert.Same(properties, enriched);
        Assert.False(enriched.ContainsKey("callId"));
    }

    [Fact]
    public async Task CurrentCallId_FlowsIntoAwaitedWork()
    {
        using (CallTelemetryContext.BeginCall("call-1"))
        {
            await Task.Delay(5);
            var seen = await Task.Run(() => CallTelemetryContext.CurrentCallId);

            Assert.Equal("call-1", seen);
            Assert.Equal("call-1", CallTelemetryContext.CurrentCallId);
        }
    }

    [Fact]
    public async Task ConcurrentScopes_DoNotLeak()
    {
        async Task<string?> RunAsync(string callId)
        {
            using (CallTelemetryContext.BeginCall(callId))
            {
                await Task.Delay(10);
                await Task.Yield();
                return CallTelemetryContext.CurrentCallId;
            }
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => RunAsync($"call-{index}")));

        for (var index = 0; index < results.Length; index++)
        {
            Assert.Equal($"call-{index}", results[index]);
        }

        Assert.Null(CallTelemetryContext.CurrentCallId);
    }
}
