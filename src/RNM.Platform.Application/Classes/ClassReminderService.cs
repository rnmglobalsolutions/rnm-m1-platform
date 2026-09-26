using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Compliance;
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
    private readonly ISendWindowPolicy sendWindowPolicy;

    public ClassReminderService(
        ITenantConfigurationProvider tenantConfigurationProvider,
        IClassSessionStore classSessionStore,
        ICrmAdapter crmAdapter,
        ClassNotificationService notificationService,
        IEventLogger eventLogger,
        ISendWindowPolicy sendWindowPolicy)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.classSessionStore = classSessionStore;
        this.crmAdapter = crmAdapter;
        this.notificationService = notificationService;
        this.eventLogger = eventLogger;
        this.sendWindowPolicy = sendWindowPolicy;
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

            var status = await ProcessReminderAsync(reminder, request.CorrelationId, request.DueAt, tenant, cancellationToken)
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
        DateTimeOffset asOf,
        TenantConfiguration tenant,
        CancellationToken cancellationToken)
    {
        if (string.Equals(reminder.EffectiveTargetType, ReminderTargetTypes.Appointment, StringComparison.OrdinalIgnoreCase))
        {
            return await ProcessAppointmentReminderAsync(reminder, correlationId, asOf, tenant, cancellationToken)
                .ConfigureAwait(false);
        }

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

        if (!string.Equals(session.Status, ClassSessionStatuses.Published, StringComparison.OrdinalIgnoreCase))
        {
            await RecordReminderTimelineAsync(reminder, correlationId, ClassTimelineEventTypes.ReminderSkipped, "Class reminder skipped because the session is not published.", "session_not_published", cancellationToken)
                .ConfigureAwait(false);
            return ClassReminderStatuses.Skipped;
        }

        if (reminder.StartsAt.HasValue && reminder.StartsAt.Value != session.StartsAt)
        {
            await RecordReminderTimelineAsync(reminder, correlationId, ClassTimelineEventTypes.ReminderSkipped, "Class reminder skipped because the session time changed.", "session_changed", cancellationToken)
                .ConfigureAwait(false);
            return ClassReminderStatuses.Skipped;
        }

        if (asOf >= session.StartsAt)
        {
            await RecordReminderTimelineAsync(reminder, correlationId, ClassTimelineEventTypes.ReminderSkipped, "Class reminder skipped because the class already started.", "class_started", cancellationToken)
                .ConfigureAwait(false);
            return ClassReminderStatuses.Skipped;
        }

        var stalenessCutoff = TimeSpan.FromMinutes(tenant.Classes?.EffectiveReminderStalenessCutoffMinutes ?? 60);
        if (asOf - reminder.DueAt > stalenessCutoff)
        {
            await RecordReminderTimelineAsync(reminder, correlationId, ClassTimelineEventTypes.ReminderSkipped, "Class reminder skipped because it was stale.", "stale", cancellationToken)
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
        var policyContact = contact.Contact ?? new CrmContactRecord(
            reminder.TenantId,
            registration.ProviderContactId,
            registration.CustomerPhoneNumber,
            registration.CustomerEmail,
            registration.CustomerName,
            ZipCode: null,
            registration.Attributes);
        var sendWindow = sendWindowPolicy.Evaluate(
            policyContact,
            tenant.TimeZone,
            tenant.Voice?.Outbound?.TcpaWindow,
            asOf);
        ConfirmationFailureReason? smsSuppressionReason = sendWindow.IsAllowed
            ? null
            : ConfirmationFailureReason.OutsideSendWindow;
        if (smsSuppressionReason is not null)
        {
            await RecordReminderTimelineAsync(
                    reminder,
                    correlationId,
                    ClassTimelineEventTypes.ReminderSkipped,
                    "Class reminder SMS skipped because it is outside the send window.",
                    "outside_send_window",
                    cancellationToken,
                    sendWindow.TimeZoneBasis,
                    sendWindow.TimeZoneId,
                    sendWindow.LocalHour.ToString())
                .ConfigureAwait(false);
        }

        if (contact.Contact is not null)
        {
            registration = registration with
            {
                ConsentStatus = contact.Contact.ConsentStatus,
                Attributes = MergeAttributes(registration.Attributes, contact.Contact.Attributes)
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
                    ClassNotificationKind.Reminder)
                {
                    SmsSuppressionReason = smsSuppressionReason
                },
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

    private async Task<string> ProcessAppointmentReminderAsync(
        ClassReminderRecord reminder,
        string correlationId,
        DateTimeOffset asOf,
        TenantConfiguration tenant,
        CancellationToken cancellationToken)
    {
        if (reminder.StartsAt is not { } startsAt
            || string.IsNullOrWhiteSpace(reminder.ProviderContactId)
            || string.IsNullOrWhiteSpace(reminder.EffectiveTargetId))
        {
            await RecordAppointmentReminderTimelineAsync(reminder, correlationId, CrmTimelineEventTypes.AppointmentReminderSkipped, "Appointment reminder skipped because required target data was missing.", "missing_target", cancellationToken)
                .ConfigureAwait(false);
            return ClassReminderStatuses.Skipped;
        }

        if (asOf >= startsAt)
        {
            await RecordAppointmentReminderTimelineAsync(reminder, correlationId, CrmTimelineEventTypes.AppointmentReminderSkipped, "Appointment reminder skipped because the appointment already started.", "appointment_started", cancellationToken)
                .ConfigureAwait(false);
            return ClassReminderStatuses.Skipped;
        }

        var appointmentReminderConfig = tenant.Communication.AppointmentReminders;
        if (appointmentReminderConfig?.IsEnabled is not true
            || appointmentReminderConfig.ReminderStalenessCutoffMinutes is not { } stalenessCutoffMinutes)
        {
            await RecordAppointmentReminderTimelineAsync(reminder, correlationId, CrmTimelineEventTypes.AppointmentReminderSkipped, "Appointment reminder skipped because appointment reminders are disabled.", "reminders_disabled", cancellationToken)
                .ConfigureAwait(false);
            return ClassReminderStatuses.Skipped;
        }

        var stalenessCutoff = TimeSpan.FromMinutes(stalenessCutoffMinutes);
        if (asOf - reminder.DueAt > stalenessCutoff)
        {
            await RecordAppointmentReminderTimelineAsync(reminder, correlationId, CrmTimelineEventTypes.AppointmentReminderSkipped, "Appointment reminder skipped because it was stale.", "stale", cancellationToken)
                .ConfigureAwait(false);
            return ClassReminderStatuses.Skipped;
        }

        var contact = await crmAdapter
            .FindContactByPhoneOrEmailAsync(
                new CrmContactLookupRequest(
                    reminder.TenantId,
                    correlationId,
                    reminder.CustomerPhoneNumber,
                    reminder.CustomerEmail),
                cancellationToken)
            .ConfigureAwait(false);
        var attributes = MergeAttributes(reminder.Attributes, contact.Contact?.Attributes);
        var policyContact = contact.Contact ?? new CrmContactRecord(
            reminder.TenantId,
            reminder.ProviderContactId,
            reminder.CustomerPhoneNumber,
            reminder.CustomerEmail,
            reminder.CustomerName,
            GetAttribute(attributes, "zipCode"),
            attributes);
        var sendWindow = sendWindowPolicy.Evaluate(
            policyContact,
            tenant.TimeZone,
            tenant.Voice?.Outbound?.TcpaWindow,
            asOf);
        ConfirmationFailureReason? smsSuppressionReason = sendWindow.IsAllowed
            ? null
            : ConfirmationFailureReason.OutsideSendWindow;
        if (smsSuppressionReason is not null)
        {
            await RecordAppointmentReminderTimelineAsync(
                    reminder,
                    correlationId,
                    CrmTimelineEventTypes.AppointmentReminderSkipped,
                    "Appointment reminder SMS skipped because it is outside the send window.",
                    "outside_send_window",
                    cancellationToken,
                    sendWindow.TimeZoneBasis,
                    sendWindow.TimeZoneId,
                    sendWindow.LocalHour.ToString())
                .ConfigureAwait(false);
        }

        var result = await notificationService
            .SendAppointmentReminderAsync(
                new AppointmentReminderNotificationRequest(
                    reminder.TenantId,
                    correlationId,
                    reminder.ProviderContactId,
                    reminder.EffectiveTargetId,
                    tenant.BusinessName,
                    contact.Contact?.Name ?? reminder.CustomerName,
                    contact.Contact?.PhoneNumber ?? reminder.CustomerPhoneNumber,
                    contact.Contact?.Email ?? reminder.CustomerEmail,
                    GetAttribute(attributes, "serviceNeed") ?? GetAttribute(attributes, "serviceType"),
                    GetAttribute(attributes, "propertyType"),
                    GetAttribute(attributes, "serviceAddress"),
                    contact.Contact?.ZipCode ?? GetAttribute(attributes, "zipCode"),
                    GetAttribute(attributes, "urgency"),
                    reminder.BookingLabel,
                    startsAt,
                    reminder.EndsAt,
                    sendWindow.TimeZoneId,
                    reminder.OnlineMeetingUrl,
                    appointmentReminderConfig.Templates,
                    contact.Contact?.ConsentStatus ?? CrmConsentStatuses.Unknown,
                    attributes)
                {
                    SmsSuppressionReason = smsSuppressionReason
                },
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
            await RecordAppointmentReminderTimelineAsync(reminder, correlationId, CrmTimelineEventTypes.AppointmentReminderFailed, "Appointment reminder failed.", "send_failed", cancellationToken)
                .ConfigureAwait(false);
            return ClassReminderStatuses.Failed;
        }

        await RecordAppointmentReminderTimelineAsync(reminder, correlationId, CrmTimelineEventTypes.AppointmentReminderSkipped, "Appointment reminder skipped.", "notification_skipped", cancellationToken)
            .ConfigureAwait(false);
        return ClassReminderStatuses.Skipped;
    }

    private async Task RecordReminderTimelineAsync(
        ClassReminderRecord reminder,
        string correlationId,
        string eventType,
        string summary,
        string reason,
        CancellationToken cancellationToken,
        string? timezoneBasis = null,
        string? timezone = null,
        string? localHour = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sessionId"] = reminder.SessionId,
            ["registrationId"] = reminder.RegistrationId,
            ["reminderKind"] = reminder.ReminderKind,
            ["reason"] = reason
        };
        if (!string.IsNullOrWhiteSpace(timezoneBasis))
        {
            metadata["timezoneBasis"] = timezoneBasis;
        }

        if (!string.IsNullOrWhiteSpace(timezone))
        {
            metadata["timezone"] = timezone;
        }

        if (!string.IsNullOrWhiteSpace(localHour))
        {
            metadata["localHour"] = localHour;
        }

        await RecordReminderTimelineAsync(reminder, correlationId, eventType, summary, metadata, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RecordReminderTimelineAsync(
        ClassReminderRecord reminder,
        string correlationId,
        string eventType,
        string summary,
        CancellationToken cancellationToken)
    {
        await RecordReminderTimelineAsync(
                reminder,
                correlationId,
                eventType,
                summary,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sessionId"] = reminder.SessionId,
                    ["registrationId"] = reminder.RegistrationId,
                    ["reminderKind"] = reminder.ReminderKind
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RecordReminderTimelineAsync(
        ClassReminderRecord reminder,
        string correlationId,
        string eventType,
        string summary,
        IReadOnlyDictionary<string, string> metadata,
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
                        metadata),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.CrmTimelineEventFailed, reminder.TenantId, correlationId, reminder.SessionId, eventType, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RecordAppointmentReminderTimelineAsync(
        ClassReminderRecord reminder,
        string correlationId,
        string eventType,
        string summary,
        string reason,
        CancellationToken cancellationToken,
        string? timezoneBasis = null,
        string? timezone = null,
        string? localHour = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["targetType"] = reminder.EffectiveTargetType,
            ["targetId"] = reminder.EffectiveTargetId,
            ["reminderKind"] = reminder.ReminderKind,
            ["reason"] = reason
        };
        if (!string.IsNullOrWhiteSpace(timezoneBasis))
        {
            metadata["timezoneBasis"] = timezoneBasis;
        }

        if (!string.IsNullOrWhiteSpace(timezone))
        {
            metadata["timezone"] = timezone;
        }

        if (!string.IsNullOrWhiteSpace(localHour))
        {
            metadata["localHour"] = localHour;
        }

        try
        {
            var result = await crmAdapter
                .AddTimelineEventAsync(
                    new CrmTimelineEventRequest(
                        reminder.TenantId,
                        correlationId,
                        reminder.ProviderContactId,
                        reminder.EffectiveTargetId,
                        eventType,
                        "AppointmentReminder",
                        summary,
                        metadata),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                await LogAsync(TelemetryEventNames.CrmTimelineEventFailed, reminder.TenantId, correlationId, reminder.EffectiveTargetId, eventType, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.CrmTimelineEventFailed, reminder.TenantId, correlationId, reminder.EffectiveTargetId, eventType, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static IReadOnlyDictionary<string, string> MergeAttributes(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string>? second)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in first.Concat(second ?? new Dictionary<string, string>()))
        {
            if (!string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
            {
                merged[item.Key.Trim()] = item.Value.Trim();
            }
        }

        return merged;
    }

    private static string? GetAttribute(IReadOnlyDictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

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
