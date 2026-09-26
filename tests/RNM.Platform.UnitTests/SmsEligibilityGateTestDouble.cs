using RNM.Platform.Application.Compliance;
using RNM.Platform.Application.Confirmations;

namespace RNM.Platform.UnitTests;

internal sealed class AllowingSmsEligibilityGate : ISmsEligibilityGate
{
    public List<SmsEligibilityRequest> Requests { get; } = [];

    public SmsEligibilitySkipReason? DenyWith { get; init; }

    public Task<SmsEligibilityResult> EvaluateAsync(
        SmsEligibilityRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(DenyWith is { } reason
            ? new SmsEligibilityResult(false, SkipReason: reason)
            : new SmsEligibilityResult(
                true,
                new SmsEligibilityProof(request.TenantId, request.CorrelationId, request.Category)));
    }
}
