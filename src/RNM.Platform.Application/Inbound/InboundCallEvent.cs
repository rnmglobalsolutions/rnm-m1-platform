namespace RNM.Platform.Application.Inbound;

public sealed record InboundCallEvent(
    string TenantId,
    string VerticalId,
    string CorrelationId,
    InboundCallEventType EventType,
    CallSession Session,
    string Provider,
    string ProviderEventType,
    string? Transcript,
    string? MessageRole,
    StructuredActionRequest? ActionRequest,
    DateTimeOffset ReceivedAtUtc);

public enum InboundCallEventType
{
    Unsupported = 0,
    CallStarted = 1,
    TranscriptUpdated = 2,
    CallEnded = 3,
    ActionRequested = 4
}

public sealed record CallSession(
    string? ProviderCallId,
    string? CallerPhoneNumber);

public sealed record StructuredActionRequest(
    string? ProviderActionId,
    string? Name,
    string? ArgumentsJson);
