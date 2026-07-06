using RNM.Platform.Application.Outbound;

namespace RNM.Platform.Application.Ports.Outbound;

public interface IOutboundCallAdapter
{
    Task<OutboundCallStartResult> StartCallAsync(
        OutboundCallStartRequest request,
        CancellationToken cancellationToken);
}
