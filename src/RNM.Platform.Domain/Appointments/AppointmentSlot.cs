namespace RNM.Platform.Domain.Appointments;

public sealed record AppointmentSlot(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string TimeZone,
    string? TechnicianName);
