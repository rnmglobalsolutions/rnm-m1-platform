using RNM.Platform.Application.Compliance;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.FollowUps;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.FollowUps;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.FollowUps;

public sealed class FollowUpAutomationTests
{
    [Fact]
    public async Task ScheduleAsync_FollowUpRequired_SchedulesFirstStep()
    {
        var store = new RecordingFollowUpStore();
        var crm = new RecordingCrmAdapter();
        var service = new FollowUpSchedulingService(
            new FollowUpTenantProvider(),
            store,
            crm,
            new RecordingEventLogger());

        await service.ScheduleAsync(
            new FollowUpScheduleRequest("tenant-a", "corr-1", "contact-1", FollowUpTriggers.LeadFollowUpRequired, "no booking")
            {
                CustomerName = "Raisel",
                CustomerPhoneNumber = "+13055550100",
                CustomerEmail = "lead@example.com",
                TriggeredAt = new DateTimeOffset(2026, 7, 10, 14, 0, 0, TimeSpan.Zero)
            },
            CancellationToken.None);

        var followUp = Assert.Single(store.FollowUps.Values);
        Assert.Equal("tenant-a", followUp.TenantId);
        Assert.Equal("lead-needs-follow-up", followUp.SequenceId);
        Assert.Equal(0, followUp.StepIndex);
        Assert.Equal(FollowUpChannels.Sms, followUp.Channel);
        Assert.Equal(new DateTimeOffset(2026, 7, 10, 14, 30, 0, TimeSpan.Zero), followUp.DueAt);
        Assert.Contains(crm.TimelineEvents, item => item.EventType == CrmTimelineEventTypes.FollowUpScheduled);
    }

    [Fact]
    public async Task ScheduleAsync_DuplicateSchedule_DoesNotResetExistingFollowUp()
    {
        var store = new RecordingFollowUpStore();
        var existingDueAt = new DateTimeOffset(2026, 7, 10, 14, 30, 0, TimeSpan.Zero);
        store.Add(CreateDue(existingDueAt) with { Status = FollowUpStatuses.Sent });
        var service = new FollowUpSchedulingService(
            new FollowUpTenantProvider(),
            store,
            new RecordingCrmAdapter(),
            new RecordingEventLogger());

        await service.ScheduleAsync(
            new FollowUpScheduleRequest("tenant-a", "corr-1", "contact-1", FollowUpTriggers.LeadFollowUpRequired, "no booking")
            {
                CustomerPhoneNumber = "+13055550100",
                CustomerEmail = "lead@example.com",
                TriggeredAt = new DateTimeOffset(2026, 7, 10, 14, 0, 0, TimeSpan.Zero)
            },
            CancellationToken.None);

        var followUp = Assert.Single(store.FollowUps.Values);
        Assert.Equal(FollowUpStatuses.Sent, followUp.Status);
    }

    [Fact]
    public async Task RunAsync_SendsSmsInsideWindow_AndSchedulesNextStep()
    {
        var store = new RecordingFollowUpStore();
        var dueAt = new DateTimeOffset(2026, 7, 10, 15, 0, 0, TimeSpan.Zero);
        store.Add(CreateDue(dueAt));
        var sms = new RecordingSmsSender();
        var email = new RecordingEmailSender();
        var crm = new RecordingCrmAdapter
        {
            LookupResult = new CrmContactLookupResult(true, "contact-1")
            {
                Contact = CreateContact(CrmConsentStatuses.OptIn)
            }
        };
        var service = CreateRunService(store, crm, sms, email);

        var result = await service.RunAsync(
            new FollowUpRunRequest("tenant-a", "corr-2", dueAt),
            CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Single(sms.Requests);
        Assert.Contains("Raisel", sms.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains(store.FollowUps.Values, item => item.SequenceId == "lead-needs-follow-up" && item.StepIndex == 1 && item.Status == FollowUpStatuses.Pending);
        Assert.Contains(crm.TimelineEvents, item => item.EventType == CrmTimelineEventTypes.FollowUpSent);
    }

    [Fact]
    public async Task RunAsync_EmailStep_SendsWithoutSendWindowBlock()
    {
        var store = new RecordingFollowUpStore();
        var dueAt = new DateTimeOffset(2026, 7, 10, 4, 0, 0, TimeSpan.Zero);
        store.Add(CreateDue(dueAt) with { StepIndex = 1, Channel = FollowUpChannels.Email });
        var sms = new RecordingSmsSender();
        var email = new RecordingEmailSender();
        var crm = new RecordingCrmAdapter
        {
            LookupResult = new CrmContactLookupResult(true, "contact-1")
            {
                Contact = CreateContact(CrmConsentStatuses.OptIn)
            }
        };
        var service = CreateRunService(store, crm, sms, email);

        var result = await service.RunAsync(
            new FollowUpRunRequest("tenant-a", "corr-2", dueAt),
            CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Empty(sms.Requests);
        Assert.Single(email.Requests);
    }

    [Fact]
    public async Task RunAsync_SmsOutsideSendWindow_IsSkipped()
    {
        var store = new RecordingFollowUpStore();
        var dueAt = new DateTimeOffset(2026, 7, 10, 4, 0, 0, TimeSpan.Zero);
        store.Add(CreateDue(dueAt));
        var sms = new RecordingSmsSender();
        var crm = new RecordingCrmAdapter
        {
            LookupResult = new CrmContactLookupResult(true, "contact-1")
            {
                Contact = CreateContact(CrmConsentStatuses.OptIn)
            }
        };
        var service = CreateRunService(store, crm, sms, new RecordingEmailSender());

        var result = await service.RunAsync(
            new FollowUpRunRequest("tenant-a", "corr-2", dueAt),
            CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(sms.Requests);
        Assert.Equal(FollowUpStatuses.Skipped, store.FollowUps.Values.Single().Status);
        Assert.Contains(crm.TimelineEvents, item =>
            item.EventType == CrmTimelineEventTypes.FollowUpSkipped
            && item.Metadata.TryGetValue("reason", out var reason)
            && reason == FollowUpSkipReasons.OutsideSendWindow);
    }

    [Fact]
    public async Task RunAsync_OptedOutContact_IsSkipped()
    {
        var store = new RecordingFollowUpStore();
        var dueAt = new DateTimeOffset(2026, 7, 10, 15, 0, 0, TimeSpan.Zero);
        store.Add(CreateDue(dueAt));
        var sms = new RecordingSmsSender();
        var crm = new RecordingCrmAdapter
        {
            LookupResult = new CrmContactLookupResult(true, "contact-1")
            {
                Contact = CreateContact(CrmConsentStatuses.OptedOut)
            }
        };
        var service = CreateRunService(store, crm, sms, new RecordingEmailSender());

        var result = await service.RunAsync(
            new FollowUpRunRequest("tenant-a", "corr-2", dueAt),
            CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(sms.Requests);
        Assert.Contains(crm.TimelineEvents, item => item.EventType == CrmTimelineEventTypes.FollowUpSkipped);
    }

    [Fact]
    public async Task RunAsync_StaleFollowUp_IsSkipped()
    {
        var store = new RecordingFollowUpStore();
        var dueAt = new DateTimeOffset(2026, 7, 10, 15, 0, 0, TimeSpan.Zero);
        store.Add(CreateDue(dueAt));
        var sms = new RecordingSmsSender();
        var crm = new RecordingCrmAdapter
        {
            LookupResult = new CrmContactLookupResult(true, "contact-1")
            {
                Contact = CreateContact(CrmConsentStatuses.OptIn)
            }
        };
        var service = CreateRunService(store, crm, sms, new RecordingEmailSender());

        var result = await service.RunAsync(
            new FollowUpRunRequest("tenant-a", "corr-2", dueAt.AddHours(3)),
            CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(sms.Requests);
        Assert.Contains(crm.TimelineEvents, item =>
            item.Metadata.TryGetValue("reason", out var reason)
            && reason == FollowUpSkipReasons.Stale);
    }

    private static FollowUpRunService CreateRunService(
        RecordingFollowUpStore store,
        RecordingCrmAdapter crm,
        RecordingSmsSender sms,
        RecordingEmailSender email) =>
        new(
            new FollowUpTenantProvider(),
            store,
            crm,
            sms,
            email,
            new SendWindowPolicy(),
            new RecordingEventLogger(),
            new AllowingSmsEligibilityGate());

    private static FollowUpDueRecord CreateDue(DateTimeOffset dueAt) =>
        new(
            "tenant-a",
            FollowUpSchedulingService.CreateRowKey(dueAt, "contact-1", "lead-needs-follow-up", 0),
            "contact-1",
            "lead-needs-follow-up",
            0,
            FollowUpTriggers.LeadFollowUpRequired,
            FollowUpChannels.Sms,
            dueAt,
            FollowUpStatuses.Pending,
            "corr-1")
        {
            CustomerName = "Raisel",
            CustomerPhoneNumber = "+13055550100",
            CustomerEmail = "lead@example.com",
            Reason = "No booking",
            Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["campaignId"] = "campaign-a"
            }
        };

    private static CrmContactRecord CreateContact(string consentStatus) =>
        new(
            "tenant-a",
            "contact-1",
            "+13055550100",
            "lead@example.com",
            "Raisel",
            "77002",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [CrmContactAttributeNames.ConsentStatus] = consentStatus,
                [CrmContactAttributeNames.SmsConsentStatus] = consentStatus,
                [CrmContactAttributeNames.EmailConsentStatus] = consentStatus,
                [CrmContactAttributeNames.CampaignId] = "campaign-a"
            });
}

internal sealed class RecordingFollowUpStore : IFollowUpStore
{
    public Dictionary<string, FollowUpDueRecord> FollowUps { get; } = new(StringComparer.Ordinal);

    public void Add(FollowUpDueRecord followUp) => FollowUps[followUp.RowKey] = followUp;

    public Task ScheduleFollowUpAsync(FollowUpDueRecord followUp, CancellationToken cancellationToken)
    {
        FollowUps.TryAdd(followUp.RowKey, followUp);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<FollowUpDueRecord>> GetDueFollowUpsAsync(
        string tenantId,
        DateTimeOffset dueAt,
        int maxItems,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyCollection<FollowUpDueRecord>>(
            FollowUps.Values
                .Where(item => item.TenantId == tenantId && item.DueAt <= dueAt && item.Status == FollowUpStatuses.Pending)
                .Take(maxItems)
                .ToArray());
    }

    public Task<bool> TryClaimFollowUpAsync(
        string tenantId,
        string rowKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!FollowUps.TryGetValue(rowKey, out var followUp) || followUp.Status != FollowUpStatuses.Pending)
        {
            return Task.FromResult(false);
        }

        FollowUps[rowKey] = followUp with { Status = FollowUpStatuses.Claimed };
        return Task.FromResult(true);
    }

    public Task MarkFollowUpAsync(
        string tenantId,
        string rowKey,
        string status,
        string correlationId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (FollowUps.TryGetValue(rowKey, out var followUp))
        {
            FollowUps[rowKey] = followUp with { Status = status };
        }

        return Task.CompletedTask;
    }

    public Task<int> CountSentForContactOnDateAsync(
        string tenantId,
        string providerContactId,
        DateOnly localDate,
        string timeZone,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(FollowUps.Values.Count(item =>
            item.TenantId == tenantId
            && item.ProviderContactId == providerContactId
            && item.Status == FollowUpStatuses.Sent));
    }
}

internal sealed class RecordingCrmAdapter : ICrmAdapter
{
    public CrmContactLookupResult LookupResult { get; init; } = new(false, null);

    public List<CrmTimelineEventRequest> TimelineEvents { get; } = [];

    public Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(
        CrmContactLookupRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(LookupResult);

    public Task<CrmContactUpsertResult> UpsertContactAsync(CrmContactUpsertRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmContactUpsertResult(true, false, request.ProviderContactId));

    public Task<CrmOperationResult> AddInteractionNoteAsync(CrmInteractionNoteRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));

    public Task<CrmOperationResult> ApplyTagsAsync(CrmTagRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));

    public Task<CrmOperationResult> LinkBookingToContactAsync(CrmBookingLinkRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));

    public Task<CrmOperationResult> AddTimelineEventAsync(CrmTimelineEventRequest request, CancellationToken cancellationToken)
    {
        TimelineEvents.Add(request);
        return Task.FromResult(new CrmOperationResult(true));
    }

    public Task<CrmOperationResult> MarkFollowUpRequiredAsync(CrmFollowUpRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));

    public Task<CrmLeadQueryResult> GetLeadsByStatusAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmLeadQueryResult(true, []));

    public Task<CrmLeadQueryResult> GetLeadsByCampaignAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmLeadQueryResult(true, []));

    public Task<CrmNextLeadToCallResult> GetNextLeadToCallAsync(CrmNextLeadToCallRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmNextLeadToCallResult(true, null));

    public Task<CrmOperationResult> RecordOutboundAttemptAsync(CrmOutboundAttemptRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));

    public Task<CrmOperationResult> MarkLeadReactivatedAsync(CrmLeadReactivationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));

    public Task<CrmOperationResult> MarkOptOutAsync(CrmOptOutRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOperationResult(true));
}

internal sealed class RecordingSmsSender : ISmsSender
{
    public List<SmsMessageRequest> Requests { get; } = [];

    public Task<SmsSendResult> SendSmsAsync(SmsMessageRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new SmsSendResult(true, "sms-1"));
    }
}

internal sealed class RecordingEmailSender : IEmailSender
{
    public List<EmailMessageRequest> Requests { get; } = [];

    public Task<EmailSendResult> SendEmailAsync(EmailMessageRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new EmailSendResult(true, "email-1"));
    }
}

internal sealed class RecordingEventLogger : IEventLogger
{
    public Task LogEventAsync(
        string eventName,
        IReadOnlyDictionary<string, string> properties,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class FollowUpTenantProvider : ITenantConfigurationProvider
{
    public Task<TenantConfiguration> GetTenantConfigurationAsync(string tenantId, CancellationToken cancellationToken)
    {
        return Task.FromResult(new TenantConfiguration(
            new TenantId(tenantId),
            new VerticalId("insurance-agents"),
            "RNM",
            "America/Chicago",
            new ServiceAreaConfiguration(["*"], [], null),
            new ProviderConfiguration("AzureTable", "GoogleCalendar", "Twilio", "SendGrid"),
            new SecretNameConfiguration("crm", "booking", "voice", "twilioSid", "twilioToken", "email"),
            new CommunicationConfiguration(
                "+15550001111",
                "info@example.com",
                new ConfirmationTemplateConfiguration("booking sms")),
            FollowUps: new FollowUpAutomationConfiguration(
                Enabled: true,
                StalenessCutoffMinutes: 120,
                MaxFollowUpsPerContactPerDay: 2,
                Sequences:
                [
                    new FollowUpSequenceConfiguration(
                        "lead-needs-follow-up",
                        FollowUpTriggers.LeadFollowUpRequired,
                        [
                            new FollowUpStepConfiguration(
                                30,
                                FollowUpChannels.Sms,
                                SmsBodyTemplate: "Hi {{customerName}}, campaign {{attr.campaignId}} needs follow-up. Reply STOP to opt out."),
                            new FollowUpStepConfiguration(
                                1440,
                                FollowUpChannels.Email,
                                EmailSubjectTemplate: "Following up",
                                EmailBodyTemplate: "Hi {{customerName}}, reference {{correlationId}}.")
                        ],
                        [
                            new FollowUpStopConditionConfiguration(
                                "leadStatus",
                                ["Booked", "AppointmentScheduled", "appointment_booked"]),
                            new FollowUpStopConditionConfiguration(ConsentStatus: CrmConsentStatuses.OptedOut)
                        ])
                ])));
    }
}
