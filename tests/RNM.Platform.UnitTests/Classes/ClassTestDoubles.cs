using RNM.Platform.Application.Classes;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Classes;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;

namespace RNM.Platform.UnitTests.Classes;

internal sealed class FakeClassSessionStore : IClassSessionStore
{
    private readonly Dictionary<string, ClassSessionRecord> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClassRegistrationRecord> registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClassReminderRecord> reminders = new(StringComparer.Ordinal);

    public FakeClassSessionStore(params ClassSessionRecord[] initialSessions)
    {
        foreach (var session in initialSessions)
        {
            sessions[session.SessionId] = session;
        }
    }

    public List<ClassReminderRecord> ScheduledReminders { get; } = [];

    public List<ClassNotificationStatusUpdate> NotificationStatusUpdates { get; } = [];

    public Task<ClassSessionUpsertResult> UpsertSessionAsync(
        ClassSessionUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var session = new ClassSessionRecord(
            request.TenantId,
            request.SessionId,
            request.Title,
            request.StartsAt,
            request.EndsAt,
            request.TimeZone,
            request.ZoomUrl,
            request.Capacity,
            request.CampaignId,
            request.Attributes);
        sessions[session.SessionId] = session;
        return Task.FromResult(new ClassSessionUpsertResult(true, session));
    }

    public Task<ClassSessionRecord?> GetSessionAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task<ClassRegistrationRecord?> GetRegistrationAsync(
        string tenantId,
        string registrationId,
        CancellationToken cancellationToken)
    {
        registrations.TryGetValue(registrationId, out var registration);
        return Task.FromResult(registration);
    }

    public Task<IReadOnlyCollection<ClassRegistrationRecord>> GetRegistrationsBySessionAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyCollection<ClassRegistrationRecord>>(
            registrations.Values
                .Where(registration => registration.TenantId == tenantId && registration.SessionId == sessionId)
                .ToArray());
    }

    public Task<ClassRegistrationRecord?> FindRegistrationAsync(
        string tenantId,
        string sessionId,
        string providerContactId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<ClassRegistrationRecord?>(
            registrations.Values.FirstOrDefault(registration =>
                registration.TenantId == tenantId
                && registration.SessionId == sessionId
                && registration.ProviderContactId == providerContactId));
    }

    public Task<ClassRegistrationRecord> UpsertRegistrationAsync(
        ClassRegistrationRecord registration,
        CancellationToken cancellationToken)
    {
        registrations[registration.RegistrationId] = registration;
        return Task.FromResult(registration);
    }

    public Task UpdateRegistrationNotificationStatusAsync(
        ClassNotificationStatusUpdate update,
        CancellationToken cancellationToken)
    {
        NotificationStatusUpdates.Add(update);
        if (registrations.TryGetValue(update.RegistrationId, out var registration))
        {
            registrations[update.RegistrationId] = registration with
            {
                ConfirmationSmsStatus = update.SmsStatus,
                ConfirmationEmailStatus = update.EmailStatus
            };
        }

        return Task.CompletedTask;
    }

    public Task ScheduleRemindersAsync(
        ClassReminderScheduleRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var offset in request.ReminderOffsetsMinutes)
        {
            var reminder = new ClassReminderRecord(
                request.TenantId,
                $"reminder-{offset}-{request.Registration.RegistrationId}",
                request.Registration.RegistrationId,
                request.Session.SessionId,
                request.Registration.ProviderContactId,
                $"{offset}m_before",
                request.Session.StartsAt.AddMinutes(-offset),
                ClassReminderStatuses.Pending,
                request.CorrelationId);
            reminders[reminder.RowKey] = reminder;
            ScheduledReminders.Add(reminder);
        }

        return Task.CompletedTask;
    }

    public Task ScheduleAppointmentRemindersAsync(
        AppointmentReminderScheduleRequest request,
        CancellationToken cancellationToken)
    {
        if (request.StartsAt <= DateTimeOffset.UtcNow)
        {
            return Task.CompletedTask;
        }

        foreach (var offset in request.ReminderOffsetsMinutes)
        {
            var dueAt = request.StartsAt.AddMinutes(-offset);
            if (dueAt <= DateTimeOffset.UtcNow)
            {
                continue;
            }

            var reminder = new ClassReminderRecord(
                request.TenantId,
                $"appointment-reminder-{offset}-{request.ProviderBookingId}",
                RegistrationId: string.Empty,
                SessionId: string.Empty,
                request.ProviderContactId,
                $"{offset}m_before",
                dueAt,
                ClassReminderStatuses.Pending,
                request.CorrelationId)
            {
                TargetType = ReminderTargetTypes.Appointment,
                TargetId = request.ProviderBookingId,
                CustomerName = request.CustomerName,
                CustomerPhoneNumber = request.CustomerPhoneNumber,
                CustomerEmail = request.CustomerEmail,
                BookingLabel = request.BookingLabel,
                StartsAt = request.StartsAt,
                EndsAt = request.EndsAt,
                TimeZone = request.TimeZone,
                OnlineMeetingUrl = request.OnlineMeetingUrl,
                Attributes = request.Attributes
            };
            reminders[reminder.RowKey] = reminder;
            ScheduledReminders.Add(reminder);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<ClassReminderRecord>> GetDueRemindersAsync(
        string tenantId,
        DateTimeOffset dueAt,
        int maxItems,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyCollection<ClassReminderRecord>>(
            reminders.Values
                .Where(reminder =>
                    reminder.TenantId == tenantId
                    && reminder.DueAt <= dueAt
                    && reminder.Status == ClassReminderStatuses.Pending)
                .Take(maxItems)
                .ToArray());
    }

    public Task<IReadOnlyCollection<ClassReminderRecord>> GetRemindersBySessionAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyCollection<ClassReminderRecord>>(
            reminders.Values
                .Where(reminder => reminder.TenantId == tenantId && reminder.SessionId == sessionId)
                .ToArray());
    }

    public Task<bool> TryClaimReminderAsync(
        string tenantId,
        string rowKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!reminders.TryGetValue(rowKey, out var reminder) || reminder.Status != ClassReminderStatuses.Pending)
        {
            return Task.FromResult(false);
        }

        reminders[rowKey] = reminder with { Status = ClassReminderStatuses.Claimed };
        return Task.FromResult(true);
    }

    public Task MarkReminderAsync(
        string tenantId,
        string rowKey,
        string status,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (reminders.TryGetValue(rowKey, out var reminder))
        {
            reminders[rowKey] = reminder with { Status = status };
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeCrmAdapter : ICrmAdapter
{
    public CrmContactLookupResult LookupResult { get; init; } = new(false, null);

    public CrmContactUpsertRequest? LastUpsertRequest { get; private set; }

    public List<CrmTimelineEventRequest> TimelineEvents { get; } = [];

    public Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(
        CrmContactLookupRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(LookupResult);

    public Task<CrmContactUpsertResult> UpsertContactAsync(
        CrmContactUpsertRequest request,
        CancellationToken cancellationToken)
    {
        LastUpsertRequest = request;
        return Task.FromResult(new CrmContactUpsertResult(true, string.IsNullOrWhiteSpace(request.ProviderContactId), request.ProviderContactId ?? "contact-1"));
    }

    public Task<CrmOperationResult> AddInteractionNoteAsync(CrmInteractionNoteRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));

    public Task<CrmOperationResult> ApplyTagsAsync(CrmTagRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));

    public Task<CrmOperationResult> LinkBookingToContactAsync(CrmBookingLinkRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));

    public Task<CrmOperationResult> AddTimelineEventAsync(CrmTimelineEventRequest request, CancellationToken cancellationToken)
    {
        TimelineEvents.Add(request);
        return Task.FromResult(new CrmOperationResult(true));
    }

    public Task<CrmOperationResult> MarkFollowUpRequiredAsync(CrmFollowUpRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));

    public Task<CrmLeadQueryResult> GetLeadsByStatusAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmLeadQueryResult(true, []));

    public Task<CrmLeadQueryResult> GetLeadsByCampaignAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmLeadQueryResult(true, []));

    public Task<CrmNextLeadToCallResult> GetNextLeadToCallAsync(CrmNextLeadToCallRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmNextLeadToCallResult(true, null));

    public Task<CrmOperationResult> RecordOutboundAttemptAsync(CrmOutboundAttemptRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));

    public Task<CrmOperationResult> MarkLeadReactivatedAsync(CrmLeadReactivationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));

    public Task<CrmOperationResult> MarkOptOutAsync(CrmOptOutRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));
}

internal sealed class FakeSmsSender : ISmsSender
{
    public List<SmsMessageRequest> Requests { get; } = [];

    public SmsSendResult Result { get; init; } = new(true, "sms-1");

    public Task<SmsSendResult> SendSmsAsync(SmsMessageRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

internal sealed class FakeEmailSender : IEmailSender
{
    public List<EmailMessageRequest> Requests { get; } = [];

    public EmailSendResult Result { get; init; } = new(true, "email-1");

    public Task<EmailSendResult> SendEmailAsync(EmailMessageRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

internal sealed class FakeEventLogger : IEventLogger
{
    public List<string> EventNames { get; } = [];

    public Task LogEventAsync(
        string eventName,
        IReadOnlyDictionary<string, string> properties,
        CancellationToken cancellationToken)
    {
        EventNames.Add(eventName);
        return Task.CompletedTask;
    }
}

internal sealed class FakeTenantConfigurationProvider : ITenantConfigurationProvider
{
    public ClassAutomationConfiguration? Classes { get; init; } =
        new(
            new ClassNotificationTemplateConfiguration(
                "Class {{classTitle}} {{classDate}} {{classTime}} {{zoomUrl}}",
                "Class {{classTitle}}",
                "Hi {{customerName}}, join {{zoomUrl}}"),
            new ClassNotificationTemplateConfiguration(
                "Reminder {{classTitle}} {{zoomUrl}}",
                "Reminder {{classTitle}}",
                "Reminder: {{zoomUrl}}"),
            [1440, 60],
            ["https://example.com"]);

    public Task<TenantConfiguration> GetTenantConfigurationAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new TenantConfiguration(
            new TenantId(tenantId),
            new VerticalId("financial-education"),
            "RNM",
            "America/Chicago",
            new ServiceAreaConfiguration(["*"], [], null),
            new ProviderConfiguration("AzureTable", "GoogleCalendar", "Twilio", "SendGrid"),
            new SecretNameConfiguration("crm", "booking", "voice", "twilioSid", "twilioToken", "email"),
            new CommunicationConfiguration(
                "+15550001111",
                "info@example.com",
                new ConfirmationTemplateConfiguration("booking sms"),
                AppointmentReminders: new AppointmentReminderConfiguration(
                    new ConfirmationTemplateConfiguration(
                        "Appt reminder: {{businessName}} {{bookingDate}} {{bookingTime}} {{timeZone}} {{onlineMeetingUrl}}",
                        "Appt reminder {{bookingDate}}",
                        "Hi {{customerName}}, appointment at {{bookingDate}} {{bookingTime}} {{timeZone}} {{onlineMeetingUrl}}"),
                    [1440, 60],
                    60)),
            Classes: Classes));
    }
}
