using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.LeadIntake;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.LeadIntake;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.LeadIntake;

public sealed class InboundLeadIntakeServiceTests
{
    [Fact]
    public async Task ProcessAsync_PersistsNormalizedLeadConsentAndQueuesBusinessNotifications()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.ProcessAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var upsert = Assert.Single(fixture.Crm.Upserts);
        Assert.Equal("+13052445176", upsert.PhoneNumber);
        Assert.Equal(CrmConsentStatuses.OptIn, upsert.Attributes[CrmContactAttributeNames.ConsentStatus]);
        Assert.Equal("subscriber-42", upsert.Attributes[CrmContactAttributeNames.ExternalSourceId]);
        Assert.Contains(fixture.Crm.Timeline, item => item.EventType == CrmTimelineEventTypes.LeadIntakeReceived);
        Assert.Contains(fixture.Crm.Timeline, item => item.EventType == CrmTimelineEventTypes.MarketingConsentExternalGranted);
        Assert.Equal(2, fixture.Scheduler.Requests.Count);
        Assert.True(fixture.Receipt.Completed);
    }

    [Fact]
    public async Task ProcessAsync_ClassifiesNearTermFinancialEducationLeadForConsultation()
    {
        var fixture = new Fixture();
        var request = CreateRequest(
            new Dictionary<string, string>
            {
                ["funnelType"] = "financial_education",
                ["primaryGoal"] = "family_protection",
                ["timeline"] = "under_30_days",
                ["currentProtection"] = "employer_only",
                ["requestedNextStep"] = "consultation"
            });

        var result = await fixture.Service.ProcessAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("ready_for_consultation", result.LeadClassification);
        Assert.Equal("consultation", result.RecommendedRoute);
        var attributes = fixture.Crm.Upserts.Single().Attributes;
        Assert.Equal("ready_for_consultation", attributes["leadClassification"]);
        Assert.Equal("consultation", attributes["recommendedRoute"]);
        Assert.Contains("requested_consultation", attributes["classificationReasons"]);
    }

    [Fact]
    public async Task ProcessAsync_ClassifiesIncompatibleBusinessOpportunityLeadAsNotQualified()
    {
        var fixture = new Fixture();
        var request = CreateRequest(
            new Dictionary<string, string>
            {
                ["funnelType"] = "business_opportunity",
                ["primaryGoal"] = "extra_income",
                ["timeline"] = "under_30_days",
                ["weeklyAvailability"] = "10_20_hours",
                ["incomeExpectation"] = "guaranteed_income"
            });

        var result = await fixture.Service.ProcessAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("not_qualified", result.LeadClassification);
        Assert.Equal("none", result.RecommendedRoute);
        var attributes = fixture.Crm.Upserts.Single().Attributes;
        Assert.Equal("not_qualified", attributes["leadClassification"]);
        Assert.Equal("none", attributes["recommendedRoute"]);
        Assert.Contains("incompatible_business_expectation", attributes["classificationReasons"]);
    }

    [Fact]
    public async Task ProcessAsync_StoresTemperatureRuleAndVersionOnContact()
    {
        var fixture = new Fixture();
        var request = CreateRequest(new Dictionary<string, string>
        {
            ["funnelType"] = "financial_education",
            ["requestedNextStep"] = "consultation"
        });

        var result = await fixture.Service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(LeadTiers.Hot, result.LeadTemperature);
        var attributes = fixture.Crm.Upserts.Single().Attributes;
        Assert.Equal(LeadTiers.Hot, attributes["leadTemperature"]);
        Assert.Equal("fe-requested-consultation", attributes["classificationRuleId"]);
        Assert.Equal("life-insurance-2026-09-26", attributes["classificationRulesetVersion"]);
    }

    [Theory]
    [InlineData("business_opportunity", "incomeExpectation", "guaranteed_income", false)]
    [InlineData("financial_education", "requestedNextStep", "no_contact", false)]
    [InlineData("financial_education", "requestedNextStep", "master_class", true)]
    public async Task ProcessAsync_FollowUpFollowsTheClassification(string funnel, string attribute, string value, bool expectedFollowUp)
    {
        var fixture = new Fixture();
        var request = CreateRequest(new Dictionary<string, string> { ["funnelType"] = funnel, [attribute] = value }) with
        {
            ScheduleFollowUp = true
        };

        var result = await fixture.Service.ProcessAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(expectedFollowUp, result.FollowUpRequested);
    }

    [Fact]
    public async Task ProcessAsync_ExistingOptedOutCannotBeReversedByManyChat()
    {
        var fixture = new Fixture();
        fixture.Crm.Existing = new CrmContactRecord(
            "tenant-a",
            "contact-existing",
            "+13052445176",
            null,
            "Jane",
            null,
            new Dictionary<string, string>
            {
                [CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptedOut
            });

        var result = await fixture.Service.ProcessAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(CrmConsentStatuses.OptedOut, fixture.Crm.Upserts.Single().Attributes[CrmContactAttributeNames.ConsentStatus]);
        Assert.Contains(fixture.Crm.Timeline, item => item.EventType == CrmTimelineEventTypes.MarketingConsentExternalBlockedOptedOut);
    }

    [Fact]
    public async Task ProcessAsync_CompletedReceiptDoesNotWriteOrNotifyAgain()
    {
        var fixture = new Fixture();
        fixture.Receipt.NextState = InboundLeadReceiptClaimState.Completed;

        var result = await fixture.Service.ProcessAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Duplicate);
        Assert.Empty(fixture.Crm.Upserts);
        Assert.Empty(fixture.Scheduler.Requests);
    }

    [Fact]
    public async Task ProcessAsync_GrantedConsentWithoutEvidenceIsRejectedBeforeClaim()
    {
        var fixture = new Fixture();
        var request = CreateRequest() with { ConsentCapturedAt = null };

        var result = await fixture.Service.ProcessAsync(request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("missing_consent_evidence", result.FailureCode);
        Assert.Equal(0, fixture.Receipt.BeginCount);
    }

    [Fact]
    public async Task ProcessAsync_SmsGrantWithoutDisclosureIsStoredAsNotGranted()
    {
        var fixture = new Fixture();
        var request = CreateRequest() with
        {
            SmsConsent = new ChannelConsentCapture(true, "consentSms", DisclosureText: "", "meta-form-v1", DateTimeOffset.UtcNow, "ManyChat")
        };

        var result = await fixture.Service.ProcessAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        var attributes = Assert.Single(fixture.Crm.Upserts).Attributes;
        Assert.Equal(CrmConsentStatuses.Unknown, attributes[CrmContactAttributeNames.SmsConsentStatus]);
        Assert.Equal(bool.FalseString, attributes[CrmContactAttributeNames.SmsConsentGranted]);
        Assert.NotEqual(CrmConsentStatuses.OptIn, attributes[CrmContactAttributeNames.ConsentStatus]);
    }

    [Fact]
    public async Task ProcessAsync_EvidencedChannelGrantsArePersistedPerChannel()
    {
        var fixture = new Fixture();
        var request = CreateRequest() with
        {
            SmsConsent = new ChannelConsentCapture(true, "consentSms", "I agree to texts. Reply STOP to opt out.", "meta-form-v1", DateTimeOffset.UtcNow, "ManyChat"),
            EmailConsent = new ChannelConsentCapture(false, "consentEmail", "I agree to emails.", "meta-form-v1", DateTimeOffset.UtcNow, "ManyChat")
        };

        var result = await fixture.Service.ProcessAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        var attributes = Assert.Single(fixture.Crm.Upserts).Attributes;
        Assert.Equal(CrmConsentStatuses.OptIn, attributes[CrmContactAttributeNames.SmsConsentStatus]);
        Assert.Equal(CrmConsentStatuses.Unknown, attributes[CrmContactAttributeNames.EmailConsentStatus]);
        Assert.Equal("I agree to texts. Reply STOP to opt out.", attributes[CrmContactAttributeNames.SmsConsentDisclosureText]);
    }

    [Fact]
    public async Task ProcessAsync_NotificationQueueExceptionDoesNotReopenCompletedReceipt()
    {
        var fixture = new Fixture();
        fixture.Scheduler.ThrowOnSchedule = true;

        var result = await fixture.Service.ProcessAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.BusinessNotificationQueued);
        Assert.True(fixture.Receipt.Completed);
        Assert.False(fixture.Receipt.Failed);
    }

    private static InboundLeadIntakeRequest CreateRequest(IReadOnlyDictionary<string, string>? attributes = null) =>
        new(
            "tenant-a",
            "life-insurance",
            "correlation-a",
            "ManyChat",
            "event-42",
            "subscriber-42",
            "Jane Lead",
            "(305) 244-5176",
            "jane@example.com",
            "meta-campaign-a",
            MarketingConsentGranted: true,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "meta-form-v1",
            attributes ?? new Dictionary<string, string> { ["intent"] = "agent_interest" },
            ScheduleFollowUp: false);

    private sealed class Fixture
    {
        public Fixture()
        {
            var logger = new NullEventLogger();
            var tenants = new TenantProvider();
            Service = new InboundLeadIntakeService(
                Receipt,
                Crm,
                new CrmApplicationService(Crm, logger),
                tenants,
                Scheduler,
                RepositoryConfiguration.Classifier(tenants, logger),
                logger);
        }

        public InboundLeadIntakeService Service { get; }
        public FakeReceiptStore Receipt { get; } = new();
        public FakeCrmAdapter Crm { get; } = new();
        public FakeScheduler Scheduler { get; } = new();
    }

    private sealed class FakeReceiptStore : IInboundLeadReceiptStore
    {
        public InboundLeadReceiptClaimState NextState { get; set; } = InboundLeadReceiptClaimState.Acquired;
        public int BeginCount { get; private set; }
        public bool Completed { get; private set; }
        public bool Failed { get; private set; }

        public Task<InboundLeadReceiptClaimResult> TryBeginAsync(InboundLeadReceiptClaimRequest request, CancellationToken cancellationToken)
        {
            BeginCount++;
            return Task.FromResult(new InboundLeadReceiptClaimResult(NextState, "row-a", "lease-a", "contact-existing"));
        }

        public Task CompleteAsync(InboundLeadReceiptCompletionRequest request, CancellationToken cancellationToken)
        {
            Completed = true;
            return Task.CompletedTask;
        }

        public Task FailAsync(InboundLeadReceiptFailureRequest request, CancellationToken cancellationToken)
        {
            Failed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScheduler : IConfirmationRetryScheduler
    {
        public List<ConfirmationRetryRequest> Requests { get; } = [];
        public bool ThrowOnSchedule { get; set; }

        public Task<bool> ScheduleAsync(ConfirmationRetryRequest request, CancellationToken cancellationToken)
        {
            if (ThrowOnSchedule)
            {
                throw new InvalidOperationException("Queue unavailable.");
            }

            Requests.Add(request);
            return Task.FromResult(true);
        }
    }

    private sealed class TenantProvider : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantConfiguration(
                new TenantId(tenantId),
                new VerticalId("life-insurance"),
                "RNM Insurance",
                "America/Chicago",
                new ServiceAreaConfiguration([], ["United States"], "Follow up"),
                new ProviderConfiguration("AzureTable", "GoogleCalendar", "Twilio", "SendGrid"),
                new SecretNameConfiguration("crm", "booking", "voice", "twilio-sid", "twilio-token", "email"),
                new CommunicationConfiguration(
                    "+13055550100",
                    "sender@example.com",
                    new ConfirmationTemplateConfiguration(
                        "received",
                        BusinessSmsBodyTemplate: "New {{customerName}} {{attr.intent}}",
                        BusinessEmailSubjectTemplate: "New {{customerName}}",
                        BusinessEmailBodyTemplate: "Phone {{customerPhoneNumber}}"),
                    BusinessNotificationEmail: "owner@example.com",
                    BusinessNotificationPhoneNumber: "+13055550101")));
    }

    private sealed class NullEventLogger : IEventLogger
    {
        public Task LogEventAsync(string eventName, IReadOnlyDictionary<string, string> properties, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeCrmAdapter : ICrmAdapter
    {
        public CrmContactRecord? Existing { get; set; }
        public List<CrmContactUpsertRequest> Upserts { get; } = [];
        public List<CrmTimelineEventRequest> Timeline { get; } = [];

        public Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(CrmContactLookupRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Existing is null
                ? new CrmContactLookupResult(false, null)
                : new CrmContactLookupResult(true, Existing.ProviderContactId) { Contact = Existing });

        public Task<CrmContactUpsertResult> UpsertContactAsync(CrmContactUpsertRequest request, CancellationToken cancellationToken)
        {
            Upserts.Add(request);
            return Task.FromResult(new CrmContactUpsertResult(true, Existing is null, Existing?.ProviderContactId ?? "contact-a"));
        }

        public Task<CrmOperationResult> AddTimelineEventAsync(CrmTimelineEventRequest request, CancellationToken cancellationToken)
        {
            Timeline.Add(request);
            return Task.FromResult(new CrmOperationResult(true));
        }

        public Task<CrmOperationResult> AddInteractionNoteAsync(CrmInteractionNoteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> ApplyTagsAsync(CrmTagRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> LinkBookingToContactAsync(CrmBookingLinkRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkFollowUpRequiredAsync(CrmFollowUpRequest request, CancellationToken cancellationToken) => Task.FromResult(new CrmOperationResult(true));
        public Task<CrmLeadQueryResult> GetLeadsByStatusAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmLeadQueryResult> GetLeadsByCampaignAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmNextLeadToCallResult> GetNextLeadToCallAsync(CrmNextLeadToCallRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> RecordOutboundAttemptAsync(CrmOutboundAttemptRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkLeadReactivatedAsync(CrmLeadReactivationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkOptOutAsync(CrmOptOutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
