namespace RNM.Platform.Contracts.Booking;

public sealed record CreateBookingRequest(
    string TenantId,
    string LeadName,
    string PhoneNumber,
    string? Email,
    string? Address,
    AvailableSlotDto Slot,
    string? Notes);
