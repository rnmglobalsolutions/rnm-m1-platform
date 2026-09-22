namespace RNM.Platform.Application.LeadIntake;

public sealed record InboundLeadIntakeRequest(
    string TenantId,
    string VerticalId,
    string CorrelationId,
    string Source,
    string ExternalEventId,
    string ExternalContactId,
    string? CustomerName,
    string? CustomerPhoneNumber,
    string? CustomerEmail,
    string? CampaignId,
    bool MarketingConsentGranted,
    DateTimeOffset? ConsentCapturedAt,
    string? ConsentTextVersion,
    IReadOnlyDictionary<string, string> Attributes,
    bool ScheduleFollowUp);

public sealed record InboundLeadIntakeResult(
    bool Succeeded,
    bool Duplicate,
    bool Processing,
    string? ProviderContactId = null,
    bool FollowUpRequested = false,
    bool BusinessNotificationQueued = false,
    string? FailureCode = null);

public sealed record InboundLeadReceiptClaimRequest(
    string TenantId,
    string Provider,
    string ExternalEventId,
    string CorrelationId,
    DateTimeOffset Now);

public sealed record InboundLeadReceiptClaimResult(
    InboundLeadReceiptClaimState State,
    string RowKey,
    string? LeaseId = null,
    string? ProviderContactId = null);

public enum InboundLeadReceiptClaimState
{
    Acquired = 0,
    Completed = 1,
    InProgress = 2
}

public sealed record InboundLeadReceiptCompletionRequest(
    string TenantId,
    string RowKey,
    string LeaseId,
    string CorrelationId,
    string ProviderContactId,
    DateTimeOffset CompletedAt);

public sealed record InboundLeadReceiptFailureRequest(
    string TenantId,
    string RowKey,
    string LeaseId,
    string CorrelationId,
    string FailureCode,
    DateTimeOffset FailedAt);

