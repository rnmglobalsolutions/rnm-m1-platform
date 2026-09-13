using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Classes;

namespace RNM.Platform.Application.Classes;

public sealed class ClassReportService
{
    private readonly IClassSessionStore classSessionStore;
    private readonly IEventLogger eventLogger;

    public ClassReportService(
        IClassSessionStore classSessionStore,
        IEventLogger eventLogger)
    {
        this.classSessionStore = classSessionStore;
        this.eventLogger = eventLogger;
    }

    public async Task<ClassReportResult> GetReportAsync(
        ClassReportRequest request,
        CancellationToken cancellationToken)
    {
        await LogAsync(TelemetryEventNames.ClassReportRequested, request, "requested", cancellationToken)
            .ConfigureAwait(false);

        var registrations = await classSessionStore
            .GetRegistrationsBySessionAsync(request.TenantId, request.SessionId, cancellationToken)
            .ConfigureAwait(false);
        var reminders = await classSessionStore
            .GetRemindersBySessionAsync(request.TenantId, request.SessionId, cancellationToken)
            .ConfigureAwait(false);

        var smsSent = registrations.Count(registration =>
            string.Equals(registration.ConfirmationSmsStatus, ConfirmationChannelStatus.Sent.ToString(), StringComparison.OrdinalIgnoreCase));
        var emailSent = registrations.Count(registration =>
            string.Equals(registration.ConfirmationEmailStatus, ConfirmationChannelStatus.Sent.ToString(), StringComparison.OrdinalIgnoreCase));
        var remindersSent = reminders.Count(reminder =>
            string.Equals(reminder.Status, ClassReminderStatuses.Sent, StringComparison.OrdinalIgnoreCase));
        var optedOut = registrations.Count(registration =>
            string.Equals(registration.ConsentStatus, Crm.CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase));

        var result = new ClassReportResult(
            request.TenantId,
            request.CorrelationId,
            request.SessionId,
            registrations.Count,
            emailSent,
            smsSent,
            remindersSent,
            optedOut,
            $"Registered {registrations.Count} leads, sent {emailSent} email confirmations and {smsSent} SMS confirmations, sent {remindersSent} reminders.");

        await LogAsync(TelemetryEventNames.ClassReportCompleted, request, "completed", cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private async Task LogAsync(
        string eventName,
        ClassReportRequest request,
        string outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger
                .LogEventAsync(
                    eventName,
                    new SafeTelemetryProperties()
                        .Add("tenantId", request.TenantId)
                        .Add("correlationId", request.CorrelationId)
                        .Add("sessionId", request.SessionId)
                        .Add("outcome", outcome)
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Class reporting telemetry is best-effort.
        }
    }
}

