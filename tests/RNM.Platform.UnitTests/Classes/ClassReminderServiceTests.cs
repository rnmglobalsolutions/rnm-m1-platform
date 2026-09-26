using RNM.Platform.Application.Classes;
using RNM.Platform.Application.Compliance;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Confirmations;
using Xunit;

namespace RNM.Platform.UnitTests.Classes;

public sealed class ClassReminderServiceTests
{
    [Fact]
    public async Task RunAsync_ClaimsDueReminderSendsNotificationAndMarksSent()
    {
        var session = CreateSession();
        var registration = CreateRegistration();
        var store = new FakeClassSessionStore(session);
        await store.UpsertRegistrationAsync(registration, CancellationToken.None);
        await store.ScheduleRemindersAsync(
            new ClassReminderScheduleRequest(
                "tenant-a",
                "corr-1",
                session,
                registration,
                [60]),
            CancellationToken.None);
        var crm = new FakeCrmAdapter
        {
            LookupResult = ContactWithConsent(CrmConsentStatuses.OptIn)
        };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = new ClassReminderService(
            new FakeTenantConfigurationProvider(),
            store,
            crm,
            new ClassNotificationService(sms, email, crm, logger, new AllowingSmsEligibilityGate()),
            logger,
            new SendWindowPolicy());

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", session.StartsAt.AddMinutes(-60)),
            CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Single(sms.Requests);
        Assert.Single(email.Requests);
        var reminders = await store.GetRemindersBySessionAsync("tenant-a", "session-a", CancellationToken.None);
        Assert.All(reminders, reminder => Assert.Equal(ClassReminderStatuses.Sent, reminder.Status));
    }

    [Fact]
    public async Task RunAsync_OutsideSendWindowSkipsSmsButStillSendsEmail()
    {
        var session = CreateSession(startsAt: new DateTimeOffset(2026, 7, 5, 9, 0, 0, TimeSpan.Zero));
        var registration = CreateRegistration();
        var store = new FakeClassSessionStore(session);
        await store.UpsertRegistrationAsync(registration, CancellationToken.None);
        await store.ScheduleRemindersAsync(
            new ClassReminderScheduleRequest(
                "tenant-a",
                "corr-1",
                session,
                registration,
                [60]),
            CancellationToken.None);
        var crm = new FakeCrmAdapter
        {
            LookupResult = ContactWithConsent(CrmConsentStatuses.OptIn)
        };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = new ClassReminderService(
            new FakeTenantConfigurationProvider(),
            store,
            crm,
            new ClassNotificationService(sms, email, crm, logger, new AllowingSmsEligibilityGate()),
            logger,
            new SendWindowPolicy());

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", session.StartsAt.AddMinutes(-60)),
            CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Empty(sms.Requests);
        Assert.Single(email.Requests);
        Assert.Contains(crm.TimelineEvents, evt =>
            evt.EventType == ClassTimelineEventTypes.ReminderSkipped
            && evt.Metadata.GetValueOrDefault("reason") == "outside_send_window");
    }

    [Fact]
    public async Task RunAsync_StaleReminderIsSkippedAndDoesNotSend()
    {
        var session = CreateSession(startsAt: new DateTimeOffset(2026, 7, 5, 18, 0, 0, TimeSpan.Zero));
        var registration = CreateRegistration();
        var store = new FakeClassSessionStore(session);
        await store.UpsertRegistrationAsync(registration, CancellationToken.None);
        await store.ScheduleRemindersAsync(
            new ClassReminderScheduleRequest(
                "tenant-a",
                "corr-1",
                session,
                registration,
                [120]),
            CancellationToken.None);
        var crm = new FakeCrmAdapter
        {
            LookupResult = new CrmContactLookupResult(true, "contact-1")
            {
                Contact = new CrmContactRecord(
                    "tenant-a",
                    "contact-1",
                    "+15551234567",
                    "jane@example.com",
                    "Jane Lead",
                    null,
                    new Dictionary<string, string>
                    {
                        [CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptIn
                    })
            }
        };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = new ClassReminderService(
            new FakeTenantConfigurationProvider(),
            store,
            crm,
            new ClassNotificationService(sms, email, crm, logger, new AllowingSmsEligibilityGate()),
            logger,
            new SendWindowPolicy());

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", new DateTimeOffset(2026, 7, 5, 17, 1, 0, TimeSpan.Zero)),
            CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(sms.Requests);
        Assert.Empty(email.Requests);
        Assert.Contains(crm.TimelineEvents, evt =>
            evt.EventType == ClassTimelineEventTypes.ReminderSkipped
            && evt.Metadata.GetValueOrDefault("reason") == "stale");
    }

    [Fact]
    public async Task RunAsync_ClassAlreadyStartedSkipsReminderAndDoesNotSend()
    {
        var session = CreateSession(startsAt: new DateTimeOffset(2026, 7, 5, 18, 0, 0, TimeSpan.Zero));
        var registration = CreateRegistration();
        var store = new FakeClassSessionStore(session);
        await store.UpsertRegistrationAsync(registration, CancellationToken.None);
        await store.ScheduleRemindersAsync(
            new ClassReminderScheduleRequest(
                "tenant-a",
                "corr-1",
                session,
                registration,
                [60]),
            CancellationToken.None);
        var crm = new FakeCrmAdapter
        {
            LookupResult = new CrmContactLookupResult(true, "contact-1")
            {
                Contact = new CrmContactRecord(
                    "tenant-a",
                    "contact-1",
                    "+15551234567",
                    "jane@example.com",
                    "Jane Lead",
                    null,
                    new Dictionary<string, string>
                    {
                        [CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptIn
                    })
            }
        };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = new ClassReminderService(
            new FakeTenantConfigurationProvider(),
            store,
            crm,
            new ClassNotificationService(sms, email, crm, logger, new AllowingSmsEligibilityGate()),
            logger,
            new SendWindowPolicy());

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", session.StartsAt.AddMinutes(1)),
            CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(sms.Requests);
        Assert.Empty(email.Requests);
        Assert.Contains(crm.TimelineEvents, evt =>
            evt.EventType == ClassTimelineEventTypes.ReminderSkipped
            && evt.Metadata.GetValueOrDefault("reason") == "class_started");
    }

    [Fact]
    public async Task RunAsync_CancelledClassSkipsReminderAndDoesNotSend()
    {
        var session = CreateSession() with { Status = ClassSessionStatuses.Cancelled };
        var registration = CreateRegistration();
        var store = new FakeClassSessionStore(session);
        await store.UpsertRegistrationAsync(registration, CancellationToken.None);
        await store.ScheduleRemindersAsync(
            new ClassReminderScheduleRequest("tenant-a", "corr-1", session, registration, [60]),
            CancellationToken.None);
        var crm = new FakeCrmAdapter { LookupResult = OptedInContact() };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = CreateService(store, crm, sms, email, logger);

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", session.StartsAt.AddMinutes(-60)),
            CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(sms.Requests);
        Assert.Empty(email.Requests);
        Assert.Contains(crm.TimelineEvents, evt =>
            evt.EventType == ClassTimelineEventTypes.ReminderSkipped
            && evt.Metadata.GetValueOrDefault("reason") == "session_not_published");
    }

    [Fact]
    public async Task RunAsync_LegacyClaimWithoutTimestampIsRecovered()
    {
        var session = CreateSession();
        var registration = CreateRegistration();
        var store = new FakeClassSessionStore(session);
        await store.UpsertRegistrationAsync(registration, CancellationToken.None);
        await store.ScheduleRemindersAsync(
            new ClassReminderScheduleRequest("tenant-a", "corr-1", session, registration, [60]),
            CancellationToken.None);
        var reminder = Assert.Single(await store.GetRemindersBySessionAsync("tenant-a", session.SessionId, CancellationToken.None));
        store.SetReminderState(reminder.RowKey, ClassReminderStatuses.Claimed);
        var crm = new FakeCrmAdapter { LookupResult = OptedInContact() };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = CreateService(store, crm, sms, email, logger);

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", reminder.DueAt),
            CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Single(sms.Requests);
        Assert.Single(email.Requests);
    }

    [Fact]
    public async Task ScheduleReminders_ReplaceExistingDoesNotResetSentReminder()
    {
        var session = CreateSession();
        var registration = CreateRegistration();
        var store = new FakeClassSessionStore(session);
        var schedule = new ClassReminderScheduleRequest("tenant-a", "corr-1", session, registration, [60]);
        await store.ScheduleRemindersAsync(schedule, CancellationToken.None);
        var reminder = Assert.Single(await store.GetRemindersBySessionAsync("tenant-a", session.SessionId, CancellationToken.None));
        await store.MarkReminderAsync("tenant-a", reminder.RowKey, ClassReminderStatuses.Sent, "corr-2", CancellationToken.None);

        await store.ScheduleRemindersAsync(schedule with { ReplaceExisting = true }, CancellationToken.None);

        var stored = Assert.Single(await store.GetRemindersBySessionAsync("tenant-a", session.SessionId, CancellationToken.None));
        Assert.Equal(ClassReminderStatuses.Sent, stored.Status);
    }

    [Fact]
    public async Task ScheduleReminders_ReplaceExistingReactivatesSessionChangeSkip()
    {
        var session = CreateSession();
        var registration = CreateRegistration();
        var store = new FakeClassSessionStore(session);
        var schedule = new ClassReminderScheduleRequest("tenant-a", "corr-1", session, registration, [60]);
        await store.ScheduleRemindersAsync(schedule, CancellationToken.None);
        await store.CancelPendingRemindersBySessionAsync("tenant-a", session.SessionId, "corr-2", CancellationToken.None);

        await store.ScheduleRemindersAsync(schedule with { ReplaceExisting = true }, CancellationToken.None);

        var stored = Assert.Single(await store.GetRemindersBySessionAsync("tenant-a", session.SessionId, CancellationToken.None));
        Assert.Equal(ClassReminderStatuses.Pending, stored.Status);
        Assert.Null(stored.SkipReason);
    }

    [Fact]
    public async Task RunAsync_AppointmentReminder_OutsideSendWindowSkipsSmsButStillSendsEmail()
    {
        var store = new FakeClassSessionStore();
        var startsAt = new DateTimeOffset(2026, 12, 15, 13, 0, 0, TimeSpan.Zero);
        await ScheduleAppointmentReminderAsync(store, startsAt, [60]);
        var crm = new FakeCrmAdapter { LookupResult = OptedInContact() };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = CreateService(store, crm, sms, email, logger);

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", startsAt.AddMinutes(-60)),
            CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Empty(sms.Requests);
        Assert.Single(email.Requests);
        Assert.Contains(crm.TimelineEvents, evt =>
            evt.EventType == CrmTimelineEventTypes.AppointmentReminderSkipped
            && evt.ProviderBookingId == "booking-1"
            && evt.Metadata.GetValueOrDefault("reason") == "outside_send_window");
        Assert.Contains(crm.TimelineEvents, evt =>
            evt.EventType == CrmTimelineEventTypes.AppointmentReminderSent
            && evt.Metadata.GetValueOrDefault("channel") == "email");
    }

    [Fact]
    public async Task RunAsync_AppointmentReminder_RendersAppointmentInContactTimezone()
    {
        var store = new FakeClassSessionStore();
        var startsAt = new DateTimeOffset(2026, 12, 15, 15, 0, 0, TimeSpan.Zero);
        await ScheduleAppointmentReminderAsync(store, startsAt, [60], timeZone: "America/Chicago");
        var crm = new FakeCrmAdapter
        {
            LookupResult = OptedInContact(
                new Dictionary<string, string>
                {
                    ["timeZone"] = "America/New_York"
                })
        };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = CreateService(store, crm, sms, email, logger);

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", startsAt.AddMinutes(-60)),
            CancellationToken.None);

        Assert.Equal(1, result.Sent);
        var smsBody = Assert.Single(sms.Requests).Body;
        var emailBody = Assert.Single(email.Requests).Body;
        Assert.Contains("America/New_York", smsBody);
        Assert.Contains("10:00", smsBody);
        Assert.Contains("America/New_York", emailBody);
        Assert.Contains("10:00", emailBody);
    }

    [Fact]
    public async Task RunAsync_AppointmentReminder_SendsTransactionalSmsWhenConsentIsUnknown()
    {
        var store = new FakeClassSessionStore();
        var startsAt = new DateTimeOffset(2026, 12, 15, 17, 0, 0, TimeSpan.Zero);
        await ScheduleAppointmentReminderAsync(store, startsAt, [60]);
        var crm = new FakeCrmAdapter
        {
            LookupResult = ContactWithConsent(CrmConsentStatuses.Unknown)
        };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = CreateService(store, crm, sms, email, logger);

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", startsAt.AddMinutes(-60)),
            CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Single(sms.Requests);
        Assert.Single(email.Requests);
        Assert.Contains(crm.TimelineEvents, evt =>
            evt.EventType == CrmTimelineEventTypes.AppointmentReminderSent
            && evt.Metadata.GetValueOrDefault("channel") == "sms");
    }

    [Fact]
    public async Task RunAsync_AppointmentReminder_OptedOutSkipsSmsAndEmail()
    {
        var store = new FakeClassSessionStore();
        var startsAt = new DateTimeOffset(2026, 12, 15, 17, 0, 0, TimeSpan.Zero);
        await ScheduleAppointmentReminderAsync(store, startsAt, [60]);
        var crm = new FakeCrmAdapter
        {
            LookupResult = ContactWithConsent(CrmConsentStatuses.OptedOut)
        };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = CreateService(store, crm, sms, email, logger);

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", startsAt.AddMinutes(-60)),
            CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(sms.Requests);
        Assert.Empty(email.Requests);
        Assert.Contains(crm.TimelineEvents, evt =>
            evt.EventType == CrmTimelineEventTypes.AppointmentReminderSkipped
            && evt.Metadata.GetValueOrDefault("reason") == "opted_out");
    }

    [Fact]
    public async Task RunAsync_ClassReminder_StillRequiresMarketingOptIn()
    {
        var session = CreateSession();
        var registration = CreateRegistration();
        var store = new FakeClassSessionStore(session);
        await store.UpsertRegistrationAsync(registration with { ConsentStatus = CrmConsentStatuses.Unknown }, CancellationToken.None);
        await store.ScheduleRemindersAsync(
            new ClassReminderScheduleRequest(
                "tenant-a",
                "corr-1",
                session,
                registration with { ConsentStatus = CrmConsentStatuses.Unknown },
                [60]),
            CancellationToken.None);
        var crm = new FakeCrmAdapter
        {
            LookupResult = ContactWithConsent(CrmConsentStatuses.Unknown)
        };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = CreateService(store, crm, sms, email, logger);

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", session.StartsAt.AddMinutes(-60)),
            CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(sms.Requests);
        Assert.Empty(email.Requests);
    }

    [Fact]
    public async Task RunAsync_AppointmentReminder_StaleReminderIsSkippedAndDoesNotSend()
    {
        var store = new FakeClassSessionStore();
        var startsAt = new DateTimeOffset(2026, 12, 15, 19, 0, 0, TimeSpan.Zero);
        await ScheduleAppointmentReminderAsync(store, startsAt, [120]);
        var crm = new FakeCrmAdapter { LookupResult = OptedInContact() };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = CreateService(store, crm, sms, email, logger);

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", startsAt.AddMinutes(-59)),
            CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(sms.Requests);
        Assert.Empty(email.Requests);
        Assert.Contains(crm.TimelineEvents, evt =>
            evt.EventType == CrmTimelineEventTypes.AppointmentReminderSkipped
            && evt.Metadata.GetValueOrDefault("reason") == "stale");
    }

    [Fact]
    public async Task RunAsync_AppointmentReminder_AlreadyStartedSkipsAndDoesNotSend()
    {
        var store = new FakeClassSessionStore();
        var startsAt = new DateTimeOffset(2026, 12, 15, 17, 0, 0, TimeSpan.Zero);
        await ScheduleAppointmentReminderAsync(store, startsAt, [60]);
        var crm = new FakeCrmAdapter { LookupResult = OptedInContact() };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = CreateService(store, crm, sms, email, logger);

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", startsAt.AddMinutes(1)),
            CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(sms.Requests);
        Assert.Empty(email.Requests);
        Assert.Contains(crm.TimelineEvents, evt =>
            evt.EventType == CrmTimelineEventTypes.AppointmentReminderSkipped
            && evt.Metadata.GetValueOrDefault("reason") == "appointment_started");
    }

    [Fact]
    public async Task RunAsync_AppointmentReminder_DoesNotDispatchTwiceAfterClaimAndMark()
    {
        var store = new FakeClassSessionStore();
        var startsAt = new DateTimeOffset(2026, 12, 15, 17, 0, 0, TimeSpan.Zero);
        await ScheduleAppointmentReminderAsync(store, startsAt, [60]);
        var crm = new FakeCrmAdapter { LookupResult = OptedInContact() };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var logger = new FakeEventLogger();
        var service = CreateService(store, crm, sms, email, logger);
        var runRequest = new ClassReminderRunRequest("tenant-a", "corr-2", startsAt.AddMinutes(-60));

        var first = await service.RunAsync(runRequest, CancellationToken.None);
        var second = await service.RunAsync(runRequest, CancellationToken.None);

        Assert.Equal(1, first.Sent);
        Assert.Equal(0, second.Scanned);
        Assert.Single(sms.Requests);
        Assert.Single(email.Requests);
    }

    private static ClassSessionRecord CreateSession() =>
        CreateSession(new DateTimeOffset(2026, 7, 5, 15, 0, 0, TimeSpan.Zero));

    private static ClassSessionRecord CreateSession(DateTimeOffset startsAt) =>
        new(
            "tenant-a",
            "session-a",
            "Financial Master Class",
            startsAt,
            startsAt.AddHours(1),
            "America/Chicago",
            "https://zoom.us/j/123",
            100,
            "campaign-a",
            new Dictionary<string, string>());

    private static ClassRegistrationRecord CreateRegistration() =>
        new(
            "tenant-a",
            "registration-a",
            "session-a",
            "contact-1",
            "Jane Lead",
            "+15551234567",
            "jane@example.com",
            "WebRegistration",
            "campaign-a",
            CrmConsentStatuses.OptIn,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>());

    private static ClassReminderService CreateService(
        FakeClassSessionStore store,
        FakeCrmAdapter crm,
        FakeSmsSender sms,
        FakeEmailSender email,
        FakeEventLogger logger) =>
        new(
            new FakeTenantConfigurationProvider(),
            store,
            crm,
            new ClassNotificationService(sms, email, crm, logger, new AllowingSmsEligibilityGate()),
            logger,
            new SendWindowPolicy());

    private static Task ScheduleAppointmentReminderAsync(
        FakeClassSessionStore store,
        DateTimeOffset startsAt,
        IReadOnlyCollection<int> offsets,
        string timeZone = "America/Chicago") =>
        store.ScheduleAppointmentRemindersAsync(
            new AppointmentReminderScheduleRequest(
                "tenant-a",
                "corr-1",
                "contact-1",
                "booking-1",
                "Jane Lead",
                "+15551234567",
                "jane@example.com",
                "Appointment",
                startsAt,
                startsAt.AddMinutes(30),
                timeZone,
                "https://meet.google.com/abc-defg-hij",
                offsets,
                new Dictionary<string, string>
                {
                    ["serviceNeed"] = "Consultation",
                    ["propertyType"] = "residential",
                    ["serviceAddress"] = "123 Main St",
                    ["zipCode"] = "75001"
                }),
            CancellationToken.None);

    private static CrmContactLookupResult OptedInContact(IReadOnlyDictionary<string, string>? attributes = null)
    {
        var mergedAttributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributes ?? new Dictionary<string, string>())
        {
            mergedAttributes[attribute.Key] = attribute.Value;
        }

        mergedAttributes[CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptIn;
        return ContactWithConsent(CrmConsentStatuses.OptIn, mergedAttributes);
    }

    private static CrmContactLookupResult ContactWithConsent(
        string consentStatus,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        var mergedAttributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CrmContactAttributeNames.ConsentStatus] = consentStatus,
            [CrmContactAttributeNames.SmsConsentStatus] = consentStatus,
            [CrmContactAttributeNames.EmailConsentStatus] = consentStatus
        };
        foreach (var attribute in attributes ?? new Dictionary<string, string>())
        {
            mergedAttributes[attribute.Key] = attribute.Value;
        }

        return new CrmContactLookupResult(true, "contact-1")
        {
            Contact = new CrmContactRecord(
                "tenant-a",
                "contact-1",
                "+15551234567",
                "jane@example.com",
                "Jane Lead",
                "75001",
                mergedAttributes)
        };
    }
}
