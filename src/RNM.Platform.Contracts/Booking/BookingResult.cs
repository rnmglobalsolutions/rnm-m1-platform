namespace RNM.Platform.Contracts.Booking;

public sealed record BookingResult(
    bool Succeeded,
    string? ProviderBookingId,
    string? Message);
