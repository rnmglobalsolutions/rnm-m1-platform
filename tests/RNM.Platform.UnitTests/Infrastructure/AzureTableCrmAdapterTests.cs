using RNM.Platform.Application.Crm;
using RNM.Platform.Infrastructure.Crm;
using Xunit;

namespace RNM.Platform.UnitTests.Infrastructure;

public sealed class AzureTableCrmAdapterTests
{
    [Fact]
    public void CanRecordOutboundInteraction_RejectsOptedOutContact()
    {
        var canRecord = AzureTableCrmAdapter.CanRecordOutboundInteraction(CrmConsentStatuses.OptedOut);

        Assert.False(canRecord);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(CrmConsentStatuses.Unknown)]
    [InlineData(CrmConsentStatuses.OptIn)]
    public void CanRecordOutboundInteraction_AllowsNonOptedOutContact(string? consentStatus)
    {
        var canRecord = AzureTableCrmAdapter.CanRecordOutboundInteraction(consentStatus);

        Assert.True(canRecord);
    }

    [Fact]
    public void SelectNextLeadToCall_RespectsConsentAttemptLimitAndFollowUpTiming()
    {
        var now = new DateTimeOffset(2026, 7, 4, 14, 0, 0, TimeSpan.Zero);
        var optedOut = CreateLead(
            "opted-out",
            consentStatus: CrmConsentStatuses.OptedOut,
            attempts: 0,
            nextFollowUpAt: null);
        var overAttemptLimit = CreateLead(
            "over-limit",
            consentStatus: CrmConsentStatuses.Unknown,
            attempts: 3,
            nextFollowUpAt: null);
        var futureFollowUp = CreateLead(
            "future",
            consentStatus: CrmConsentStatuses.Unknown,
            attempts: 0,
            nextFollowUpAt: now.AddMinutes(1));
        var eligible = CreateLead(
            "eligible",
            consentStatus: CrmConsentStatuses.Unknown,
            attempts: 1,
            nextFollowUpAt: now.AddMinutes(-5));

        var selected = AzureTableCrmAdapter.SelectNextLeadToCall(
            [optedOut, overAttemptLimit, futureFollowUp, eligible],
            maxOutboundAttempts: 3,
            now);

        Assert.Same(eligible, selected);
    }

    private static CrmContactRecord CreateLead(
        string contactId,
        string consentStatus,
        int attempts,
        DateTimeOffset? nextFollowUpAt)
    {
        var attributes = new Dictionary<string, string>
        {
            [CrmContactAttributeNames.ConsentStatus] = consentStatus,
            [CrmContactAttributeNames.OutboundAttemptCount] = attempts.ToString()
        };
        if (nextFollowUpAt.HasValue)
        {
            attributes[CrmContactAttributeNames.NextFollowUpAt] = nextFollowUpAt.Value.ToString("O");
        }

        return new CrmContactRecord(
            "tenant-a",
            contactId,
            PhoneNumber: null,
            Email: null,
            Name: null,
            ZipCode: null,
            attributes);
    }
}
