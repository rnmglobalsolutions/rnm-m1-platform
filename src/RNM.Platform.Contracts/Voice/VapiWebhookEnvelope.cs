namespace RNM.Platform.Contracts.Voice;

public sealed record VapiWebhookEnvelope(
    string RawEventType,
    VapiWebhookEventKind EventKind,
    string? CallId,
    string? CallerPhoneNumber,
    string? Transcript,
    string? MessageRole,
    VapiToolCallRequest? ToolCall,
    DateTimeOffset ReceivedAtUtc)
{
    public bool IsSupported => EventKind is not VapiWebhookEventKind.Unsupported;
}

public enum VapiWebhookEventKind
{
    Unsupported = 0,
    CallStarted = 1,
    TranscriptUpdated = 2,
    CallEnded = 3,
    ToolCallRequested = 4
}

public sealed record VapiToolCallRequest(
    string? ToolCallId,
    string? Name,
    string? ArgumentsJson);
