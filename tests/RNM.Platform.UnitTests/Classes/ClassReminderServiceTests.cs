using RNM.Platform.Application.Classes;
using RNM.Platform.Application.Crm;
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
            new ClassNotificationService(sms, email, crm, logger),
            logger);

        var result = await service.RunAsync(
            new ClassReminderRunRequest("tenant-a", "corr-2", session.StartsAt),
            CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Single(sms.Requests);
        Assert.Single(email.Requests);
        var reminders = await store.GetRemindersBySessionAsync("tenant-a", "session-a", CancellationToken.None);
        Assert.All(reminders, reminder => Assert.Equal(ClassReminderStatuses.Sent, reminder.Status));
    }

    private static ClassSessionRecord CreateSession() =>
        new(
            "tenant-a",
            "session-a",
            "Financial Master Class",
            DateTimeOffset.UtcNow.AddHours(2),
            DateTimeOffset.UtcNow.AddHours(3),
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
}

