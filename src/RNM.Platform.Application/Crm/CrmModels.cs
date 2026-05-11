using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Qualification;

namespace RNM.Platform.Application.Crm;

public sealed record CrmContactLookupRequest(
    string TenantId,
    string CorrelationId,
    string? PhoneNumber,
    string? Email);

public sealed record CrmContactLookupResult(
    bool Found,
    string? ProviderContactId);

public sealed record CrmContactUpsertRequest(
    string TenantId,
    string VerticalId,
    string CorrelationId,
    string? ProviderContactId,
    string? PhoneNumber,
    string? Email,
    string? Name,
    string? ZipCode,
    IReadOnlyDictionary<string, string> Attributes);

public sealed record CrmContactUpsertResult(
    bool Succeeded,
    bool Created,
    string? ProviderContactId,
    CrmFailureReason? FailureReason = null,
    string? Message = null);

public sealed record CrmInteractionNoteRequest(
    string TenantId,
    string CorrelationId,
    string ProviderContactId,
    string Note);

public sealed record CrmTagRequest(
    string TenantId,
    string CorrelationId,
    string ProviderContactId,
    IReadOnlyCollection<string> Tags);

public sealed record CrmBookingLinkRequest(
    string TenantId,
    string CorrelationId,
    string ProviderContactId,
    string ProviderBookingId);

public sealed record CrmOperationResult(
    bool Succeeded,
    CrmFailureReason? FailureReason = null,
    string? Message = null);

public sealed record CrmContactEnsureRequest(
    string TenantId,
    string VerticalId,
    string CorrelationId,
    QualificationResult QualificationResult);

public sealed record CrmPostBookingSyncRequest(
    string TenantId,
    string VerticalId,
    string CorrelationId,
    QualificationResult QualificationResult,
    BookingDecisionResult BookingDecision,
    string ProviderContactId,
    string? ServiceType = null);

public sealed record CrmSyncRequest(
    string TenantId,
    string VerticalId,
    string CorrelationId,
    QualificationResult QualificationResult,
    BookingDecisionResult BookingDecision,
    string? ServiceType = null);

public sealed record CrmSyncResult(
    CrmSyncState State,
    string? ProviderContactId,
    CrmFailureReason? FailureReason = null)
{
    public bool Succeeded => State is CrmSyncState.Succeeded;
}

public enum CrmSyncState
{
    Succeeded = 0,
    Skipped = 1,
    Failed = 2
}

public enum CrmFailureReason
{
    BookingNotCompleted = 0,
    ContactUpsertFailed = 1,
    MissingProviderContactId = 2,
    NoteFailed = 3,
    TagsFailed = 4,
    BookingLinkFailed = 5,
    AdapterFailure = 6,
    MissingContactIdentifier = 7
}
