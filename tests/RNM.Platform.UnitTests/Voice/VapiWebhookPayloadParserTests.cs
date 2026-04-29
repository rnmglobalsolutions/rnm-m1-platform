using RNM.Platform.Api.Voice;
using RNM.Platform.Contracts.Voice;
using Xunit;

namespace RNM.Platform.UnitTests.Voice;

public sealed class VapiWebhookPayloadParserTests
{
    [Fact]
    public void Parse_CallStartedEvent_ReturnsTypedEnvelope()
    {
        var parser = new VapiWebhookPayloadParser();

        var result = parser.Parse(
            """
            {
              "type": "call-started",
              "call": {
                "id": "call-123",
                "customer": { "number": "+15551234567" }
              }
            }
            """,
            DateTimeOffset.UtcNow);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Envelope);
        Assert.Equal(VapiWebhookEventKind.CallStarted, result.Envelope.EventKind);
        Assert.Equal("call-123", result.Envelope.CallId);
        Assert.Equal("+15551234567", result.Envelope.CallerPhoneNumber);
    }

    [Fact]
    public void Parse_TranscriptUpdateEvent_ReturnsTypedEnvelope()
    {
        var parser = new VapiWebhookPayloadParser();

        var result = parser.Parse(
            """
            {
              "message": {
                "type": "transcript",
                "role": "user",
                "transcript": "My AC stopped working.",
                "call": { "id": "call-123" }
              }
            }
            """,
            DateTimeOffset.UtcNow);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Envelope);
        Assert.Equal(VapiWebhookEventKind.TranscriptUpdated, result.Envelope.EventKind);
        Assert.Equal("call-123", result.Envelope.CallId);
        Assert.Equal("user", result.Envelope.MessageRole);
        Assert.Equal("My AC stopped working.", result.Envelope.Transcript);
    }

    [Fact]
    public void Parse_CallEndedEvent_ReturnsTypedEnvelope()
    {
        var parser = new VapiWebhookPayloadParser();

        var result = parser.Parse(
            """{"message":{"type":"call-ended","call":{"id":"call-456"}}}""",
            DateTimeOffset.UtcNow);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Envelope);
        Assert.Equal(VapiWebhookEventKind.CallEnded, result.Envelope.EventKind);
        Assert.Equal("call-456", result.Envelope.CallId);
    }

    [Fact]
    public void Parse_ToolCallEvent_ReturnsTypedEnvelope()
    {
        var parser = new VapiWebhookPayloadParser();

        var result = parser.Parse(
            """
            {
              "type": "tool-calls",
              "message": {
                "toolCalls": [
                  {
                    "id": "tool-1",
                    "function": {
                      "name": "check_availability",
                      "arguments": "{\"zipCode\":\"12345\"}"
                    }
                  }
                ]
              }
            }
            """,
            DateTimeOffset.UtcNow);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Envelope);
        Assert.Equal(VapiWebhookEventKind.ToolCallRequested, result.Envelope.EventKind);
        Assert.NotNull(result.Envelope.ToolCall);
        Assert.Equal("tool-1", result.Envelope.ToolCall.ToolCallId);
        Assert.Equal("check_availability", result.Envelope.ToolCall.Name);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsInvalidResult()
    {
        var parser = new VapiWebhookPayloadParser();

        var result = parser.Parse("{", DateTimeOffset.UtcNow);

        Assert.False(result.IsValid);
        Assert.Equal("malformed_json", result.ErrorCode);
    }

    [Fact]
    public void Parse_NonStringEventType_NormalizesToUnknownWithoutRawJson()
    {
        var parser = new VapiWebhookPayloadParser();

        var result = parser.Parse(
            """{"type":{"nested":"call-started"},"callId":"call-123"}""",
            DateTimeOffset.UtcNow);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Envelope);
        Assert.Equal("unknown", result.Envelope.RawEventType);
        Assert.Equal(VapiWebhookEventKind.Unsupported, result.Envelope.EventKind);
        Assert.DoesNotContain("{", result.Envelope.RawEventType);
        Assert.DoesNotContain("}", result.Envelope.RawEventType);
    }

    [Fact]
    public void Parse_JsonExceedingMaxDepth_ReturnsInvalidResult()
    {
        var parser = new VapiWebhookPayloadParser(new VapiWebhookOptions
        {
            MaxBodyBytes = VapiWebhookOptions.DefaultMaxBodyBytes,
            JsonMaxDepth = 2
        });

        var result = parser.Parse("""{"message":{"call":{"id":"call-123"}}}""", DateTimeOffset.UtcNow);

        Assert.False(result.IsValid);
        Assert.Equal("malformed_json", result.ErrorCode);
    }
}
