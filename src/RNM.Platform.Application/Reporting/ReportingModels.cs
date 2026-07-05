namespace RNM.Platform.Application.Reporting;

public sealed record PilotReportRequest(
    string TenantId,
    string CorrelationId,
    DateTimeOffset From,
    DateTimeOffset To);

public sealed record PilotReportResult(
    string TenantId,
    string CorrelationId,
    DateTimeOffset From,
    DateTimeOffset To,
    SpeedToContactReport SpeedToContact,
    FunnelReport Funnel,
    ProjectedRevenueReport ProjectedRevenue,
    ActivitySummaryReport Activity,
    BaselineComparison Baseline,
    string Summary);

public sealed record SpeedToContactReport(
    int ContactedLeadCount,
    decimal AverageSecondsToContact,
    decimal WithinOneMinutePercent,
    decimal WithinFiveMinutesPercent,
    decimal OverFiveMinutesPercent,
    string Label);

public sealed record FunnelReport(
    int TotalLeads,
    int LeadsContacted,
    decimal ContactRatePercent,
    int AppointmentsBooked,
    decimal BookingRatePercent,
    int ReactivatedBooked,
    string Label);

public sealed record ProjectedRevenueReport(
    string Label,
    decimal CloseRate,
    decimal AvgCommissionValue,
    decimal ProjectedRevenue,
    decimal ReactivatedProjectedRevenue);

public sealed record ActivitySummaryReport(
    int TotalOutboundAttempts,
    int SmsSent,
    int EmailsSent,
    int FollowUpsExecuted,
    int NoAnswerCount,
    int VoicemailCount,
    string Label);

public sealed record BaselineComparison(
    string Label,
    BaselineMetric LeadsContactedPerWeek,
    BaselineMetric AvgContactTimeSeconds,
    BaselineMetric AppointmentsPerWeek);

public sealed record BaselineMetric(
    decimal Pilot,
    decimal? Baseline,
    decimal? Delta);

public sealed record ReportingDataSet(
    IReadOnlyCollection<ReportingContactRecord> Contacts,
    IReadOnlyCollection<ReportingBookingRecord> Bookings,
    IReadOnlyCollection<ReportingTimelineEventRecord> TimelineEvents);

public sealed record ReportingContactRecord(
    string TenantId,
    string ProviderContactId,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastContactedAt,
    int OutboundAttemptCount,
    string? LeadStatus,
    string? Tags);

public sealed record ReportingBookingRecord(
    string TenantId,
    string ProviderContactId,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? StartsAt,
    string? BookingState);

public sealed record ReportingTimelineEventRecord(
    string TenantId,
    string ProviderContactId,
    DateTimeOffset? CreatedAt,
    string EventType,
    IReadOnlyDictionary<string, string> Metadata);
