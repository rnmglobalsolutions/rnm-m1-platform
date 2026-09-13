using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Classes;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Classes;

public sealed class ClassReminderService
{
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly IClassSessionStore classSessionStore;
    private readonly ICrmAdapter crmAdapter;
    private readonly ClassNotificationService notificationService;
    private readonly IEventLogger eventLogger;

    public ClassReminderService(
        ITenantConfigurationProvider tenantConfigurationProvider,
        IClassSessionStore classSessionStore,
        ICrmAdapter crmAdapter,
        ClassNotificationService notificationService,
        IEventLogger eventLogger)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.classSessionStore = classSessionStore;
        this.crmAdapter = crmAdapter;
        this.notificationService = notificationService;
        this.eventLogger = eventLogger;
    }

    public async Task<ClassReminderRunResult> RunAsync(
        ClassReminderRunRequest request,
        CancellationToken cancellationToken)
    {
        await LogAsync(TelemetryEventNames.ClassReminderRunRequested, request.TenantId, request.CorrelationId, null, "requested", cancellationToken)
            .ConfigureAwait(false);

        var tenant = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
            .ConfigureAwait(false);
        if (tenant.Classes?.ReminderTemplates is null)
        {
            return new ClassReminderRunResult(request.TenantId, request.CorrelationId, 0, 0, 0, 0);
        }

        var reminders = await classSessionStore
            .GetDueRemindersAsync(
                request.TenantId,
                request.DueAt,
                Math.Clamp(request.MaxItems, 1, 100),
                cancellationToken)
            .ConfigureAwait(false);

        var sent = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var reminder in reminders)
        {
            if (!await classSessionStore.TryClaimReminderAsync(request.TenantId, reminder.RowKey, request.CorrelationId, cancellationToken).ConfigureAwait(false))
            {
                skipped++;
                continue;
            }

            var status = await ProcessReminderAsync(reminder, request.CorrelationId, tenant, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(status, ClassReminderStatuses.Sent, StringComparison.OrdinalIgnoreCase))
            {
                sent++;
            }
            else if (string.Equals(status, ClassReminderStatuses.Skipped, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
            }
            else
            {
                failed++;
            }

            await classSessionStore.MarkReminderAsync(request.TenantId, reminder.RowKey, status, request.CorrelationId, cancellationToken)
                .ConfigureAwait(false);
        }

        var result = new ClassReminderRunResult(request.TenantId, request.CorrelationId, reminders.Count, sent, skipped, failed);
        await LogAsync(TelemetryEventNames.ClassReminderRunCompleted, request.TenantId, request.CorrelationId, null, "completed", cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private async Task<string> ProcessReminderAsync(
        ClassReminderRecord reminder,
        string correlationId,
        TenantConfiguration tenant,
        CancellationToken cancellationToken)
    {
        var session = await classSessionStore
            .GetSessionAsync(reminder.TenantId, reminder.SessionId, cancellationToken)
            .ConfigureAwait(false);
        var registration = await classSessionStore
            .GetRegistrationAsync(reminder.TenantId, reminder.RegistrationId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null || registration is null)
        {
            await RecordReminderTimelineAsync(reminder, correlationId, ClassTimelineEventTypes.ReminderSkipped, "Class reminder skipped because session or registration was missing.", cancellationToken)
                .ConfigureAwait(false);
            return ClassReminderStatuses.Skipped;
        }

        var contact = await crmAdapter
            .FindContactByPhoneOrEmailAsync(
                new CrmContactLookupRequest(
                    reminder.TenantId,
                    correlationId,
                    registration.CustomerPhoneNumber,
                    registration.CustomerEmail),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(contact.Contact?.ConsentStatus, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase))
        {
            await RecordReminderTimelineAsync(reminder, correlationId, ClassTimelineEventTypes.ReminderSkipped, "Class reminder skipped because contact is opted out.", cancellationToken)
                .ConfigureAwait(false);
            return ClassReminderStatuses.Skipped;
        }

        if (contact.Contact is not null)
        {
            registration = registration with
            {
                ConsentStatus = contact.Contact.ConsentStatus
            };
        }

        var result = await notificationService
            .SendAsync(
                new ClassNotificationRequest(
                    reminder.TenantId,
                    correlationId,
                    session,
                    registration,
                    new ClassNotificationTemplateSet(
                        tenant.Classes?.ReminderTemplates?.SmsBodyTemplate,
                        tenant.Classes?.ReminderTemplates?.EmailSubjectTemplate,
                        tenant.Classes?.ReminderTemplates?.EmailBodyTemplate),
                    ClassNotificationKind.Reminder),
                tenant,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Sms?.Status is ConfirmationChannelStatus.Sent
            || result.Email?.Status is ConfirmationChannelStatus.Sent)
        {
            return ClassReminderStatuses.Sent;
        }

        if (result.Sms?.Status is ConfirmationChannelStatus.Failed
            || result.Email?.Status is ConfirmationChannelStatus.Failed)
        {
            await RecordReminderTimelineAsync(reminder, correlationId, ClassTimelineEventTypes.ReminderFailed, "Class reminder failed.", cancellationToken)
                .ConfigureAwait(false);
            return ClassReminderStatuses.Failed;
        }

        await RecordReminderTimelineAsync(reminder, correlationId, ClassTimelineEventTypes.ReminderSkipped, "Class reminder skipped.", cancellationToken)
            .ConfigureAwait(false);
        return ClassReminderStatuses.Skipped;
    }

    private async Task RecordReminderTimelineAsync(
        ClassReminderRecord reminder,
        string correlationId,
        string eventType,
        string summary,
        CancellationToken cancellationToken)
    {
        try
        {
            await crmAdapter
                .AddTimelineEventAsync(
                    new CrmTimelineEventRequest(
                        reminder.TenantId,
                        correlationId,
                        reminder.ProviderContactId,
                        ProviderBookingId: null,
                        eventType,
                        "ClassReminder",
                        summary,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["sessionId"] = reminder.SessionId,
                            ["registrationId"] = reminder.RegistrationId,
                            ["reminderKind"] = reminder.ReminderKind
                        }),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.CrmTimelineEventFailed, reminder.TenantId, correlationId, reminder.SessionId, eventType, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task LogAsync(
        string eventName,
        string tenantId,
        string correlationId,
        string? sessionId,
        string outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger
                .LogEventAsync(
                    eventName,
                    new SafeTelemetryProperties()
                        .Add("tenantId", tenantId)
                        .Add("correlationId", correlationId)
                        .Add("sessionId", sessionId)
                        .Add("outcome", outcome)
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Reminder telemetry is best-effort.
        }
    }
}
