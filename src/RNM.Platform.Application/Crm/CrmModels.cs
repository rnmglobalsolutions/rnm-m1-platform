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

public sealed record CrmContactRecord(
    string TenantId,
    string ProviderContactId,
    string? PhoneNumber,
    string? Email,
    string? Name,
    string? ZipCode,
    IReadOnlyDictionary<string, string> Attributes)
{
    public string ConsentStatus => GetAttribute(CrmContactAttributeNames.ConsentStatus) ?? CrmConsentStatuses.Unknown;

    public string? OutboundLeadStatus => GetAttribute(CrmContactAttributeNames.LeadStatus);

    public string? CampaignId => GetAttribute(CrmContactAttributeNames.CampaignId);

    public int OutboundAttemptCount =>
        int.TryParse(GetAttribute(CrmContactAttributeNames.OutboundAttemptCount), out var count)
            ? count
            : 0;

    public DateTimeOffset? LastContactedAt => TryGetDateTimeOffset(CrmContactAttributeNames.LastContactedAt);

    public DateTimeOffset? NextFollowUpAt => TryGetDateTimeOffset(CrmContactAttributeNames.NextFollowUpAt);

    private string? GetAttribute(string name) =>
        Attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private DateTimeOffset? TryGetDateTimeOffset(string name) =>
        DateTimeOffset.TryParse(GetAttribute(name), out var value) ? value : null;
}

public sealed record CrmLeadQueryRequest(
    string TenantId,
    string CorrelationId,
    string Value);

public sealed record CrmNextLeadToCallRequest(
    string TenantId,
    string CorrelationId,
    string CampaignId)
{
    public int MaxOutboundAttempts { get; init; } = 3;

    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CrmLeadQueryResult(
    bool Succeeded,
    IReadOnlyCollection<CrmContactRecord> Leads,
    CrmFailureReason? FailureReason = null,
    string? Message = null);

public sealed record CrmNextLeadToCallResult(
    bool Succeeded,
    CrmContactRecord? Lead,
    CrmFailureReason? FailureReason = null,
    string? Message = null)
{
    public bool Found => Lead is not null;
}

public sealed record CrmOutboundAttemptRequest(
    string TenantId,
    string CorrelationId,
    string ProviderContactId,
    string Outcome)
{
    public DateTimeOffset AttemptedAt { get; init; } = DateTimeOffset.UtcNow;

    public string? Note { get; init; }
}

public sealed record CrmLeadReactivationRequest(
    string TenantId,
    string CorrelationId,
    string ProviderContactId);

public sealed record CrmOptOutRequest(
    string TenantId,
    string CorrelationId,
    string? ProviderContactId)
{
    public string Source { get; init; } = "Inbound";

    public string? Reason { get; init; }

    public string? PhoneNumber { get; init; }

    public string? Email { get; init; }
}

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
    MissingContactIdentifier = 7,
    ConsentOptedOut = 8,
    ContactNotFound = 9
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
    public const string OutboundAttemptRecorded = "outbound.attempt_recorded";
    public const string LeadReactivated = "lead.reactivated";
    public const string ConsentOptedOut = "consent.opted_out";
    public const string SmsSent = "sms.sent";
    public const string EmailSent = "email.sent";
}

public static class CrmContactAttributeNames
{
    public const string LeadSource = "leadSource";
    public const string CampaignId = "campaignId";
    public const string LeadStatus = "leadStatus";
    public const string OutboundAttemptCount = "outboundAttemptCount";
    public const string LastContactedAt = "lastContactedAt";
    public const string NextFollowUpAt = "nextFollowUpAt";
    public const string Intent = "intent";
    public const string TargetPropertyAddress = "targetPropertyAddress";
    public const string AssignedAgent = "assignedAgent";
    public const string ConsentStatus = "consentStatus";
}

public static class CrmOutboundLeadStatuses
{
    public const string New = "new";
    public const string Contacted = "contacted";
    public const string Qualified = "qualified";
    public const string AppointmentBooked = "appointment_booked";
    public const string Reactivated = "reactivated";
    public const string Nurturing = "nurturing";
    public const string NotContacted = "not_contacted";
    public const string Dead = "dead";
}

public static class CrmIntentValues
{
    public const string Buyer = "buyer";
    public const string Seller = "seller";
    public const string Renter = "renter";
    public const string Unknown = "unknown";
}

public static class CrmConsentStatuses
{
    public const string OptIn = "opt_in";
    public const string Unknown = "unknown";
    public const string OptedOut = "opted_out";
}
