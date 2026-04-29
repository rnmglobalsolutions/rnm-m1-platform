namespace RNM.Platform.Domain.Appointments;

public sealed record Appointment(
    string ProviderAppointmentId,
    AppointmentSlot Slot,
    AppointmentStatus Status);
