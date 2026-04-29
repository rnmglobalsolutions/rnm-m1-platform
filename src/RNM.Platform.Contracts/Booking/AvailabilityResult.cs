namespace RNM.Platform.Contracts.Booking;

public sealed record AvailabilityResult(
    bool HasAvailability,
    IReadOnlyCollection<AvailableSlotDto> Slots,
    string? Message);

public sealed record AvailableSlotDto(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? TechnicianName);
