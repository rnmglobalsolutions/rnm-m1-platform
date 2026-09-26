using RNM.Platform.Application.Compliance;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.UnitTests.Classes;
using Xunit;

namespace RNM.Platform.UnitTests.Compliance;

public sealed class SmsEligibilityGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EvaluateAsync_RetryAfterSmsOptOut_IsDeniedAndRecorded()
    {
        var crm = CreateCrm(CrmConsentStatuses.OptedOut);
        var gate = CreateGate(crm);

        var result = await gate.EvaluateAsync(
            CreateRequest(SmsMessageCategory.BookingConfirmation) with
            {
                IsRetry = true,
                OriginalRequestedAt = Now.AddMinutes(-5)
            },
            CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Equal(SmsEligibilitySkipReason.ContactOptedOut, result.SkipReason);
        Assert.Contains(crm.TimelineEvents, item =>
            item.EventType == CrmTimelineEventTypes.SmsSkipped
            && item.Metadata.GetValueOrDefault("reason") == nameof(SmsEligibilitySkipReason.ContactOptedOut));
    }

    [Fact]
    public async Task EvaluateAsync_ImmediateBookingOutsideWindow_IsAllowed()
    {
        var gate = CreateGate(CreateCrm(CrmConsentStatuses.Unknown));

        var result = await gate.EvaluateAsync(
            CreateRequest(SmsMessageCategory.BookingConfirmation),
            CancellationToken.None);

        Assert.True(result.IsEligible);
        Assert.NotNull(result.Proof);
    }

    [Fact]
    public async Task EvaluateAsync_InternalNotificationWithoutCustomerConsent_IsAllowed()
    {
        var gate = CreateGate(new FakeCrmAdapter());

        var result = await gate.EvaluateAsync(
            new SmsEligibilityRequest(
                "tenant-a",
                "corr-1",
                SmsMessageCategory.InternalOperational,
                ProviderContactId: null,
                PhoneNumber: "+15557654321"),
            CancellationToken.None);

        Assert.True(result.IsEligible);
    }

    [Fact]
    public async Task EvaluateAsync_MarketingWithoutExplicitSmsConsent_IsDenied()
    {
        var gate = CreateGate(CreateCrm(CrmConsentStatuses.Unknown));

        var result = await gate.EvaluateAsync(
            CreateRequest(SmsMessageCategory.MarketingFollowUp),
            CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Equal(SmsEligibilitySkipReason.ConsentNotGranted, result.SkipReason);
    }

    [Fact]
    public async Task EvaluateAsync_StaleRetry_IsDenied()
    {
        var gate = CreateGate(CreateCrm(CrmConsentStatuses.OptIn));

        var result = await gate.EvaluateAsync(
            CreateRequest(SmsMessageCategory.ClassReminder) with
            {
                IsRetry = true,
                OriginalRequestedAt = Now.AddMinutes(-61)
            },
            CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Equal(SmsEligibilitySkipReason.RetryStale, result.SkipReason);
    }

    [Fact]
    public async Task EvaluateAsync_BookingConfirmationWhenCrmUnavailable_IsAllowed()
    {
        var gate = CreateGate(new FakeCrmAdapter { LookupException = new InvalidOperationException("crm down") });

        var result = await gate.EvaluateAsync(
            CreateRequest(SmsMessageCategory.BookingConfirmation) with { ProviderContactId = null },
            CancellationToken.None);

        Assert.True(result.IsEligible);
        Assert.NotNull(result.Proof);
    }

    [Fact]
    public async Task EvaluateAsync_BookingConfirmationWithoutCrmContact_IsAllowed()
    {
        var gate = CreateGate(new FakeCrmAdapter());

        var result = await gate.EvaluateAsync(
            CreateRequest(SmsMessageCategory.BookingConfirmation) with { ProviderContactId = null },
            CancellationToken.None);

        Assert.True(result.IsEligible);
    }

    [Fact]
    public async Task EvaluateAsync_MarketingWhenCrmUnavailable_IsDenied()
    {
        var gate = CreateGate(new FakeCrmAdapter { LookupException = new InvalidOperationException("crm down") });

        var result = await gate.EvaluateAsync(
            CreateRequest(SmsMessageCategory.MarketingFollowUp),
            CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Equal(SmsEligibilitySkipReason.DependencyUnavailable, result.SkipReason);
    }

    [Fact]
    public async Task EvaluateAsync_KnownContact_SkipsCrmLookupAndStillHonorsOptOut()
    {
        var crm = CreateCrm(CrmConsentStatuses.OptIn);
        var optedOut = CreateCrm(CrmConsentStatuses.OptedOut).LookupResult.Contact;
        var gate = CreateGate(crm);

        var result = await gate.EvaluateAsync(
            CreateRequest(SmsMessageCategory.BookingConfirmation) with { KnownContact = optedOut },
            CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Equal(SmsEligibilitySkipReason.ContactOptedOut, result.SkipReason);
        Assert.Equal(0, crm.LookupCount);
    }

    private static SmsEligibilityGate CreateGate(FakeCrmAdapter crm) =>
        new(
            crm,
            new FakeTenantConfigurationProvider(),
            new SendWindowPolicy(),
            new FakeEventLogger(),
            new FixedTimeProvider(Now));

    private static SmsEligibilityRequest CreateRequest(SmsMessageCategory category) =>
        new("tenant-a", "corr-1", category, "contact-1", "+15551234567");

    private static FakeCrmAdapter CreateCrm(string smsConsentStatus) =>
        new()
        {
            LookupResult = new CrmContactLookupResult(true, "contact-1")
            {
                Contact = new CrmContactRecord(
                    "tenant-a",
                    "contact-1",
                    "+15551234567",
                    "lead@example.com",
                    "Lead",
                    "75001",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [CrmContactAttributeNames.SmsConsentStatus] = smsConsentStatus
                    })
            }
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
