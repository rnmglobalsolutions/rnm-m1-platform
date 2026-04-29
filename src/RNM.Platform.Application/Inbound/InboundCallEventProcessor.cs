namespace RNM.Platform.Application.Inbound;

public sealed class InboundCallEventProcessor : IInboundCallEventProcessor
{
    public Task<InboundCallEventProcessingResult> ProcessAsync(
        InboundCallEvent inboundCallEvent,
        CancellationToken cancellationToken)
    {
        var result = inboundCallEvent.EventType is InboundCallEventType.Unsupported
            ? InboundCallEventProcessingResult.IgnoredUnsupported()
            : InboundCallEventProcessingResult.ProcessedResult();

        return Task.FromResult(result);
    }
}
