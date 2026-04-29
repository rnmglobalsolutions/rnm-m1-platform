using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;

namespace RNM.Platform.Application.Crm;

public sealed class CrmApplicationService
{
    private const int MaxDynamicTagValueLength = 48;

    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly ICrmAdapter crmAdapter;
    private readonly IEventLogger eventLogger;

    public CrmApplicationService(
        ICrmAdapter crmAdapter,
        IEventLogger eventLogger)
    {
        this.crmAdapter = crmAdapter;
        this.eventLogger = eventLogger;
    }

    public async Task<CrmSyncResult> SyncBookedLeadAsync(
        CrmSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.BookingDecision.IsBooked || string.IsNullOrWhiteSpace(request.BookingDecision.ProviderBookingId))
        {
            return await SkipAsync(request, CrmFailureReason.BookingNotCompleted, cancellationToken)
                .ConfigureAwait(false);
        }

        var phoneNumber = request.QualificationResult.LeadData.CallerPhoneNumber;
        var email = GetFieldValue(request, "email");
        if (string.IsNullOrWhiteSpace(phoneNumber) && string.IsNullOrWhiteSpace(email))
        {
            return await SkipAsync(request, CrmFailureReason.MissingContactIdentifier, cancellationToken)
                .ConfigureAwait(false);
        }

        CrmContactLookupResult lookupResult;
        try
        {
            lookupResult = await crmAdapter
                .FindContactByPhoneOrEmailAsync(CreateLookupRequest(request, phoneNumber, email), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return await FailAsync(request, CrmFailureReason.AdapterFailure, null, cancellationToken)
                .ConfigureAwait(false);
        }

        if (lookupResult.Found && string.IsNullOrWhiteSpace(lookupResult.ProviderContactId))
        {
            return await FailAsync(request, CrmFailureReason.AdapterFailure, null, cancellationToken)
                .ConfigureAwait(false);
        }

        await LogAsync(TelemetryEventNames.CrmUpsertRequested, request, null, cancellationToken)
            .ConfigureAwait(false);

        CrmContactUpsertResult upsertResult;
        try
        {
            upsertResult = await crmAdapter
                .UpsertContactAsync(CreateUpsertRequest(request, lookupResult, phoneNumber, email), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return await FailAsync(request, CrmFailureReason.AdapterFailure, lookupResult.ProviderContactId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!upsertResult.Succeeded)
        {
            return await FailAsync(
                    request,
                    upsertResult.FailureReason ?? CrmFailureReason.ContactUpsertFailed,
                    upsertResult.ProviderContactId ?? lookupResult.ProviderContactId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(upsertResult.ProviderContactId))
        {
            return await FailAsync(request, CrmFailureReason.MissingProviderContactId, null, cancellationToken)
                .ConfigureAwait(false);
        }

        var contactId = upsertResult.ProviderContactId;
        await LogAsync(
                upsertResult.Created ? TelemetryEventNames.CrmContactCreated : TelemetryEventNames.CrmContactUpdated,
                request,
                new CrmSyncResult(CrmSyncState.Succeeded, contactId),
                cancellationToken)
            .ConfigureAwait(false);

        var noteResult = await TryOperationAsync(
                () => crmAdapter.AddInteractionNoteAsync(CreateNoteRequest(request, contactId), cancellationToken))
            .ConfigureAwait(false);
        if (!noteResult.Succeeded)
        {
            return await FailAsync(request, noteResult.FailureReason ?? CrmFailureReason.NoteFailed, contactId, cancellationToken)
                .ConfigureAwait(false);
        }

        await LogAsync(TelemetryEventNames.CrmNoteAdded, request, new CrmSyncResult(CrmSyncState.Succeeded, contactId), cancellationToken)
            .ConfigureAwait(false);

        var tagResult = await TryOperationAsync(
                () => crmAdapter.ApplyTagsAsync(CreateTagRequest(request, contactId), cancellationToken))
            .ConfigureAwait(false);
        if (!tagResult.Succeeded)
        {
            return await FailAsync(request, tagResult.FailureReason ?? CrmFailureReason.TagsFailed, contactId, cancellationToken)
                .ConfigureAwait(false);
        }

        await LogAsync(TelemetryEventNames.CrmTagsApplied, request, new CrmSyncResult(CrmSyncState.Succeeded, contactId), cancellationToken)
            .ConfigureAwait(false);

        var linkResult = await TryOperationAsync(
                () => crmAdapter.LinkBookingToContactAsync(
                    CreateBookingLinkRequest(request, contactId, request.BookingDecision.ProviderBookingId),
                    cancellationToken))
            .ConfigureAwait(false);
        if (!linkResult.Succeeded)
        {
            return await FailAsync(request, linkResult.FailureReason ?? CrmFailureReason.BookingLinkFailed, contactId, cancellationToken)
                .ConfigureAwait(false);
        }

        var synced = new CrmSyncResult(CrmSyncState.Succeeded, contactId);
        await LogAsync(TelemetryEventNames.CrmBookingLinked, request, synced, cancellationToken)
            .ConfigureAwait(false);
        return synced;
    }

    private static CrmContactLookupRequest CreateLookupRequest(
        CrmSyncRequest request,
        string? phoneNumber,
        string? email)
    {
        return new CrmContactLookupRequest(
            request.TenantId,
            request.CorrelationId,
            phoneNumber,
            email);
    }

    private static CrmContactUpsertRequest CreateUpsertRequest(
        CrmSyncRequest request,
        CrmContactLookupResult lookupResult,
        string? phoneNumber,
        string? email)
    {
        return new CrmContactUpsertRequest(
            request.TenantId,
            request.VerticalId,
            request.CorrelationId,
            lookupResult.ProviderContactId,
            phoneNumber,
            email,
            GetFieldValue(request, "name"),
            request.QualificationResult.LeadData.ZipCode,
            EmptyAttributes);
    }

    private static CrmInteractionNoteRequest CreateNoteRequest(
        CrmSyncRequest request,
        string providerContactId)
    {
        var note = $"Inbound call booking outcome: {request.BookingDecision.State}; qualification: {request.QualificationResult.State}.";
        return new CrmInteractionNoteRequest(
            request.TenantId,
            request.CorrelationId,
            providerContactId,
            note);
    }

    private static CrmTagRequest CreateTagRequest(
        CrmSyncRequest request,
        string providerContactId)
    {
        var tags = new List<string>
        {
            "Inbound Call",
            "AI Booked",
            $"Booking {request.BookingDecision.State}"
        };

        var safeServiceType = SanitizeTagValue(request.ServiceType);
        if (!string.IsNullOrWhiteSpace(safeServiceType))
        {
            tags.Add($"Service {safeServiceType}");
        }

        return new CrmTagRequest(
            request.TenantId,
            request.CorrelationId,
            providerContactId,
            tags);
    }

    private static CrmBookingLinkRequest CreateBookingLinkRequest(
        CrmSyncRequest request,
        string providerContactId,
        string providerBookingId)
    {
        return new CrmBookingLinkRequest(
            request.TenantId,
            request.CorrelationId,
            providerContactId,
            providerBookingId);
    }

    private static string? GetFieldValue(CrmSyncRequest request, string fieldName)
    {
        return request.QualificationResult.LeadData.Fields.TryGetValue(fieldName, out var value)
            ? value
            : null;
    }

    private static string? SanitizeTagValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var safeCharacters = value
            .Trim()
            .Where(character =>
                !char.IsControl(character)
                && (char.IsLetterOrDigit(character)
                    || char.IsWhiteSpace(character)
                    || character is '-' or '_' or '/' or '&'))
            .Take(MaxDynamicTagValueLength)
            .ToArray();

        var sanitized = new string(safeCharacters).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }

    private async Task<CrmOperationResult> TryOperationAsync(
        Func<Task<CrmOperationResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch
        {
            return new CrmOperationResult(false, CrmFailureReason.AdapterFailure);
        }
    }

    private async Task<CrmSyncResult> FailAsync(
        CrmSyncRequest request,
        CrmFailureReason reason,
        string? providerContactId,
        CancellationToken cancellationToken)
    {
        var failed = new CrmSyncResult(CrmSyncState.Failed, providerContactId, reason);
        await LogAsync(TelemetryEventNames.CrmFailed, request, failed, cancellationToken)
            .ConfigureAwait(false);
        return failed;
    }

    private async Task<CrmSyncResult> SkipAsync(
        CrmSyncRequest request,
        CrmFailureReason reason,
        CancellationToken cancellationToken)
    {
        var skipped = new CrmSyncResult(CrmSyncState.Skipped, ProviderContactId: null, reason);
        await LogAsync(TelemetryEventNames.CrmSkipped, request, skipped, cancellationToken)
            .ConfigureAwait(false);
        return skipped;
    }

    private async Task LogAsync(
        string eventName,
        CrmSyncRequest request,
        CrmSyncResult? result,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", request.CorrelationId)
            .Add("tenantId", request.TenantId)
            .Add("verticalId", request.VerticalId)
            .Add("qualificationState", request.QualificationResult.State.ToString())
            .Add("bookingState", request.BookingDecision.State.ToString())
            .AddIf(result is not null, "crmState", result?.State.ToString())
            .AddIf(result?.FailureReason is not null, "failureReason", result?.FailureReason.ToString())
            .AddIf(!string.IsNullOrWhiteSpace(request.ServiceType), "serviceTypePresent", "true")
            .ToDictionary();

        try
        {
            await eventLogger.LogEventAsync(eventName, properties, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // CRM telemetry is best-effort.
        }
    }
}
