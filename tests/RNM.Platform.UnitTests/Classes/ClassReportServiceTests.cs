using RNM.Platform.Application.Classes;
using RNM.Platform.Application.Confirmations;
using Xunit;

namespace RNM.Platform.UnitTests.Classes;

public sealed class ClassReportServiceTests
{
    [Fact]
    public async Task GetReportAsync_ComputesCountsFromRegistrationsAndReminders()
    {
        var session = new ClassSessionRecord(
            "tenant-a",
            "session-a",
            "Financial Master Class",
            DateTimeOffset.UtcNow.AddDays(1),
            null,
            "America/Chicago",
            "https://zoom.us/j/123",
            100,
            "campaign-a",
            new Dictionary<string, string>());
        var store = new FakeClassSessionStore(session);
        await store.UpsertRegistrationAsync(
            new ClassRegistrationRecord(
                "tenant-a",
                "registration-a",
                "session-a",
                "contact-1",
                "Jane Lead",
                "+15551234567",
                "jane@example.com",
                "WebRegistration",
                "campaign-a",
                "opt_in",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string>())
            {
                ConfirmationSmsStatus = ConfirmationChannelStatus.Sent.ToString(),
                ConfirmationEmailStatus = ConfirmationChannelStatus.Sent.ToString()
            },
            CancellationToken.None);
        await store.ScheduleRemindersAsync(
            new ClassReminderScheduleRequest(
                "tenant-a",
                "corr-1",
                session,
                (await store.GetRegistrationsBySessionAsync("tenant-a", "session-a", CancellationToken.None)).Single(),
                [60]),
            CancellationToken.None);
        var reminders = await store.GetRemindersBySessionAsync("tenant-a", "session-a", CancellationToken.None);
        await store.MarkReminderAsync("tenant-a", reminders.Single().RowKey, ClassReminderStatuses.Sent, "corr-2", CancellationToken.None);
        var service = new ClassReportService(store, new FakeEventLogger());

        var result = await service.GetReportAsync(
            new ClassReportRequest("tenant-a", "corr-3", "session-a"),
            CancellationToken.None);

        Assert.Equal(1, result.Registrations);
        Assert.Equal(1, result.ConfirmedEmailsSent);
        Assert.Equal(1, result.ConfirmedSmsSent);
        Assert.Equal(1, result.RemindersSent);
        Assert.Contains("Registered 1 leads", result.Summary);
    }
}

