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
    IReadOnlyDictionary<string, string> Attributes)
{
    public string LeadStatus { get; init; } = CrmLeadStatuses.Qualified;

    public bool NeedsFollowUp { get; init; }

    public string? FollowUpReason { get; init; }

    public DateTimeOffset? FollowUpAt { get; init; }

    public DateTimeOffset LastInteractionAt { get; init; } = DateTimeOffset.UtcNow;
}

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
    string ProviderBookingId)
{
    public string? VerticalId { get; init; }

    public string? BookingProvider { get; init; }

    public string Source { get; init; } = "InboundVoice";

    public string? CustomerName { get; init; }

    public string? PhoneNumber { get; init; }

    public string? Email { get; init; }

    public string? ServiceType { get; init; }

    public string? PropertyType { get; init; }

    public string? ServiceAddress { get; init; }

    public string? ZipCode { get; init; }

    public string? Urgency { get; init; }

    public string? PreferredWindow { get; init; }

    public string? BookingLabel { get; init; }

    public DateTimeOffset? StartsAt { get; init; }

    public DateTimeOffset? EndsAt { get; init; }

    public string? TimeZone { get; init; }

    public string? BookingState { get; init; }

    public string? QualificationState { get; init; }

    public string? ServiceAreaState { get; init; }
}

public sealed record CrmOperationResult(
    bool Succeeded,
    CrmFailureReason? FailureReason = null,
    string? Message = null);

public sealed record CrmTimelineEventRequest(
    string TenantId,
    string CorrelationId,
    string? ProviderContactId,
    string? ProviderBookingId,
    string EventType,
    string Source,
    string Summary,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record CrmFollowUpRequest(
    string TenantId,
    string CorrelationId,
    string ProviderContactId,
    string Reason)
{
    public string LeadStatus { get; init; } = CrmLeadStatuses.NeedsFollowUp;

    public DateTimeOffset? FollowUpAt { get; init; }

    public DateTimeOffset LastInteractionAt { get; init; } = DateTimeOffset.UtcNow;
}

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
    string? ServiceType = null)
{
    public string? PreferredWindow { get; init; }

    public string? TimeZone { get; init; }

    public string? BookingProvider { get; init; }

    public string Source { get; init; } = "InboundVoice";
}

public sealed record CrmSyncRequest(
    string TenantId,
    string VerticalId,
    string CorrelationId,
    QualificationResult QualificationResult,
    BookingDecisionResult BookingDecision,
    string? ServiceType = null)
{
    public string? PreferredWindow { get; init; }

    public string? TimeZone { get; init; }

    public string? BookingProvider { get; init; }

    public string Source { get; init; } = "InboundVoice";
}

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

public static class CrmLeadStatuses
{
    public const string New = "New";
    public const string Qualified = "Qualified";
    public const string AppointmentScheduled = "AppointmentScheduled";
    public const string NeedsFollowUp = "NeedsFollowUp";
    public const string Booked = "Booked";
    public const string Lost = "Lost";
}

public static class CrmTimelineEventTypes
{
    public const string LeadQualified = "lead.qualified";
    public const string BookingCreated = "booking.created";
    public const string FollowUpRequired = "followup.required";
}
