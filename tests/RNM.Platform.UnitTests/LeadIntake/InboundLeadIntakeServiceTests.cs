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

    private static InboundLeadIntakeRequest CreateRequest() =>
        new(
            "tenant-a",
            "insurance-agents",
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
            new Dictionary<string, string> { ["intent"] = "agent_interest" },
            ScheduleFollowUp: false);

    private sealed class Fixture
    {
        public Fixture()
        {
            var logger = new NullEventLogger();
            Service = new InboundLeadIntakeService(
                Receipt,
                Crm,
                new CrmApplicationService(Crm, logger),
                new TenantProvider(),
                Scheduler,
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
                new VerticalId("insurance-agents"),
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
