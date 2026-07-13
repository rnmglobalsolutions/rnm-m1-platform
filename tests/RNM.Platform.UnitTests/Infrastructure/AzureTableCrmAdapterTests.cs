using RNM.Platform.Application.Crm;
using RNM.Platform.Infrastructure.Crm;
using Xunit;

namespace RNM.Platform.UnitTests.Infrastructure;

public sealed class AzureTableCrmAdapterTests
{
    [Fact]
    public void CanRecordOutboundInteraction_AllowsOnlyOptInContact()
    {
        var canRecord = AzureTableCrmAdapter.CanRecordOutboundInteraction(CrmConsentStatuses.OptIn);

        Assert.True(canRecord);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(CrmConsentStatuses.Unknown)]
    [InlineData(CrmConsentStatuses.OptedOut)]
    public void CanRecordOutboundInteraction_RejectsNonOptInContact(string? consentStatus)
    {
        var canRecord = AzureTableCrmAdapter.CanRecordOutboundInteraction(consentStatus);

        Assert.False(canRecord);
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
            consentStatus: CrmConsentStatuses.OptIn,
            attempts: 3,
            nextFollowUpAt: null);
        var futureFollowUp = CreateLead(
            "future",
            consentStatus: CrmConsentStatuses.OptIn,
            attempts: 0,
            nextFollowUpAt: now.AddMinutes(1));
        var eligible = CreateLead(
            "eligible",
            consentStatus: CrmConsentStatuses.OptIn,
            attempts: 1,
            nextFollowUpAt: now.AddMinutes(-5));

        var selected = AzureTableCrmAdapter.SelectNextLeadToCall(
            [optedOut, overAttemptLimit, futureFollowUp, eligible],
            maxOutboundAttempts: 3,
            now);

        Assert.Same(eligible, selected);
    }

    [Theory]
    [InlineData("3052445176", "+13052445176")]
    [InlineData("(305) 244-5176", "+13052445176")]
    [InlineData("+1 305 244 5176", "+13052445176")]
    public void NormalizePhoneForIndex_ReusesE164Rules(string input, string expected)
    {
        var normalized = AzureTableCrmAdapter.NormalizePhoneForIndex(input);

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void NormalizePhoneForIndex_ReturnsNullForInvalidPhone()
    {
        var normalized = AzureTableCrmAdapter.NormalizePhoneForIndex("not a phone");

        Assert.Null(normalized);
    }

    [Fact]
    public void CreatePhoneIndexEntity_UsesTenantPartitionAndE164RowKey()
    {
        var entity = AzureTableCrmAdapter.CreatePhoneIndexEntity(
            "tenant-a",
            "+13052445176",
            "contact-1",
            "correlation-1");

        Assert.Equal("tenant-a", entity.PartitionKey);
        Assert.Equal("+13052445176", entity.RowKey);
        Assert.Equal("contact-1", entity["ContactId"]);
        Assert.Equal("correlation-1", entity["CorrelationId"]);
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
