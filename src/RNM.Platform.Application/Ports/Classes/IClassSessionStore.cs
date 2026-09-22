using RNM.Platform.Application.Classes;

namespace RNM.Platform.Application.Ports.Classes;

public interface IClassSessionStore
{
    Task<ClassSessionUpsertResult> UpsertSessionAsync(
        ClassSessionUpsertRequest request,
        CancellationToken cancellationToken);

    Task<ClassSessionRecord?> GetSessionAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken);

    Task<ClassRegistrationRecord?> GetRegistrationAsync(
        string tenantId,
        string registrationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ClassRegistrationRecord>> GetRegistrationsBySessionAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken);

    Task<ClassRegistrationRecord?> FindRegistrationAsync(
        string tenantId,
        string sessionId,
        string providerContactId,
        CancellationToken cancellationToken);

    Task<ClassRegistrationRecord> UpsertRegistrationAsync(
        ClassRegistrationRecord registration,
        CancellationToken cancellationToken);

    Task<ClassRegistrationReservationResult> TryReserveRegistrationAsync(
        string tenantId,
        string sessionId,
        string registrationId,
        string correlationId,
        CancellationToken cancellationToken);

    Task ReleaseRegistrationReservationAsync(
        string tenantId,
        string sessionId,
        string registrationId,
        string correlationId,
        CancellationToken cancellationToken);

    Task UpdateRegistrationNotificationStatusAsync(
        ClassNotificationStatusUpdate update,
        CancellationToken cancellationToken);

    Task ScheduleRemindersAsync(
        ClassReminderScheduleRequest request,
        CancellationToken cancellationToken);

    Task CancelPendingRemindersBySessionAsync(
        string tenantId,
        string sessionId,
        string correlationId,
        CancellationToken cancellationToken);

    Task ScheduleAppointmentRemindersAsync(
        AppointmentReminderScheduleRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ClassReminderRecord>> GetDueRemindersAsync(
        string tenantId,
        DateTimeOffset dueAt,
        int maxItems,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ClassReminderRecord>> GetRemindersBySessionAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken);

    Task<bool> TryClaimReminderAsync(
        string tenantId,
        string rowKey,
        string correlationId,
        CancellationToken cancellationToken);

    Task MarkReminderAsync(
        string tenantId,
        string rowKey,
        string status,
        string correlationId,
        CancellationToken cancellationToken);
}
