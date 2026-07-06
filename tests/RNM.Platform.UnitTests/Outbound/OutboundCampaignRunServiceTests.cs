using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Outbound;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.Outbound;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.Outbound;

public sealed class OutboundCampaignRunServiceTests
{
    [Fact]
    public async Task RunAsync_ReturnsSafeError_WhenOutboundConfigIsMissing()
    {
        var outboundAdapter = new RecordingOutboundCallAdapter();
        var service = CreateService(
            new StubTenantConfigurationProvider(CreateTenantConfiguration(outbound: null)),
            new StubCrmAdapter([CreateLead("contact-1")]),
            outboundAdapter);

        var result = await service.RunAsync(
            new OutboundCampaignRunRequest("tenant-a", "campaign-a", "corr-1"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("missing_configuration", result.FailureReason);
        Assert.Empty(outboundAdapter.Requests);
    }

    [Fact]
    public async Task RunAsync_SkipsLeadOutsideTcpaWindow_UsingTenantTimezoneWhenLeadTimezoneMissing()
    {
        var outboundAdapter = new RecordingOutboundCallAdapter();
            var eventLogger = new RecordingEventLogger();
        var service = CreateService(
            new StubTenantConfigurationProvider(CreateTenantConfiguration(CreateOutboundConfiguration())),
            new StubCrmAdapter([CreateLead("contact-1")]),
            outboundAdapter,
            eventLogger,
            utcNow: () => new DateTimeOffset(2026, 7, 5, 3, 0, 0, TimeSpan.Zero));

        var result = await service.RunAsync(
            new OutboundCampaignRunRequest("tenant-a", "campaign-a", "corr-1"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.StartedCallCount);
        Assert.Equal(1, result.SkippedLeadCount);
        Assert.Equal("skipped_outside_tcpa_window", result.Items.Single().Outcome);
        Assert.Equal("tenant", result.Items.Single().TimeZoneBasis);
        Assert.Equal("America/Chicago", result.Items.Single().TimeZone);
        Assert.Empty(outboundAdapter.Requests);
        Assert.Contains(eventLogger.Events, item =>
            item.EventName == TelemetryEventNames.OutboundCallSkipped
            && item.Properties.GetValueOrDefault("timezoneBasis") == "tenant");
    }

    [Fact]
    public async Task RunAsync_SkipsOptedOutLead_EvenIfCrmReturnsIt()
    {
        var outboundAdapter = new RecordingOutboundCallAdapter();
        var service = CreateService(
            new StubTenantConfigurationProvider(CreateTenantConfiguration(CreateOutboundConfiguration())),
            new StubCrmAdapter([CreateLead("contact-1", consentStatus: CrmConsentStatuses.OptedOut)]),
            outboundAdapter);

        var result = await service.RunAsync(
            new OutboundCampaignRunRequest("tenant-a", "campaign-a", "corr-1"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("skipped_opted_out", result.Items.Single().Outcome);
        Assert.Empty(outboundAdapter.Requests);
    }

    [Fact]
    public async Task RunAsync_RespectsPacingLimitAndDelay()
    {
        var outboundAdapter = new RecordingOutboundCallAdapter();
        var delays = new List<TimeSpan>();
        var service = CreateService(
            new StubTenantConfigurationProvider(
                CreateTenantConfiguration(
                    outbound: CreateOutboundConfiguration(
                        pacing: new OutboundPacingConfiguration(2, 5)))),
            new StubCrmAdapter([CreateLead("contact-1"), CreateLead("contact-2")]),
            outboundAdapter,
            delay: (timeSpan, _) =>
            {
                delays.Add(timeSpan);
                return Task.CompletedTask;
            });

        var result = await service.RunAsync(
            new OutboundCampaignRunRequest("tenant-a", "campaign-a", "corr-1"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.StartedCallCount);
        Assert.Equal(2, outboundAdapter.Requests.Count);
        Assert.Single(delays);
        Assert.Equal(TimeSpan.FromSeconds(5), delays.Single());
    }

    private static OutboundCampaignRunService CreateService(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ICrmAdapter crmAdapter,
        IOutboundCallAdapter outboundCallAdapter,
        IEventLogger? eventLogger = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        return new OutboundCampaignRunService(
            tenantConfigurationProvider,
            crmAdapter,
            outboundCallAdapter,
            eventLogger ?? new RecordingEventLogger(),
            utcNow ?? (() => new DateTimeOffset(2026, 7, 5, 15, 0, 0, TimeSpan.Zero)),
            delay ?? ((_, _) => Task.CompletedTask));
    }

    private static TenantConfiguration CreateTenantConfiguration(
        OutboundVoiceConfiguration? outbound = null)
    {
        return new TenantConfiguration(
            new TenantId("tenant-a"),
            new VerticalId("real-estate"),
            "Tenant A",
            "America/Chicago",
            new ServiceAreaConfiguration(["*"], [], null),
            new ProviderConfiguration("AzureTable", "GoogleCalendar", "Twilio", "SendGrid"),
            new SecretNameConfiguration("crm", "booking", "voice", "sid", "token", "email"),
            new CommunicationConfiguration("+15550001000", null, new ConfirmationTemplateConfiguration("sms")),
            new ReportingConfiguration(),
            outbound is null ? null : new VoiceConfiguration(outbound));
    }

    private static OutboundVoiceConfiguration CreateOutboundConfiguration(
        OutboundPacingConfiguration? pacing = null)
    {
        return new OutboundVoiceConfiguration(
            "vapi-key",
            "https://api.vapi.ai/",
            "assistant-1",
            "phone-1",
            "https://platform.example.com/",
            pacing ?? new OutboundPacingConfiguration(1, 0),
            3,
            new TcpaWindowConfiguration(8, 21));
    }

    private static CrmContactRecord CreateLead(
        string providerContactId,
        string consentStatus = CrmConsentStatuses.OptIn)
    {
        return new CrmContactRecord(
            "tenant-a",
            providerContactId,
            "+15551234567",
            "lead@example.com",
            "Lead One",
            "77002",
            new Dictionary<string, string>
            {
                [CrmContactAttributeNames.CampaignId] = "campaign-a",
                [CrmContactAttributeNames.ConsentStatus] = consentStatus
            });
    }

    private sealed class StubTenantConfigurationProvider : ITenantConfigurationProvider
    {
        private readonly TenantConfiguration configuration;

        public StubTenantConfigurationProvider(TenantConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(configuration);
    }

    private sealed class StubCrmAdapter : ICrmAdapter
    {
        private readonly List<CrmContactRecord> leads;

        public StubCrmAdapter(IEnumerable<CrmContactRecord> leads)
        {
            this.leads = leads.ToList();
        }

        public Task<CrmNextLeadToCallResult> GetNextLeadToCallAsync(
            CrmNextLeadToCallRequest request,
            CancellationToken cancellationToken)
        {
            var excluded = request.ExcludedProviderContactIds.ToHashSet(StringComparer.Ordinal);
            var lead = leads.FirstOrDefault(item => !excluded.Contains(item.ProviderContactId));
            return Task.FromResult(new CrmNextLeadToCallResult(true, lead));
        }

        public Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(CrmContactLookupRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmContactUpsertResult> UpsertContactAsync(CrmContactUpsertRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> AddInteractionNoteAsync(CrmInteractionNoteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> ApplyTagsAsync(CrmTagRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> LinkBookingToContactAsync(CrmBookingLinkRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> AddTimelineEventAsync(CrmTimelineEventRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkFollowUpRequiredAsync(CrmFollowUpRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmLeadQueryResult> GetLeadsByStatusAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmLeadQueryResult> GetLeadsByCampaignAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> RecordOutboundAttemptAsync(CrmOutboundAttemptRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkLeadReactivatedAsync(CrmLeadReactivationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkOptOutAsync(CrmOptOutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingOutboundCallAdapter : IOutboundCallAdapter
    {
        public List<OutboundCallStartRequest> Requests { get; } = [];

        public Task<OutboundCallStartResult> StartCallAsync(
            OutboundCallStartRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new OutboundCallStartResult(true, $"call-{Requests.Count}", "queued"));
        }
    }

    private sealed class RecordingEventLogger : IEventLogger
    {
        public List<(string EventName, IReadOnlyDictionary<string, string> Properties)> Events { get; } = [];

        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken)
        {
            Events.Add((eventName, properties));
            return Task.CompletedTask;
        }
    }
}
