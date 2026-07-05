using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Reporting;
using RNM.Platform.Application.Reporting;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.Reporting;

public sealed class PilotReportingServiceTests
{
    private static readonly DateTimeOffset From = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 7, 7, 23, 59, 59, TimeSpan.Zero);

    [Fact]
    public async Task GetPilotReportAsync_ComputesSpeedToContactBuckets()
    {
        var service = CreateService(new ReportingDataSet(
            [
                Contact("c1", From.AddHours(1), lastContactedAt: From.AddHours(1).AddSeconds(30), attempts: 1),
                Contact("c2", From.AddHours(2), lastContactedAt: From.AddHours(2).AddSeconds(120), attempts: 1),
                Contact("c3", From.AddHours(3), lastContactedAt: From.AddHours(3).AddSeconds(600), attempts: 1)
            ],
            [],
            []));

        var report = await service.GetPilotReportAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(3, report.SpeedToContact.ContactedLeadCount);
        Assert.Equal(250, report.SpeedToContact.AverageSecondsToContact);
        Assert.Equal(33.33m, report.SpeedToContact.WithinOneMinutePercent);
        Assert.Equal(33.33m, report.SpeedToContact.WithinFiveMinutesPercent);
        Assert.Equal(33.33m, report.SpeedToContact.OverFiveMinutesPercent);
    }

    [Fact]
    public async Task GetPilotReportAsync_ComputesFunnelRevenueActivityAndBaseline()
    {
        var service = CreateService(
            new ReportingDataSet(
                [
                    Contact("c1", From.AddHours(1), lastContactedAt: From.AddHours(1).AddSeconds(30), attempts: 2),
                    Contact("c2", From.AddHours(2), lastContactedAt: From.AddHours(2).AddSeconds(90), attempts: 1, leadStatus: "reactivated"),
                    Contact("c3", From.AddHours(3), attempts: 0, tags: "old_database"),
                    Contact("c4", From.AddHours(4), attempts: 0)
                ],
                [
                    Booking("c1", From.AddHours(6)),
                    Booking("c2", From.AddHours(7)),
                    Booking("c3", From.AddHours(8))
                ],
                [
                    Timeline("c1", From.AddHours(2), "outbound.attempt_recorded", new Dictionary<string, string> { ["outcome"] = "voicemail" }),
                    Timeline("c1", From.AddHours(3), "outbound.attempt_recorded", new Dictionary<string, string> { ["outcome"] = "answered" }),
                    Timeline("c2", From.AddHours(4), "outbound.attempt_recorded", new Dictionary<string, string> { ["outcome"] = "no_answer" }),
                    Timeline("c1", From.AddHours(5), "sms.sent"),
                    Timeline("c1", From.AddHours(5).AddMinutes(1), "email.sent")
                ]),
            new ReportingConfiguration(
                CloseRate: 0.25m,
                AvgCommissionValue: 10000m,
                new ReportingBaselineConfiguration(
                    LeadsContactedPerWeek: 1,
                    AvgContactTimeSeconds: 300,
                    AppointmentsPerWeek: 1)));

        var report = await service.GetPilotReportAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(4, report.Funnel.TotalLeads);
        Assert.Equal(2, report.Funnel.LeadsContacted);
        Assert.Equal(50, report.Funnel.ContactRatePercent);
        Assert.Equal(3, report.Funnel.AppointmentsBooked);
        Assert.Equal(150, report.Funnel.BookingRatePercent);
        Assert.Equal(2, report.Funnel.ReactivatedBooked);
        Assert.Equal(7500, report.ProjectedRevenue.ProjectedRevenue);
        Assert.Equal(5000, report.ProjectedRevenue.ReactivatedProjectedRevenue);
        Assert.Equal("projected", report.ProjectedRevenue.Label);
        Assert.Equal(3, report.Activity.TotalOutboundAttempts);
        Assert.Equal(1, report.Activity.SmsSent);
        Assert.Equal(1, report.Activity.EmailsSent);
        Assert.Equal(1, report.Activity.FollowUpsExecuted);
        Assert.Equal(1, report.Activity.NoAnswerCount);
        Assert.Equal(1, report.Activity.VoicemailCount);
        Assert.Equal(1, report.Baseline.LeadsContactedPerWeek.Delta);
        Assert.Equal(-240, report.Baseline.AvgContactTimeSeconds.Delta);
        Assert.Equal(2, report.Baseline.AppointmentsPerWeek.Delta);
        Assert.Contains("Projected commission: $7,500", report.Summary);
    }

    [Fact]
    public async Task GetPilotReportAsync_ReturnsSafeZeros_WhenNoDataAndBaselineMissing()
    {
        var service = CreateService(new ReportingDataSet([], [], []), reporting: null);

        var report = await service.GetPilotReportAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(0, report.Funnel.TotalLeads);
        Assert.Equal(0, report.SpeedToContact.AverageSecondsToContact);
        Assert.Equal(0, report.ProjectedRevenue.ProjectedRevenue);
        Assert.Equal("baseline_not_set", report.Baseline.Label);
        Assert.Null(report.Baseline.LeadsContactedPerWeek.Baseline);
        Assert.Contains("baseline not set", report.Summary);
    }

    [Fact]
    public async Task GetPilotReportAsync_UsesTenantScopedReadRequest()
    {
        var adapter = new RecordingReportingReadAdapter(new ReportingDataSet([], [], []));
        var service = CreateService(adapter, new ReportingConfiguration());

        await service.GetPilotReportAsync(CreateRequest(tenantId: "tenant-a"), CancellationToken.None);

        Assert.Equal("tenant-a", adapter.LastRequest?.TenantId);
        Assert.Equal(From, adapter.LastRequest?.From);
        Assert.Equal(To, adapter.LastRequest?.To);
    }

    private static PilotReportingService CreateService(
        ReportingDataSet dataSet,
        ReportingConfiguration? reporting = null) =>
        CreateService(new RecordingReportingReadAdapter(dataSet), reporting);

    private static PilotReportingService CreateService(
        RecordingReportingReadAdapter adapter,
        ReportingConfiguration? reporting = null)
    {
        return new PilotReportingService(
            adapter,
            new StubTenantConfigurationProvider(reporting),
            new RecordingEventLogger());
    }

    private static PilotReportRequest CreateRequest(string tenantId = "tenant-a") =>
        new(tenantId, "corr-123", From, To);

    private static ReportingContactRecord Contact(
        string contactId,
        DateTimeOffset createdAt,
        DateTimeOffset? lastContactedAt = null,
        int attempts = 0,
        string? leadStatus = null,
        string? tags = null) =>
        new("tenant-a", contactId, createdAt, lastContactedAt, attempts, leadStatus, tags);

    private static ReportingBookingRecord Booking(string contactId, DateTimeOffset createdAt) =>
        new("tenant-a", contactId, createdAt, StartsAt: null, BookingState: "Booked");

    private static ReportingTimelineEventRecord Timeline(
        string contactId,
        DateTimeOffset createdAt,
        string eventType,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new("tenant-a", contactId, createdAt, eventType, metadata ?? new Dictionary<string, string>());

    private sealed class RecordingReportingReadAdapter(ReportingDataSet dataSet) : IReportingReadAdapter
    {
        public PilotReportRequest? LastRequest { get; private set; }

        public Task<ReportingDataSet> GetPilotReportingDataAsync(
            PilotReportRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(dataSet);
        }
    }

    private sealed class StubTenantConfigurationProvider(ReportingConfiguration? reporting) : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TenantConfiguration(
                new TenantId(tenantId),
                new VerticalId("real-estate"),
                "Tenant",
                "America/Chicago",
                new ServiceAreaConfiguration(["*"], [], null),
                new ProviderConfiguration("AzureTable", "GoogleCalendar", "Twilio", "SendGrid"),
                new SecretNameConfiguration("crm", "booking", "voice", "sid", "token", "email"),
                new CommunicationConfiguration(
                    "+15550001000",
                    "ops@example.com",
                    new ConfirmationTemplateConfiguration("sms")),
                reporting));
        }
    }

    private sealed class RecordingEventLogger : IEventLogger
    {
        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
