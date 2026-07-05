using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Qualification;

namespace RNM.Platform.Application.Crm;

public sealed class CrmApplicationService
{
    private const int MaxDynamicTagValueLength = 48;

    private static readonly HashSet<string> ContactAttributeFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "serviceNeed",
            "propertyType",
            "serviceAddress",
            "urgency",
            "preferredTime",
            CrmContactAttributeNames.LeadSource,
            CrmContactAttributeNames.CampaignId,
            CrmContactAttributeNames.LeadStatus,
            CrmContactAttributeNames.OutboundAttemptCount,
            CrmContactAttributeNames.LastContactedAt,
            CrmContactAttributeNames.NextFollowUpAt,
            CrmContactAttributeNames.Intent,
            CrmContactAttributeNames.TargetPropertyAddress,
            CrmContactAttributeNames.AssignedAgent,
            CrmContactAttributeNames.ConsentStatus
        };

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
            return await SkipAsync(
                    new CrmPostBookingSyncRequest(
                        request.TenantId,
                        request.VerticalId,
                        request.CorrelationId,
                        request.QualificationResult,
                        request.BookingDecision,
                        ProviderContactId: string.Empty,
                        request.ServiceType),
                    CrmFailureReason.BookingNotCompleted,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var ensureResult = await EnsureContactAsync(
                new CrmContactEnsureRequest(
                    request.TenantId,
                    request.VerticalId,
                    request.CorrelationId,
                    request.QualificationResult),
                cancellationToken)
            .ConfigureAwait(false);
        if (!ensureResult.Succeeded || string.IsNullOrWhiteSpace(ensureResult.ProviderContactId))
        {
            return ensureResult;
        }

        return await CompleteBookedLeadSyncAsync(
                new CrmPostBookingSyncRequest(
                    request.TenantId,
                    request.VerticalId,
                    request.CorrelationId,
                    request.QualificationResult,
                    request.BookingDecision,
                    ensureResult.ProviderContactId,
                    request.ServiceType)
                {
                    PreferredWindow = request.PreferredWindow,
                    TimeZone = request.TimeZone,
                    BookingProvider = request.BookingProvider,
                    Source = request.Source
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CrmSyncResult> EnsureContactAsync(
        CrmContactEnsureRequest request,
        CancellationToken cancellationToken)
    {
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
        catch (OperationCanceledException)
        {
            throw;
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
        catch (OperationCanceledException)
        {
            throw;
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
        await TryAddTimelineEventAsync(
                CreateLeadQualifiedTimelineEvent(request, contactId),
                request,
                cancellationToken)
            .ConfigureAwait(false);

        return new CrmSyncResult(CrmSyncState.Succeeded, contactId);
    }

    public async Task<CrmSyncResult> CompleteBookedLeadSyncAsync(
        CrmPostBookingSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.BookingDecision.IsBooked || string.IsNullOrWhiteSpace(request.BookingDecision.ProviderBookingId))
        {
            return await SkipAsync(request, CrmFailureReason.BookingNotCompleted, cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(request.ProviderContactId))
        {
            return await FailAsync(request, CrmFailureReason.MissingProviderContactId, null, cancellationToken)
                .ConfigureAwait(false);
        }

        var contactId = request.ProviderContactId;
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
        await TryAddTimelineEventAsync(
                CreateBookingTimelineEvent(request, contactId, request.BookingDecision.ProviderBookingId),
                request,
                cancellationToken)
            .ConfigureAwait(false);
        return synced;
    }

    public async Task<CrmOperationResult> MarkFollowUpRequiredAsync(
        CrmFollowUpRequest request,
        CancellationToken cancellationToken)
    {
        CrmOperationResult result;
        try
        {
            result = await crmAdapter
                .MarkFollowUpRequiredAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            result = new CrmOperationResult(false, CrmFailureReason.AdapterFailure);
        }

        if (!result.Succeeded)
        {
            await LogAsync(
                    TelemetryEventNames.CrmFailed,
                    request.TenantId,
                    request.CorrelationId,
                    request.ProviderContactId,
                    result.FailureReason ?? CrmFailureReason.AdapterFailure,
                    cancellationToken)
                .ConfigureAwait(false);
            return result;
        }

        await LogAsync(
                TelemetryEventNames.CrmFollowUpRequired,
                request.TenantId,
                request.CorrelationId,
                request.ProviderContactId,
                null,
                cancellationToken)
            .ConfigureAwait(false);

        await TryAddTimelineEventAsync(
                new CrmTimelineEventRequest(
                    request.TenantId,
                    request.CorrelationId,
                    request.ProviderContactId,
                    ProviderBookingId: null,
                    CrmTimelineEventTypes.FollowUpRequired,
                    Source: "InboundVoice",
                    Summary: $"Follow-up required: {request.Reason}",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["leadStatus"] = request.LeadStatus,
                        ["followUpReason"] = request.Reason
                    }),
                request.TenantId,
                request.CorrelationId,
                request.ProviderContactId,
                cancellationToken)
            .ConfigureAwait(false);

        return result;
    }

    private static CrmContactLookupRequest CreateLookupRequest(
        CrmContactEnsureRequest request,
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
        CrmContactEnsureRequest request,
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
            CreateContactAttributes(request))
        {
            LeadStatus = CrmLeadStatuses.Qualified,
            NeedsFollowUp = false,
            LastInteractionAt = DateTimeOffset.UtcNow
        };
    }

    private static CrmInteractionNoteRequest CreateNoteRequest(
        CrmPostBookingSyncRequest request,
        string providerContactId)
    {
        var selectedSlot = request.BookingDecision.SelectedSlot;
        var noteLines = new List<string>
        {
            "AI booked appointment.",
            $"Booking state: {request.BookingDecision.State}",
            $"Qualification: {request.QualificationResult.State}"
        };
        AddNoteLine(noteLines, "Service", request.ServiceType ?? GetFieldValue(request, "serviceNeed"));
        AddNoteLine(noteLines, "Urgency", GetFieldValue(request, "urgency"));
        AddNoteLine(noteLines, "ZIP", request.QualificationResult.LeadData.ZipCode);
        AddNoteLine(noteLines, "Booking", selectedSlot?.Label);
        AddNoteLine(noteLines, "Reference", request.CorrelationId);

        return new CrmInteractionNoteRequest(
            request.TenantId,
            request.CorrelationId,
            providerContactId,
            string.Join(Environment.NewLine, noteLines));
    }

    private static CrmTagRequest CreateTagRequest(
        CrmPostBookingSyncRequest request,
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
        CrmPostBookingSyncRequest request,
        string providerContactId,
        string providerBookingId)
    {
        var leadData = request.QualificationResult.LeadData;
        var selectedSlot = request.BookingDecision.SelectedSlot;
        return new CrmBookingLinkRequest(
            request.TenantId,
            request.CorrelationId,
            providerContactId,
            providerBookingId)
        {
            VerticalId = request.VerticalId,
            BookingProvider = request.BookingProvider,
            Source = request.Source,
            CustomerName = GetFieldValue(request, "name"),
            PhoneNumber = leadData.CallerPhoneNumber,
            Email = GetFieldValue(request, "email"),
            ServiceType = request.ServiceType ?? GetFieldValue(request, "serviceNeed"),
            PropertyType = GetFieldValue(request, "propertyType"),
            ServiceAddress = GetFieldValue(request, "serviceAddress"),
            ZipCode = leadData.ZipCode,
            Urgency = GetFieldValue(request, "urgency"),
            PreferredWindow = request.PreferredWindow ?? GetFieldValue(request, "preferredTime"),
            BookingLabel = selectedSlot?.Label,
            StartsAt = selectedSlot?.StartsAt,
            EndsAt = selectedSlot?.EndsAt,
            TimeZone = request.TimeZone,
            BookingState = request.BookingDecision.State.ToString(),
            QualificationState = request.QualificationResult.State.ToString(),
            ServiceAreaState = request.QualificationResult.ServiceAreaDecision.State.ToString()
        };
    }

    private static string? GetFieldValue(CrmContactEnsureRequest request, string fieldName)
    {
        return GetFieldValue(request.QualificationResult, fieldName);
    }

    private static string? GetFieldValue(CrmPostBookingSyncRequest request, string fieldName)
    {
        return GetFieldValue(request.QualificationResult, fieldName);
    }

    private static string? GetFieldValue(QualificationResult qualificationResult, string fieldName)
    {
        return qualificationResult.LeadData.Fields.TryGetValue(fieldName, out var value)
            ? value
            : null;
    }

    private static IReadOnlyDictionary<string, string> CreateContactAttributes(CrmContactEnsureRequest request)
    {
        return request.QualificationResult.LeadData.Fields
            .Where(field =>
                ContactAttributeFields.Contains(field.Key)
                && !string.IsNullOrWhiteSpace(field.Value))
            .ToDictionary(
                field => field.Key,
                field => field.Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static CrmTimelineEventRequest CreateLeadQualifiedTimelineEvent(
        CrmContactEnsureRequest request,
        string providerContactId)
    {
        return new CrmTimelineEventRequest(
            request.TenantId,
            request.CorrelationId,
            providerContactId,
            ProviderBookingId: null,
            CrmTimelineEventTypes.LeadQualified,
            Source: "InboundVoice",
            Summary: "Lead qualified and contact ensured.",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["qualificationState"] = request.QualificationResult.State.ToString(),
                ["serviceAreaState"] = request.QualificationResult.ServiceAreaDecision.State.ToString(),
                ["serviceNeed"] = GetFieldValue(request, "serviceNeed") ?? string.Empty,
                ["urgency"] = GetFieldValue(request, "urgency") ?? string.Empty
            });
    }

    private static CrmTimelineEventRequest CreateBookingTimelineEvent(
        CrmPostBookingSyncRequest request,
        string providerContactId,
        string? providerBookingId)
    {
        return new CrmTimelineEventRequest(
            request.TenantId,
            request.CorrelationId,
            providerContactId,
            providerBookingId,
            CrmTimelineEventTypes.BookingCreated,
            request.Source,
            "Appointment booked and linked to contact.",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["bookingState"] = request.BookingDecision.State.ToString(),
                ["bookingProvider"] = request.BookingProvider ?? string.Empty,
                ["serviceType"] = request.ServiceType ?? GetFieldValue(request, "serviceNeed") ?? string.Empty,
                ["bookingLabel"] = request.BookingDecision.SelectedSlot?.Label ?? string.Empty
            });
    }

    private async Task TryAddTimelineEventAsync(
        CrmTimelineEventRequest request,
        CrmContactEnsureRequest sourceRequest,
        CancellationToken cancellationToken)
    {
        await TryAddTimelineEventAsync(
                request,
                sourceRequest.TenantId,
                sourceRequest.CorrelationId,
                request.ProviderContactId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryAddTimelineEventAsync(
        CrmTimelineEventRequest request,
        CrmPostBookingSyncRequest sourceRequest,
        CancellationToken cancellationToken)
    {
        await TryAddTimelineEventAsync(
                request,
                sourceRequest.TenantId,
                sourceRequest.CorrelationId,
                request.ProviderContactId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryAddTimelineEventAsync(
        CrmTimelineEventRequest request,
        string tenantId,
        string correlationId,
        string? providerContactId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await crmAdapter.AddTimelineEventAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                await LogAsync(
                        TelemetryEventNames.CrmTimelineEventAdded,
                        tenantId,
                        correlationId,
                        providerContactId,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await LogAsync(
                        TelemetryEventNames.CrmTimelineEventFailed,
                        tenantId,
                        correlationId,
                        providerContactId,
                        result.FailureReason ?? CrmFailureReason.AdapterFailure,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(
                    TelemetryEventNames.CrmTimelineEventFailed,
                    tenantId,
                    correlationId,
                    providerContactId,
                    CrmFailureReason.AdapterFailure,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void AddNoteLine(List<string> noteLines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            noteLines.Add($"{label}: {value.Trim()}");
        }
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new CrmOperationResult(false, CrmFailureReason.AdapterFailure);
        }
    }

    private async Task<CrmSyncResult> FailAsync(
        CrmContactEnsureRequest request,
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
        CrmContactEnsureRequest request,
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
        CrmContactEnsureRequest request,
        CrmSyncResult? result,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", request.CorrelationId)
            .Add("tenantId", request.TenantId)
            .Add("verticalId", request.VerticalId)
            .Add("qualificationState", request.QualificationResult.State.ToString())
            .AddIf(result is not null, "crmState", result?.State.ToString())
            .AddIf(result?.FailureReason is not null, "failureReason", result?.FailureReason.ToString())
            .ToDictionary();

        try
        {
            await eventLogger.LogEventAsync(eventName, properties, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // CRM telemetry is best-effort.
        }
    }

    private async Task LogAsync(
        string eventName,
        string tenantId,
        string correlationId,
        string? providerContactId,
        CrmFailureReason? failureReason,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", correlationId)
            .Add("tenantId", tenantId)
            .AddIf(!string.IsNullOrWhiteSpace(providerContactId), "providerContactId", providerContactId)
            .AddIf(failureReason is not null, "failureReason", failureReason?.ToString())
            .ToDictionary();

        try
        {
            await eventLogger.LogEventAsync(eventName, properties, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // CRM telemetry is best-effort.
        }
    }

    private async Task<CrmSyncResult> FailAsync(
        CrmPostBookingSyncRequest request,
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
        CrmPostBookingSyncRequest request,
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
        CrmPostBookingSyncRequest request,
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // CRM telemetry is best-effort.
        }
    }
}
