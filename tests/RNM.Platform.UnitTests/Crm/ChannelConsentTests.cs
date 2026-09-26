using RNM.Platform.Application.Crm;
using Xunit;

namespace RNM.Platform.UnitTests.Crm;

public sealed class ChannelConsentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ResolveStatus_LegacyOptInRecord_CoversBothChannels()
    {
        var contact = CreateContact(new() { [CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptIn });

        Assert.Equal(CrmConsentStatuses.OptIn, contact.SmsConsentStatus);
        Assert.Equal(CrmConsentStatuses.OptIn, contact.EmailConsentStatus);
    }

    [Fact]
    public void ResolveStatus_LegacyOptOutRecord_BlocksSmsOnly()
    {
        var contact = CreateContact(new() { [CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptedOut });

        Assert.Equal(CrmConsentStatuses.OptedOut, contact.SmsConsentStatus);
        Assert.Equal(CrmConsentStatuses.Unknown, contact.EmailConsentStatus);
    }

    [Fact]
    public void ResolveStatus_PerChannelRecord_DoesNotInheritLegacyOptIn()
    {
        var contact = CreateContact(new()
        {
            [CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptIn,
            [CrmContactAttributeNames.SmsConsentStatus] = CrmConsentStatuses.OptIn
        });

        Assert.Equal(CrmConsentStatuses.OptIn, contact.SmsConsentStatus);
        Assert.Equal(CrmConsentStatuses.Unknown, contact.EmailConsentStatus);
    }

    [Fact]
    public void ResolveStatus_SmsStopAfterLegacyOptIn_KeepsEmailOutOfLegacyFallback()
    {
        var contact = CreateContact(new()
        {
            [CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptedOut,
            [CrmContactAttributeNames.SmsConsentStatus] = CrmConsentStatuses.OptedOut
        });

        Assert.Equal(CrmConsentStatuses.OptedOut, contact.SmsConsentStatus);
        Assert.Equal(CrmConsentStatuses.Unknown, contact.EmailConsentStatus);
    }

    [Fact]
    public void RequireEvidence_GrantWithoutDisclosure_IsDowngraded()
    {
        var capture = new ChannelConsentCapture(true, "consentSms", DisclosureText: "", "v1", Now, "ManyChat");

        var result = ChannelConsent.RequireEvidence(capture, Now, out var downgraded);

        Assert.True(downgraded);
        Assert.False(result!.Granted);
        Assert.Equal("consentSms", result.SourceField);
    }

    [Fact]
    public void RequireEvidence_CompleteGrant_IsKept()
    {
        var capture = new ChannelConsentCapture(true, "consentSms", "I agree. Reply STOP to opt out.", "v1", Now, "ManyChat");

        var result = ChannelConsent.RequireEvidence(capture, Now, out var downgraded);

        Assert.False(downgraded);
        Assert.Same(capture, result);
    }

    private static CrmContactRecord CreateContact(Dictionary<string, string> attributes) =>
        new("tenant-a", "contact-1", "+15551234567", "lead@example.com", "Lead", null, attributes);
}
