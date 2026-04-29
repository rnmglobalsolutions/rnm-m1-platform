namespace RNM.Platform.Contracts.Booking;

public sealed record AvailabilityRequest(
    string TenantId,
    string ServiceType,
    string? PreferredWindow,
    string TimeZone);
