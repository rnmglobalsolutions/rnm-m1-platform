using RNM.Platform.Application.Qualification;

namespace RNM.Platform.Application.Booking;

public sealed record BookingAvailabilityRequest(
    string TenantId,
    string VerticalId,
    string CorrelationId,
    string? ServiceType,
    string? PreferredWindow,
    string TimeZone);

public sealed record BookingAvailabilityResult(
    bool HasAvailability,
    IReadOnlyCollection<AvailableSlot> Slots,
    BookingFailureReason? FailureReason = null,
    string? Message = null)
{
    public bool Succeeded { get; init; } = true;
}

public sealed record AvailableSlot(
    string? SlotId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Label = null);

public sealed record CreateBookingRequest(
    string TenantId,
    string VerticalId,
    string CorrelationId,
    QualifiedLeadData LeadData,
    AvailableSlot Slot,
    string? ServiceType,
    string? PreferredWindow);

public sealed record CreateBookingResult(
    bool Succeeded,
    string? ProviderBookingId,
    BookingFailureReason? FailureReason = null,
    string? Message = null);

public enum BookingFailureReason
{
    QualificationNotQualified = 0,
    MissingRequiredFields = 1,
    OutOfServiceArea = 2,
    InvalidInput = 3,
    NeedsEscalation = 4,
    NoAvailability = 5,
    AdapterFailure = 6,
    SlotUnavailable = 7
}

public sealed record BookingRequest(
    string TenantId,
    string VerticalId,
    string CorrelationId,
    string TimeZone,
    QualificationResult QualificationResult,
    string? ServiceType = null,
    string? PreferredWindow = null,
    AvailableSlot? SelectedSlot = null,
    bool AutoSelectFirstAvailableAfterConfirmation = false);

public sealed record BookingDecisionResult(
    BookingDecisionState State,
    BookingFailureReason? FailureReason,
    IReadOnlyCollection<AvailableSlot> AvailableSlots,
    AvailableSlot? SelectedSlot,
    string? ProviderBookingId)
{
    public bool IsBooked => State is BookingDecisionState.Booked;
}

public enum BookingDecisionState
{
    Refused = 0,
    NoAvailability = 1,
    AvailabilityFound = 2,
    Booked = 3,
    Failed = 4
}
