namespace RNM.Platform.Application.Inbound;

public interface IInboundCallEventProcessor
{
    Task<InboundCallEventProcessingResult> ProcessAsync(
        InboundCallEvent inboundCallEvent,
        CancellationToken cancellationToken);
}
