using RNM.Platform.Application.LeadIntake;

namespace RNM.Platform.Application.Ports.LeadIntake;

public interface IInboundLeadReceiptStore
{
    Task<InboundLeadReceiptClaimResult> TryBeginAsync(
        InboundLeadReceiptClaimRequest request,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        InboundLeadReceiptCompletionRequest request,
        CancellationToken cancellationToken);

    Task FailAsync(
        InboundLeadReceiptFailureRequest request,
        CancellationToken cancellationToken);
}

