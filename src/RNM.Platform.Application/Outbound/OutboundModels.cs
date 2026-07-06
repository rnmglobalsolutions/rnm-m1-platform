using RNM.Platform.Application.Crm;

namespace RNM.Platform.Application.Outbound;

public sealed record OutboundCallStartRequest(
    string TenantId,
    string CorrelationId,
    CrmContactRecord Contact,
    string AssistantId,
    string PhoneNumberId,
    string CallbackWebhookUrl);

public sealed record OutboundCallStartResult(
    bool Succeeded,
    string? ProviderCallId,
    string? Status,
    OutboundCallFailureReason? FailureReason = null,
    bool Retryable = false,
    string? Message = null);

public enum OutboundCallFailureReason
{
    MissingConfiguration,
    MissingPhoneNumber,
    ConsentOptedOut,
    ProviderFailure,
    CircuitOpen,
    AdapterFailure
}

public sealed record OutboundCampaignRunRequest(
    string TenantId,
    string CampaignId,
    string CorrelationId);

public sealed record OutboundCampaignRunResult(
    bool Succeeded,
    string TenantId,
    string CampaignId,
    string CorrelationId,
    int StartedCallCount,
    int SkippedLeadCount,
    int FailedCallStartCount,
    IReadOnlyCollection<OutboundCampaignRunItem> Items,
    string? FailureReason = null,
    string? Message = null);

public sealed record OutboundCampaignRunItem(
    string? ProviderContactId,
    string Outcome,
    string? ProviderCallId = null,
    string? Status = null,
    string? TimeZoneBasis = null,
    string? TimeZone = null,
    bool Retryable = false);
