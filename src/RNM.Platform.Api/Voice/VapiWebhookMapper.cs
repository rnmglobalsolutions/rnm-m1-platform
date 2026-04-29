using RNM.Platform.Application.Inbound;
using RNM.Platform.Application.Tenancy;
using RNM.Platform.Contracts.Voice;

namespace RNM.Platform.Api.Voice;

public sealed class VapiWebhookMapper
{
    public InboundCallEvent Map(
        VapiWebhookEnvelope envelope,
        TenantContext tenantContext,
        string correlationId)
    {
        return new InboundCallEvent(
            tenantContext.TenantId,
            tenantContext.VerticalId,
            correlationId,
            MapEventType(envelope.EventKind),
            new CallSession(
                envelope.CallId,
                envelope.CallerPhoneNumber),
            "vapi",
            envelope.RawEventType,
            envelope.Transcript,
            envelope.MessageRole,
            envelope.ToolCall is null
                ? null
                : new StructuredActionRequest(
                    envelope.ToolCall.ToolCallId,
                    envelope.ToolCall.Name,
                    envelope.ToolCall.ArgumentsJson),
            envelope.ReceivedAtUtc);
    }

    private static InboundCallEventType MapEventType(VapiWebhookEventKind eventKind)
    {
        return eventKind switch
        {
            VapiWebhookEventKind.CallStarted => InboundCallEventType.CallStarted,
            VapiWebhookEventKind.TranscriptUpdated => InboundCallEventType.TranscriptUpdated,
            VapiWebhookEventKind.CallEnded => InboundCallEventType.CallEnded,
            VapiWebhookEventKind.ToolCallRequested => InboundCallEventType.ActionRequested,
            _ => InboundCallEventType.Unsupported
        };
    }
}
