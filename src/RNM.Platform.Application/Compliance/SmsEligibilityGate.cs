using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;

namespace RNM.Platform.Application.Compliance;

public interface ISmsEligibilityGate
{
    Task<SmsEligibilityResult> EvaluateAsync(
        SmsEligibilityRequest request,
        CancellationToken cancellationToken);
}

public sealed record SmsEligibilityRequest(
    string TenantId,
    string CorrelationId,
    SmsMessageCategory Category,
    string? ProviderContactId,
    string? PhoneNumber)
{
    public bool IsRetry { get; init; }

    public DateTimeOffset? OriginalRequestedAt { get; init; }

    /// <summary>
    /// Contact already loaded by the caller for this phone number; avoids a second CRM lookup on the booking path.
    /// </summary>
    public CrmContactRecord? KnownContact { get; init; }
}

public sealed record SmsEligibilityResult(
    bool IsEligible,
    SmsEligibilityProof? Proof = null,
    SmsEligibilitySkipReason? SkipReason = null);

public enum SmsEligibilitySkipReason
{
    MissingContactId = 0,
    ContactNotFound = 1,
    ContactMismatch = 2,
    ContactOptedOut = 3,
    ConsentNotGranted = 4,
    OutsideSendWindow = 5,
    RetryMissingTimestamp = 6,
    RetryStale = 7,
    DependencyUnavailable = 8
}

public sealed class SmsEligibilityGate : ISmsEligibilityGate
{
    private readonly ICrmAdapter crmAdapter;
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ISendWindowPolicy sendWindowPolicy;
    private readonly IEventLogger eventLogger;
    private readonly TimeProvider timeProvider;

    public SmsEligibilityGate(
        ICrmAdapter crmAdapter,
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISendWindowPolicy sendWindowPolicy,
        IEventLogger eventLogger,
        TimeProvider timeProvider)
    {
        this.crmAdapter = crmAdapter;
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.sendWindowPolicy = sendWindowPolicy;
        this.eventLogger = eventLogger;
        this.timeProvider = timeProvider;
    }

    public async Task<SmsEligibilityResult> EvaluateAsync(
        SmsEligibilityRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var tenant = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
            .ConfigureAwait(false);

        if (request.IsRetry)
        {
            if (request.OriginalRequestedAt is not { } originalRequestedAt)
            {
                return await SkipAsync(request, SmsEligibilitySkipReason.RetryMissingTimestamp, request.ProviderContactId, cancellationToken)
                    .ConfigureAwait(false);
            }

            var cutoff = TimeSpan.FromMinutes(tenant.Communication.EffectiveSmsRetryStalenessCutoffMinutes);
            if (now - originalRequestedAt > cutoff)
            {
                return await SkipAsync(request, SmsEligibilitySkipReason.RetryStale, request.ProviderContactId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (request.Category is SmsMessageCategory.InternalOperational)
        {
            return Eligible(request);
        }

        CrmContactLookupResult lookup;
        try
        {
            lookup = request.KnownContact is { } knownContact
                ? new CrmContactLookupResult(true, knownContact.ProviderContactId) { Contact = knownContact }
                : await crmAdapter
                    .FindContactByPhoneOrEmailAsync(
                        new CrmContactLookupRequest(
                            request.TenantId,
                            request.CorrelationId,
                            request.PhoneNumber,
                            Email: null),
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A booking confirmation answers the caller's own request; Twilio still enforces carrier-level STOP.
            return request.Category is SmsMessageCategory.BookingConfirmation
                ? Eligible(request)
                : await SkipAsync(request, SmsEligibilitySkipReason.DependencyUnavailable, request.ProviderContactId, cancellationToken)
                    .ConfigureAwait(false);
        }

        if (request.Category is SmsMessageCategory.BookingConfirmation)
        {
            // Transactional: only an explicit opt-out on this number blocks it, so a CRM outage never drops the confirmation.
            return lookup.Found
                && string.Equals(lookup.Contact?.SmsConsentStatus, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase)
                    ? await SkipAsync(request, SmsEligibilitySkipReason.ContactOptedOut, request.ProviderContactId ?? lookup.ProviderContactId, cancellationToken)
                        .ConfigureAwait(false)
                    : Eligible(request);
        }

        var timelineContactId = request.ProviderContactId ?? lookup.ProviderContactId;
        if (string.IsNullOrWhiteSpace(request.ProviderContactId))
        {
            return await SkipAsync(request, SmsEligibilitySkipReason.MissingContactId, timelineContactId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!lookup.Found || lookup.Contact is null)
        {
            return await SkipAsync(request, SmsEligibilitySkipReason.ContactNotFound, timelineContactId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.Equals(lookup.Contact.ProviderContactId, request.ProviderContactId, StringComparison.Ordinal))
        {
            return await SkipAsync(request, SmsEligibilitySkipReason.ContactMismatch, timelineContactId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(lookup.Contact.SmsConsentStatus, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase))
        {
            return await SkipAsync(request, SmsEligibilitySkipReason.ContactOptedOut, timelineContactId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (RequiresExplicitSmsConsent(request.Category)
            && !string.Equals(lookup.Contact.SmsConsentStatus, CrmConsentStatuses.OptIn, StringComparison.OrdinalIgnoreCase))
        {
            return await SkipAsync(request, SmsEligibilitySkipReason.ConsentNotGranted, timelineContactId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (AppliesSendWindow(request.Category))
        {
            var window = sendWindowPolicy.Evaluate(
                lookup.Contact,
                tenant.TimeZone,
                tenant.Voice?.Outbound?.TcpaWindow,
                now);
            if (!window.IsAllowed)
            {
                return await SkipAsync(request, SmsEligibilitySkipReason.OutsideSendWindow, timelineContactId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return Eligible(request);
    }

    private static bool RequiresExplicitSmsConsent(SmsMessageCategory category) =>
        category is SmsMessageCategory.ClassRegistrationConfirmation
            or SmsMessageCategory.ClassReminder
            or SmsMessageCategory.MarketingFollowUp;

    private static bool AppliesSendWindow(SmsMessageCategory category) =>
        category is SmsMessageCategory.ClassReminder
            or SmsMessageCategory.AppointmentReminder
            or SmsMessageCategory.MarketingFollowUp;

    private static SmsEligibilityResult Eligible(SmsEligibilityRequest request) =>
        new(
            true,
            new SmsEligibilityProof(request.TenantId, request.CorrelationId, request.Category));

    private async Task<SmsEligibilityResult> SkipAsync(
        SmsEligibilityRequest request,
        SmsEligibilitySkipReason reason,
        string? providerContactId,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["category"] = request.Category.ToString(),
            ["reason"] = reason.ToString(),
            ["isRetry"] = request.IsRetry.ToString()
        };

        try
        {
            await crmAdapter.AddTimelineEventAsync(
                    new CrmTimelineEventRequest(
                        request.TenantId,
                        request.CorrelationId,
                        providerContactId,
                        ProviderBookingId: null,
                        CrmTimelineEventTypes.SmsSkipped,
                        "SmsEligibilityGate",
                        "SMS skipped by eligibility policy.",
                        metadata),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A timeline failure must not turn a denied SMS into an allowed send.
        }

        try
        {
            await eventLogger.LogEventAsync(
                    TelemetryEventNames.SmsEligibilitySkipped,
                    new SafeTelemetryProperties()
                        .Add("tenantId", request.TenantId)
                        .Add("correlationId", request.CorrelationId)
                        .Add("category", request.Category.ToString())
                        .Add("reason", reason.ToString())
                        .Add("isRetry", request.IsRetry.ToString())
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Eligibility remains denied when best-effort telemetry is unavailable.
        }

        return new SmsEligibilityResult(false, SkipReason: reason);
    }
}
