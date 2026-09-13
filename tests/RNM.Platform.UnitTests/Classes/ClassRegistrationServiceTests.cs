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
using Xunit;

namespace RNM.Platform.UnitTests.Classes;

public sealed class ClassRegistrationServiceTests
{
    [Fact]
    public async Task RegisterAsync_CreatesContactRegistrationConfirmationAndReminders()
    {
        var session = CreateSession();
        var store = new FakeClassSessionStore(session);
        var crm = new FakeCrmAdapter();
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var service = CreateRegistrationService(store, crm, sms, email);

        var result = await service.RegisterAsync(
            new ClassRegistrationRequest(
                "tenant-a",
                "corr-1",
                session.SessionId,
                "Jane Lead",
                "5551234567",
                "jane@example.com")
            {
                CampaignId = "campaign-a",
                MarketingConsentGranted = true,
                Attributes = new Dictionary<string, string> { ["intent"] = "buyer" }
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Registration);
        Assert.Equal(CrmConsentStatuses.OptIn, result.Registration.ConsentStatus);
        Assert.Single(sms.Requests);
        Assert.Single(email.Requests);
        Assert.Equal(2, store.ScheduledReminders.Count);
        Assert.Equal("masterclass", crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.SourceFunnel]);
        Assert.Contains(crm.TimelineEvents, evt => evt.EventType == ClassTimelineEventTypes.RegistrationCreated);
    }

    [Fact]
    public async Task RegisterAsync_DoesNotReverseOptedOutFromWebRegistration()
    {
        var session = CreateSession();
        var store = new FakeClassSessionStore(session);
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
                        [CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptedOut
                    })
            }
        };
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var service = CreateRegistrationService(store, crm, sms, email);

        var result = await service.RegisterAsync(
            new ClassRegistrationRequest(
                "tenant-a",
                "corr-1",
                session.SessionId,
                "Jane Lead",
                "5551234567",
                "jane@example.com")
            {
                MarketingConsentGranted = true
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(CrmConsentStatuses.OptedOut, crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.ConsentStatus]);
        Assert.Empty(sms.Requests);
        Assert.Single(email.Requests);
        Assert.Contains(
            crm.TimelineEvents,
            evt => evt.EventType == CrmTimelineEventTypes.MarketingConsentWebRegistrationBlockedOptedOut);
    }

    private static ClassRegistrationService CreateRegistrationService(
        FakeClassSessionStore store,
        FakeCrmAdapter crm,
        FakeSmsSender sms,
        FakeEmailSender email)
    {
        var logger = new FakeEventLogger();
        return new ClassRegistrationService(
            new FakeTenantConfigurationProvider(),
            store,
            crm,
            new ClassNotificationService(sms, email, crm, logger),
            logger);
    }

    private static ClassSessionRecord CreateSession() =>
        new(
            "tenant-a",
            "session-a",
            "Financial Master Class",
            new DateTimeOffset(2026, 6, 10, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero),
            "America/Chicago",
            "https://zoom.us/j/123",
            100,
            "campaign-a",
            new Dictionary<string, string>());

}
