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
                ConsentCapturedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ConsentTextVersion = "class-registration-v1",
                Attributes = new Dictionary<string, string> { ["intent"] = "buyer" }
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Registration);
        Assert.Equal(CrmConsentStatuses.OptIn, result.Registration.ConsentStatus);
        Assert.Single(sms.Requests);
        Assert.Single(email.Requests);
        Assert.Equal(2, store.ScheduledReminders.Count);
        Assert.Equal("class_registration", crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.SourceFunnel]);
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
                MarketingConsentGranted = true,
                ConsentCapturedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ConsentTextVersion = "class-registration-v1"
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(CrmConsentStatuses.OptedOut, crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.ConsentStatus]);
        Assert.Empty(sms.Requests);
        Assert.Empty(email.Requests);
        Assert.Contains(
            crm.TimelineEvents,
            evt => evt.EventType == CrmTimelineEventTypes.MarketingConsentWebRegistrationBlockedOptedOut);
    }

    [Fact]
    public async Task RegisterAsync_DuplicateDoesNotRepeatSideEffects()
    {
        var session = CreateSession();
        var store = new FakeClassSessionStore(session);
        var crm = new FakeCrmAdapter();
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var service = CreateRegistrationService(store, crm, sms, email);
        var request = CreateConsentRequest(session.SessionId, "Jane Lead", "5551234567", "jane@example.com");

        var first = await service.RegisterAsync(request, CancellationToken.None);
        var duplicate = await service.RegisterAsync(request with { CorrelationId = "corr-2" }, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(duplicate.Succeeded);
        Assert.True(duplicate.Duplicate);
        Assert.Single(sms.Requests);
        Assert.Single(email.Requests);
        Assert.Equal(2, store.ScheduledReminders.Count);
    }

    [Fact]
    public async Task RegisterAsync_IncompleteDuplicateResumesNotificationsWithoutResettingReminders()
    {
        var session = CreateSession();
        var store = new FakeClassSessionStore(session);
        var crm = new FakeCrmAdapter();
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var service = CreateRegistrationService(store, crm, sms, email);
        var request = CreateConsentRequest(session.SessionId, "Jane Lead", "5551234567", "jane@example.com");

        var first = await service.RegisterAsync(request, CancellationToken.None);
        store.ClearRegistrationNotificationStatus(first.Registration!.RegistrationId);
        sms.Requests.Clear();
        email.Requests.Clear();

        var resumed = await service.RegisterAsync(request with { CorrelationId = "corr-retry" }, CancellationToken.None);

        Assert.True(resumed.Succeeded);
        Assert.True(resumed.Duplicate);
        Assert.Single(sms.Requests);
        Assert.Single(email.Requests);
        Assert.Equal(2, store.ScheduledReminders.Count);
    }

    [Fact]
    public async Task RegisterAsync_RequestAttributesCannotOverrideReservedValues()
    {
        var session = CreateSession();
        var store = new FakeClassSessionStore(session);
        var crm = new FakeCrmAdapter();
        var service = CreateRegistrationService(store, crm, new FakeSmsSender(), new FakeEmailSender());
        var request = CreateConsentRequest(session.SessionId, "Jane Lead", "5551234567", "jane@example.com") with
        {
            CampaignId = "trusted-campaign",
            Attributes = new Dictionary<string, string>
            {
                [CrmContactAttributeNames.CampaignId] = "attacker-campaign",
                [CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptedOut,
                [CrmContactAttributeNames.SourceRegistrationId] = "attacker-registration",
                ["intent"] = "buyer"
            }
        };

        var result = await service.RegisterAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("trusted-campaign", crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.CampaignId]);
        Assert.Equal(CrmConsentStatuses.OptIn, crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.ConsentStatus]);
        Assert.NotEqual("attacker-registration", crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.SourceRegistrationId]);
        Assert.Equal("buyer", crm.LastUpsertRequest?.Attributes["intent"]);
    }

    [Fact]
    public async Task RegisterAsync_ExplicitConsentWithoutEvidenceIsRejected()
    {
        var session = CreateSession();
        var store = new FakeClassSessionStore(session);
        var crm = new FakeCrmAdapter();
        var service = CreateRegistrationService(store, crm, new FakeSmsSender(), new FakeEmailSender());

        var result = await service.RegisterAsync(
            new ClassRegistrationRequest("tenant-a", "corr-1", session.SessionId, "Jane Lead", "5551234567", null)
            {
                MarketingConsentGranted = true
            },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ClassFailureReason.InvalidRequest, result.FailureReason);
        Assert.Null(crm.LastUpsertRequest);
    }

    [Fact]
    public async Task RegisterAsync_ExistingOptInIsPreservedWithoutRecordingANewGrant()
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
                        [CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptIn
                    })
            }
        };
        var sms = new FakeSmsSender();
        var service = CreateRegistrationService(store, crm, sms, new FakeEmailSender());
        var request = new ClassRegistrationRequest(
            "tenant-a",
            "corr-1",
            session.SessionId,
            "Jane Lead",
            "5551234567",
            "jane@example.com");

        var result = await service.RegisterAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(CrmConsentStatuses.OptIn, crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.ConsentStatus]);
        Assert.Single(sms.Requests);
        Assert.Contains(crm.TimelineEvents, evt =>
            evt.EventType == CrmTimelineEventTypes.MarketingConsentWebRegistrationDeclined
            && evt.Metadata.GetValueOrDefault("explicitConsent") == bool.FalseString);
        Assert.DoesNotContain(crm.TimelineEvents, evt =>
            evt.EventType == CrmTimelineEventTypes.MarketingConsentWebRegistrationGranted);
    }

    [Fact]
    public async Task RegisterAsync_DuplicateWithNewExplicitConsentPersistsGrantAndSendsPreviouslySkippedSms()
    {
        var session = CreateSession();
        var store = new FakeClassSessionStore(session);
        var crm = new FakeCrmAdapter();
        var sms = new FakeSmsSender();
        var email = new FakeEmailSender();
        var service = CreateRegistrationService(store, crm, sms, email);
        var request = new ClassRegistrationRequest(
            "tenant-a",
            "corr-1",
            session.SessionId,
            "Jane Lead",
            "5551234567",
            "jane@example.com");

        var first = await service.RegisterAsync(request, CancellationToken.None);
        var duplicate = await service.RegisterAsync(
            request with
            {
                CorrelationId = "corr-2",
                MarketingConsentGranted = true,
                ConsentCapturedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ConsentTextVersion = "class-registration-v1"
            },
            CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(duplicate.Succeeded);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(CrmConsentStatuses.OptIn, duplicate.Registration?.ConsentStatus);
        Assert.Single(sms.Requests);
        Assert.Single(email.Requests);
        Assert.Equal(CrmConsentStatuses.OptIn, crm.LastUpsertRequest?.Attributes[CrmContactAttributeNames.ConsentStatus]);
        Assert.Contains(crm.TimelineEvents, evt =>
            evt.EventType == CrmTimelineEventTypes.MarketingConsentWebRegistrationGranted
            && evt.Metadata.GetValueOrDefault("explicitConsent") == bool.TrueString);
    }

    [Fact]
    public async Task RegisterAsync_ClosedSessionIsRejected()
    {
        var session = CreateSession() with { Status = ClassSessionStatuses.Closed };
        var crm = new FakeCrmAdapter();
        var service = CreateRegistrationService(
            new FakeClassSessionStore(session),
            crm,
            new FakeSmsSender(),
            new FakeEmailSender());

        var result = await service.RegisterAsync(
            CreateConsentRequest(session.SessionId, "Jane Lead", "5551234567", "jane@example.com"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ClassFailureReason.SessionNotOpen, result.FailureReason);
        Assert.Null(crm.LastUpsertRequest);
    }

    [Fact]
    public async Task RegisterAsync_ReleasesCapacityWhenCrmWriteFails()
    {
        var session = CreateSession() with { Capacity = 1 };
        var store = new FakeClassSessionStore(session);
        var crm = new FakeCrmAdapter
        {
            UpsertResult = new CrmContactUpsertResult(false, false, null)
        };
        var service = CreateRegistrationService(store, crm, new FakeSmsSender(), new FakeEmailSender());

        var failed = await service.RegisterAsync(
            CreateConsentRequest(session.SessionId, "Jane Lead", "5551234567", "jane@example.com"),
            CancellationToken.None);
        crm.UpsertResult = new CrmContactUpsertResult(true, true, "contact-2");
        var retried = await service.RegisterAsync(
            CreateConsentRequest(session.SessionId, "Second Lead", "5552223333", "second@example.com") with { CorrelationId = "corr-2" },
            CancellationToken.None);

        Assert.False(failed.Succeeded);
        Assert.True(retried.Succeeded);
    }

    [Fact]
    public async Task RegisterAsync_FailedDeliverySchedulesDurableRetries()
    {
        var session = CreateSession();
        var store = new FakeClassSessionStore(session);
        var crm = new FakeCrmAdapter();
        var scheduler = new FakeConfirmationRetryScheduler();
        var logger = new FakeEventLogger();
        var sms = new FakeSmsSender { Result = new SmsSendResult(false) };
        var email = new FakeEmailSender { Result = new EmailSendResult(false) };
        var service = new ClassRegistrationService(
            new FakeTenantConfigurationProvider(),
            store,
            crm,
            new ClassNotificationService(
                sms,
                email,
                crm,
                logger,
                scheduler),
            logger);

        var result = await service.RegisterAsync(
            CreateConsentRequest(session.SessionId, "Jane Lead", "5551234567", "jane@example.com"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, scheduler.Requests.Count);
        Assert.All(scheduler.Requests, retry => Assert.Equal(result.Registration?.RegistrationId, retry.ClassRegistrationId));
        var stored = await store.GetRegistrationAsync("tenant-a", result.Registration!.RegistrationId, CancellationToken.None);
        Assert.Equal("RetryScheduled", stored?.ConfirmationSmsStatus);
        Assert.Equal("RetryScheduled", stored?.ConfirmationEmailStatus);

        var duplicate = await service.RegisterAsync(
            CreateConsentRequest(session.SessionId, "Jane Lead", "5551234567", "jane@example.com") with { CorrelationId = "corr-2" },
            CancellationToken.None);

        Assert.True(duplicate.Duplicate);
        Assert.Single(sms.Requests);
        Assert.Single(email.Requests);
        Assert.Equal(2, scheduler.Requests.Count);
    }

    [Fact]
    public async Task RegisterAsync_FailedDeliveryWithoutQueuedRetryCanResume()
    {
        var session = CreateSession();
        var store = new FakeClassSessionStore(session);
        var crm = new FakeCrmAdapter();
        var sms = new FakeSmsSender { Result = new SmsSendResult(false) };
        var email = new FakeEmailSender { Result = new EmailSendResult(false) };
        var service = CreateRegistrationService(store, crm, sms, email);
        var request = CreateConsentRequest(session.SessionId, "Jane Lead", "5551234567", "jane@example.com");

        var first = await service.RegisterAsync(request, CancellationToken.None);
        var duplicate = await service.RegisterAsync(request with { CorrelationId = "corr-2" }, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(2, sms.Requests.Count);
        Assert.Equal(2, email.Requests.Count);
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

    private static ClassRegistrationRequest CreateConsentRequest(
        string sessionId,
        string name,
        string phone,
        string email) =>
        new("tenant-a", "corr-1", sessionId, name, phone, email)
        {
            MarketingConsentGranted = true,
            ConsentCapturedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ConsentTextVersion = "class-registration-v1"
        };

    private static ClassSessionRecord CreateSession() =>
        new(
            "tenant-a",
            "session-a",
            "Financial Master Class",
            DateTimeOffset.UtcNow.AddDays(10),
            DateTimeOffset.UtcNow.AddDays(10).AddHours(1),
            "America/Chicago",
            "https://zoom.us/j/123",
            100,
            "campaign-a",
            new Dictionary<string, string>());

}
