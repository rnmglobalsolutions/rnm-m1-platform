using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Reporting;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Reporting;

public sealed class PilotReportingService
{
    private const string BaselineNotSet = "baseline_not_set";
    private readonly IReportingReadAdapter reportingReadAdapter;
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly IEventLogger eventLogger;

    public PilotReportingService(
        IReportingReadAdapter reportingReadAdapter,
        ITenantConfigurationProvider tenantConfigurationProvider,
        IEventLogger eventLogger)
    {
        this.reportingReadAdapter = reportingReadAdapter;
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.eventLogger = eventLogger;
    }

    public async Task<PilotReportResult> GetPilotReportAsync(
        PilotReportRequest request,
        CancellationToken cancellationToken)
    {
        await LogAsync("reporting.pilot_requested", request, "requested", cancellationToken).ConfigureAwait(false);

        var tenant = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
            .ConfigureAwait(false);
        var data = await reportingReadAdapter
            .GetPilotReportingDataAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var contactsInRange = data.Contacts
            .Where(contact => IsInRange(contact.CreatedAt, request.From, request.To))
            .ToArray();
        var contactById = data.Contacts
            .GroupBy(contact => contact.ProviderContactId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var outboundEvents = data.TimelineEvents
            .Where(IsOutboundAttempt)
            .ToArray();
        var outboundContactIds = outboundEvents
            .Where(evt => !string.IsNullOrWhiteSpace(evt.ProviderContactId))
            .Select(evt => evt.ProviderContactId)
            .ToHashSet(StringComparer.Ordinal);

        var speed = BuildSpeedToContact(contactsInRange);
        var funnel = BuildFunnel(contactsInRange, data.Bookings, contactById, outboundContactIds);
        var revenue = BuildProjectedRevenue(funnel, tenant.Reporting);
        var activity = BuildActivity(outboundEvents, data.TimelineEvents);
        var baseline = BuildBaseline(funnel, speed, tenant.Reporting?.Baseline);
        var summary = BuildSummary(funnel, speed, revenue, baseline);

        await LogAsync("reporting.pilot_completed", request, "completed", cancellationToken).ConfigureAwait(false);

        return new PilotReportResult(
            request.TenantId,
            request.CorrelationId,
            request.From,
            request.To,
            speed,
            funnel,
            revenue,
            activity,
            baseline,
            summary);
    }

    private static SpeedToContactReport BuildSpeedToContact(
        IReadOnlyCollection<ReportingContactRecord> contacts)
    {
        var durations = contacts
            .Where(contact => contact.CreatedAt.HasValue && contact.LastContactedAt.HasValue)
            .Select(contact => (contact.LastContactedAt!.Value - contact.CreatedAt!.Value).TotalSeconds)
            .Where(seconds => seconds >= 0)
            .ToArray();
        if (durations.Length == 0)
        {
            return new SpeedToContactReport(0, 0, 0, 0, 0, "no_contact_data");
        }

        return new SpeedToContactReport(
            durations.Length,
            Round((decimal)durations.Average()),
            Percent(durations.Count(seconds => seconds <= 60), durations.Length),
            Percent(durations.Count(seconds => seconds > 60 && seconds <= 300), durations.Length),
            Percent(durations.Count(seconds => seconds > 300), durations.Length),
            "computed");
    }

    private static FunnelReport BuildFunnel(
        IReadOnlyCollection<ReportingContactRecord> contacts,
        IReadOnlyCollection<ReportingBookingRecord> bookings,
        IReadOnlyDictionary<string, ReportingContactRecord> contactById,
        IReadOnlySet<string> outboundContactIds)
    {
        var totalLeads = contacts.Count;
        var contacted = contacts.Count(contact =>
            contact.OutboundAttemptCount > 0 || outboundContactIds.Contains(contact.ProviderContactId));
        var booked = bookings.Count(IsBooked);
        var reactivatedBooked = bookings.Count(booking =>
            !string.IsNullOrWhiteSpace(booking.ProviderContactId)
            && contactById.TryGetValue(booking.ProviderContactId, out var contact)
            && IsReactivated(contact));

        return new FunnelReport(
            totalLeads,
            contacted,
            Percent(contacted, totalLeads),
            booked,
            Percent(booked, contacted),
            reactivatedBooked,
            totalLeads == 0 ? "no_lead_data" : "computed");
    }

    private static ProjectedRevenueReport BuildProjectedRevenue(
        FunnelReport funnel,
        ReportingConfiguration? configuration)
    {
        var closeRate = configuration?.CloseRate ?? 0;
        var avgCommission = configuration?.AvgCommissionValue ?? 0;
        return new ProjectedRevenueReport(
            "projected",
            closeRate,
            avgCommission,
            Round(funnel.AppointmentsBooked * closeRate * avgCommission),
            Round(funnel.ReactivatedBooked * closeRate * avgCommission));
    }

    private static ActivitySummaryReport BuildActivity(
        IReadOnlyCollection<ReportingTimelineEventRecord> outboundEvents,
        IReadOnlyCollection<ReportingTimelineEventRecord> timelineEvents)
    {
        var followUps = outboundEvents
            .Where(evt => !string.IsNullOrWhiteSpace(evt.ProviderContactId))
            .GroupBy(evt => evt.ProviderContactId, StringComparer.Ordinal)
            .Sum(group => Math.Max(0, group.Count() - 1));

        return new ActivitySummaryReport(
            outboundEvents.Count,
            timelineEvents.Count(evt => string.Equals(evt.EventType, "sms.sent", StringComparison.OrdinalIgnoreCase)),
            timelineEvents.Count(evt => string.Equals(evt.EventType, "email.sent", StringComparison.OrdinalIgnoreCase)),
            followUps,
            outboundEvents.Count(evt => IsOutcome(evt, "no_answer", "no-answer", "no answer")),
            outboundEvents.Count(evt => IsOutcome(evt, "voicemail", "voice_mail", "voice-mail")),
            timelineEvents.Count == 0 ? "no_activity_data" : "computed");
    }

    private static BaselineComparison BuildBaseline(
        FunnelReport funnel,
        SpeedToContactReport speed,
        ReportingBaselineConfiguration? baseline)
    {
        if (baseline is null || !baseline.IsSet)
        {
            return new BaselineComparison(
                BaselineNotSet,
                new BaselineMetric(funnel.LeadsContacted, null, null),
                new BaselineMetric(speed.AverageSecondsToContact, null, null),
                new BaselineMetric(funnel.AppointmentsBooked, null, null));
        }

        return new BaselineComparison(
            "computed",
            Compare(funnel.LeadsContacted, baseline.LeadsContactedPerWeek),
            Compare(speed.AverageSecondsToContact, baseline.AvgContactTimeSeconds),
            Compare(funnel.AppointmentsBooked, baseline.AppointmentsPerWeek));
    }

    private static string BuildSummary(
        FunnelReport funnel,
        SpeedToContactReport speed,
        ProjectedRevenueReport revenue,
        BaselineComparison baseline)
    {
        var baselineText = baseline.Label == BaselineNotSet
            ? "baseline not set"
            : $"baseline ~{baseline.LeadsContactedPerWeek.Baseline:0}/week";
        return $"Contacted {funnel.LeadsContacted} leads ({baselineText}), avg {speed.AverageSecondsToContact:0}s to first contact, booked {funnel.AppointmentsBooked} appointments, revived {funnel.ReactivatedBooked} from reactivated leads. Projected commission: {revenue.ProjectedRevenue:C0}.";
    }

    private async Task LogAsync(
        string eventName,
        PilotReportRequest request,
        string outcome,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", request.CorrelationId)
            .Add("tenantId", request.TenantId)
            .Add("outcome", outcome)
            .ToDictionary();

        try
        {
            await eventLogger.LogEventAsync(eventName, properties, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Reporting telemetry is best-effort.
        }
    }

    private static bool IsInRange(DateTimeOffset? value, DateTimeOffset from, DateTimeOffset to) =>
        value.HasValue && value.Value >= from && value.Value <= to;

    private static bool IsOutboundAttempt(ReportingTimelineEventRecord timelineEvent) =>
        string.Equals(timelineEvent.EventType, "outbound.attempt_recorded", StringComparison.OrdinalIgnoreCase);

    private static bool IsBooked(ReportingBookingRecord booking) =>
        string.IsNullOrWhiteSpace(booking.BookingState)
        || string.Equals(booking.BookingState, "Booked", StringComparison.OrdinalIgnoreCase);

    private static bool IsReactivated(ReportingContactRecord contact) =>
        string.Equals(contact.LeadStatus, "reactivated", StringComparison.OrdinalIgnoreCase)
        || ContainsTag(contact.Tags, "reactivation")
        || ContainsTag(contact.Tags, "reactivated")
        || ContainsTag(contact.Tags, "old_database");

    private static bool ContainsTag(string? tags, string expectedTag) =>
        (tags ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(tag => string.Equals(tag, expectedTag, StringComparison.OrdinalIgnoreCase));

    private static bool IsOutcome(
        ReportingTimelineEventRecord timelineEvent,
        params string[] expectedOutcomes)
    {
        if (!timelineEvent.Metadata.TryGetValue("outcome", out var outcome))
        {
            return false;
        }

        return expectedOutcomes.Any(expected =>
            string.Equals(outcome, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static BaselineMetric Compare(decimal pilot, int? baseline) =>
        new(pilot, baseline, baseline.HasValue ? pilot - baseline.Value : null);

    private static decimal Percent(int numerator, int denominator) =>
        denominator <= 0 ? 0 : Round(numerator * 100m / denominator);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
