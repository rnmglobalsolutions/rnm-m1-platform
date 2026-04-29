using RNM.Platform.Domain.Tenancy;
using RNM.Platform.SharedKernel.Correlation;

namespace RNM.Platform.Domain.Calls;

public sealed record CallSession(
    TenantId TenantId,
    CorrelationId CorrelationId,
    string ProviderCallId,
    string CallerPhoneNumber,
    DateTimeOffset StartedAtUtc,
    CallType CallType = CallType.Unknown,
    CallOutcome Outcome = CallOutcome.Unknown);
