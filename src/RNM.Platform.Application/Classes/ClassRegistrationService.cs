using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.LeadImport;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Classes;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Classes;

public sealed class ClassRegistrationService
{
    private const int MaxAttributeValueLength = 512;
    private const int MaxAttributeCount = 25;
    private const string RetryScheduledStatus = "RetryScheduled";
    private static readonly HashSet<string> ReservedAttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        CrmContactAttributeNames.CampaignId,
        CrmContactAttributeNames.LeadSource,
        CrmContactAttributeNames.LeadStatus,
        CrmContactAttributeNames.ConsentStatus,
        CrmContactAttributeNames.ConsentCapturedAt,
        CrmContactAttributeNames.ConsentTextVersion,
        CrmContactAttributeNames.SourceFunnel,
        CrmContactAttributeNames.SourceSessionId,
        CrmContactAttributeNames.SourceRegistrationId
    };
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly IClassSessionStore classSessionStore;
    private readonly ICrmAdapter crmAdapter;
    private readonly ClassNotificationService notificationService;
    private readonly IEventLogger eventLogger;

    public ClassRegistrationService(
        ITenantConfigurationProvider tenantConfigurationProvider,
        IClassSessionStore classSessionStore,
        ICrmAdapter crmAdapter,
        ClassNotificationService notificationService,
        IEventLogger eventLogger)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.classSessionStore = classSessionStore;
        this.crmAdapter = crmAdapter;
        this.notificationService = notificationService;
        this.eventLogger = eventLogger;
    }

    public async Task<ClassSessionUpsertResult> UpsertSessionAsync(
        ClassSessionUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var validationFailure = ValidateSession(request);
        if (validationFailure is not null)
        {
            return new ClassSessionUpsertResult(false, null, ClassFailureReason.InvalidRequest, validationFailure);
        }

        var tenant = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
            .ConfigureAwait(false);

        var previous = await classSessionStore
            .GetSessionAsync(request.TenantId, request.SessionId, cancellationToken)
            .ConfigureAwait(false);

        var result = await classSessionStore
            .UpsertSessionAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (result.Succeeded)
        {
            await LogAsync(TelemetryEventNames.ClassSessionUpserted, request.TenantId, request.CorrelationId, request.SessionId, "upserted", cancellationToken)
                .ConfigureAwait(false);
            await TryAddTimelineEventAsync(
                    request.TenantId,
                    request.CorrelationId,
                    providerContactId: null,
                    ClassTimelineEventTypes.SessionUpserted,
                    "Class session upserted.",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["sessionId"] = request.SessionId,
                        ["title"] = request.Title,
                        ["campaignId"] = request.CampaignId ?? string.Empty
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (previous is not null
                && (previous.StartsAt != result.Session!.StartsAt
                    || !string.Equals(previous.Status, result.Session.Status, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    await classSessionStore
                        .CancelPendingRemindersBySessionAsync(request.TenantId, request.SessionId, request.CorrelationId, cancellationToken)
                        .ConfigureAwait(false);

                    if (string.Equals(result.Session.Status, ClassSessionStatuses.Published, StringComparison.OrdinalIgnoreCase)
                        && result.Session.StartsAt > DateTimeOffset.UtcNow)
                    {
                        var registrations = await classSessionStore
                            .GetRegistrationsBySessionAsync(request.TenantId, request.SessionId, cancellationToken)
                            .ConfigureAwait(false);
                        foreach (var registration in registrations)
                        {
                            await classSessionStore
                                .ScheduleRemindersAsync(
                                    new ClassReminderScheduleRequest(
                                        request.TenantId,
                                        request.CorrelationId,
                                        result.Session,
                                        registration,
                                        tenant.Classes?.EffectiveReminderOffsetsMinutes ?? [1440, 60])
                                    {
                                        ReplaceExisting = true
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await LogAsync(TelemetryEventNames.ClassReminderFailed, request.TenantId, request.CorrelationId, request.SessionId, "session_reschedule_failed", cancellationToken)
                        .ConfigureAwait(false);
                    return new ClassSessionUpsertResult(false, result.Session, ClassFailureReason.StorageFailure, "Session changed, but its reminders could not be rescheduled.");
                }
            }
        }

        return result;
    }

    public async Task<ClassRegistrationResult> RegisterAsync(
        ClassRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        await LogAsync(TelemetryEventNames.ClassRegistrationRequested, request.TenantId, request.CorrelationId, request.SessionId, "requested", cancellationToken)
            .ConfigureAwait(false);

        var tenant = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
            .ConfigureAwait(false);
        if (tenant.Classes is null)
        {
            return await FailedAsync(request, ClassFailureReason.MissingClassConfiguration, "Class automation is not configured for this tenant.", cancellationToken).ConfigureAwait(false);
        }

        var normalized = NormalizeRegistration(request);
        if (normalized.FailureReason is not null)
        {
            return await FailedAsync(request, normalized.FailureReason.Value, normalized.Message, cancellationToken).ConfigureAwait(false);
        }

        var session = await classSessionStore
            .GetSessionAsync(request.TenantId, request.SessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return await FailedAsync(request, ClassFailureReason.MissingSession, "Class session was not found.", cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(session.Status, ClassSessionStatuses.Published, StringComparison.OrdinalIgnoreCase))
        {
            return await FailedAsync(request, ClassFailureReason.SessionNotOpen, "Class session is not open for registration.", cancellationToken).ConfigureAwait(false);
        }

        if (session.StartsAt <= DateTimeOffset.UtcNow)
        {
            return await FailedAsync(request, ClassFailureReason.SessionAlreadyStarted, "Class session has already started.", cancellationToken).ConfigureAwait(false);
        }

        var lookup = await crmAdapter
            .FindContactByPhoneOrEmailAsync(
                new CrmContactLookupRequest(
                    request.TenantId,
                    request.CorrelationId,
                    normalized.PhoneNumber,
                    normalized.Email),
                cancellationToken)
            .ConfigureAwait(false);
        var finalConsent = ResolveConsentStatus(lookup.Contact?.ConsentStatus, request.MarketingConsentGranted);
        var registrationIdentity = normalized.PhoneNumber ?? normalized.Email ?? normalized.Name;
        var registrationId = CreateRegistrationId(request.TenantId, request.SessionId, registrationIdentity);

        ClassRegistrationRecord? existingRegistration = null;
        if (!string.IsNullOrWhiteSpace(lookup.ProviderContactId))
        {
            existingRegistration = await classSessionStore
                .FindRegistrationAsync(request.TenantId, session.SessionId, lookup.ProviderContactId, cancellationToken)
                .ConfigureAwait(false);
        }

        existingRegistration ??= await classSessionStore
            .GetRegistrationAsync(request.TenantId, registrationId, cancellationToken)
            .ConfigureAwait(false);
        if (existingRegistration is not null)
        {
            existingRegistration = await ApplyExplicitConsentToExistingRegistrationAsync(
                    request,
                    tenant,
                    session,
                    normalized,
                    lookup,
                    existingRegistration,
                    cancellationToken)
                .ConfigureAwait(false);
            var resumedNotification = await ResumeIncompleteRegistrationAsync(
                    request,
                    tenant,
                    session,
                    existingRegistration,
                    cancellationToken)
                .ConfigureAwait(false);
            await LogAsync(TelemetryEventNames.ClassRegistrationCompleted, request.TenantId, request.CorrelationId, request.SessionId, "duplicate", cancellationToken)
                .ConfigureAwait(false);
            return new ClassRegistrationResult(true, session, existingRegistration, resumedNotification.Sms, resumedNotification.Email)
            {
                Duplicate = true
            };
        }

        var providerContactId = lookup.ProviderContactId;
        ClassRegistrationReservationResult reservation;
        try
        {
            reservation = await classSessionStore
                .TryReserveRegistrationAsync(
                    request.TenantId,
                    session.SessionId,
                    registrationId,
                    request.CorrelationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailedAsync(request, ClassFailureReason.StorageFailure, "Class registration reservation failed.", cancellationToken).ConfigureAwait(false);
        }

        if (!reservation.Succeeded)
        {
            var reason = reservation.CapacityReached ? ClassFailureReason.CapacityReached : ClassFailureReason.StorageFailure;
            return await FailedAsync(request, reason, reservation.Message, cancellationToken).ConfigureAwait(false);
        }

        var attributes = CreateContactAttributes(request, session, registrationId, finalConsent);
        var upsert = await crmAdapter
            .UpsertContactAsync(
                new CrmContactUpsertRequest(
                    request.TenantId,
                    tenant.VerticalId.Value,
                    request.CorrelationId,
                    providerContactId,
                    normalized.PhoneNumber,
                    normalized.Email,
                    normalized.Name,
                    ZipCode: null,
                    attributes)
                {
                    LeadStatus = CrmOutboundLeadStatuses.Qualified,
                    LastInteractionAt = DateTimeOffset.UtcNow,
                    AllowOptOutReversal = false
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!upsert.Succeeded || string.IsNullOrWhiteSpace(upsert.ProviderContactId))
        {
            await TryReleaseReservationAsync(request, registrationId, "crm_write_failed", cancellationToken).ConfigureAwait(false);
            return await FailedAsync(request, ClassFailureReason.CrmWriteFailed, "CRM contact upsert failed.", cancellationToken).ConfigureAwait(false);
        }

        await RecordConsentTimelineAsync(request, upsert.ProviderContactId, finalConsent, lookup.Contact?.ConsentStatus, cancellationToken)
            .ConfigureAwait(false);

        var registration = new ClassRegistrationRecord(
                request.TenantId,
                registrationId,
                session.SessionId,
                upsert.ProviderContactId,
                normalized.Name,
                normalized.PhoneNumber,
                normalized.Email,
                request.Source,
                request.CampaignId ?? session.CampaignId,
                finalConsent,
                DateTimeOffset.UtcNow,
                attributes)
            with
            {
                CustomerName = normalized.Name,
                CustomerPhoneNumber = normalized.PhoneNumber,
                CustomerEmail = normalized.Email,
                Source = request.Source,
                CampaignId = request.CampaignId ?? session.CampaignId,
                ConsentStatus = finalConsent,
                Attributes = attributes
            };

        try
        {
            registration = await classSessionStore.UpsertRegistrationAsync(registration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await TryReleaseReservationAsync(request, registrationId, "registration_write_failed", cancellationToken).ConfigureAwait(false);
            return await FailedAsync(request, ClassFailureReason.StorageFailure, "Class registration write failed.", cancellationToken).ConfigureAwait(false);
        }

        await TryAddTimelineEventAsync(
                request.TenantId,
                request.CorrelationId,
                upsert.ProviderContactId,
                ClassTimelineEventTypes.RegistrationCreated,
                "Class registration created.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sessionId"] = session.SessionId,
                    ["registrationId"] = registration.RegistrationId,
                    ["campaignId"] = registration.CampaignId ?? string.Empty,
                    ["source"] = request.Source,
                    ["consentStatus"] = finalConsent
                },
                cancellationToken)
            .ConfigureAwait(false);

        var templates = ToTemplateSet(tenant.Classes.RegistrationTemplates);
        var notification = await notificationService
            .SendAsync(
                new ClassNotificationRequest(
                    request.TenantId,
                    request.CorrelationId,
                    session,
                    registration,
                    templates,
                    ClassNotificationKind.RegistrationConfirmation),
                tenant,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await classSessionStore.UpdateRegistrationNotificationStatusAsync(
                    new ClassNotificationStatusUpdate(
                        request.TenantId,
                        registration.RegistrationId,
                        request.CorrelationId,
                        GetPersistedNotificationStatus(notification.Sms),
                        GetPersistedNotificationStatus(notification.Email)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.ClassNotificationFailed, request.TenantId, request.CorrelationId, request.SessionId, "registration_status_update_failed", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await classSessionStore.ScheduleRemindersAsync(
                    new ClassReminderScheduleRequest(
                        request.TenantId,
                        request.CorrelationId,
                        session,
                        registration,
                        tenant.Classes.EffectiveReminderOffsetsMinutes),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.ClassReminderFailed, request.TenantId, request.CorrelationId, request.SessionId, "schedule_failed", cancellationToken)
                .ConfigureAwait(false);
        }

        await LogAsync(TelemetryEventNames.ClassRegistrationCompleted, request.TenantId, request.CorrelationId, request.SessionId, "completed", cancellationToken)
            .ConfigureAwait(false);
        return new ClassRegistrationResult(true, session, registration, notification.Sms, notification.Email);
    }

    private async Task<(ConfirmationChannelResult? Sms, ConfirmationChannelResult? Email)> ResumeIncompleteRegistrationAsync(
        ClassRegistrationRequest request,
        TenantConfiguration tenant,
        ClassSessionRecord session,
        ClassRegistrationRecord registration,
        CancellationToken cancellationToken)
    {
        var smsProcessed = IsNotificationFinal(registration.ConfirmationSmsStatus);
        var emailProcessed = IsNotificationFinal(registration.ConfirmationEmailStatus);
        (ConfirmationChannelResult? Sms, ConfirmationChannelResult? Email) notification = (null, null);

        if (!smsProcessed || !emailProcessed)
        {
            notification = await notificationService
                .SendAsync(
                    new ClassNotificationRequest(
                        request.TenantId,
                        request.CorrelationId,
                        session,
                        registration,
                        ToTemplateSet(tenant.Classes!.RegistrationTemplates),
                        ClassNotificationKind.RegistrationConfirmation)
                    {
                        SmsSuppressionReason = smsProcessed ? ConfirmationFailureReason.AlreadyProcessed : null,
                        EmailSuppressionReason = emailProcessed ? ConfirmationFailureReason.AlreadyProcessed : null
                    },
                    tenant,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await classSessionStore.UpdateRegistrationNotificationStatusAsync(
                        new ClassNotificationStatusUpdate(
                            request.TenantId,
                            registration.RegistrationId,
                            request.CorrelationId,
                            smsProcessed ? null : GetPersistedNotificationStatus(notification.Sms),
                            emailProcessed ? null : GetPersistedNotificationStatus(notification.Email)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await LogAsync(TelemetryEventNames.ClassNotificationFailed, request.TenantId, request.CorrelationId, request.SessionId, "duplicate_status_update_failed", cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        try
        {
            await classSessionStore.ScheduleRemindersAsync(
                    new ClassReminderScheduleRequest(
                        request.TenantId,
                        request.CorrelationId,
                        session,
                        registration,
                        tenant.Classes!.EffectiveReminderOffsetsMinutes),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.ClassReminderFailed, request.TenantId, request.CorrelationId, request.SessionId, "duplicate_schedule_failed", cancellationToken)
                .ConfigureAwait(false);
        }

        return notification;
    }

    private async Task<ClassRegistrationRecord> ApplyExplicitConsentToExistingRegistrationAsync(
        ClassRegistrationRequest request,
        TenantConfiguration tenant,
        ClassSessionRecord session,
        NormalizedRegistration normalized,
        CrmContactLookupResult lookup,
        ClassRegistrationRecord registration,
        CancellationToken cancellationToken)
    {
        if (!request.MarketingConsentGranted)
        {
            return registration;
        }

        var previousConsent = lookup.Contact?.ConsentStatus ?? registration.ConsentStatus;
        if (string.Equals(previousConsent, CrmConsentStatuses.OptIn, StringComparison.OrdinalIgnoreCase))
        {
            return registration;
        }

        var finalConsent = ResolveConsentStatus(previousConsent, granted: true);
        if (string.Equals(finalConsent, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase))
        {
            await RecordConsentTimelineAsync(
                    request,
                    registration.ProviderContactId,
                    finalConsent,
                    previousConsent,
                    cancellationToken)
                .ConfigureAwait(false);
            return registration;
        }

        var attributes = new Dictionary<string, string>(registration.Attributes, StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in CreateContactAttributes(request, session, registration.RegistrationId, finalConsent))
        {
            attributes[attribute.Key] = attribute.Value;
        }

        var upsert = await crmAdapter
            .UpsertContactAsync(
                new CrmContactUpsertRequest(
                    request.TenantId,
                    tenant.VerticalId.Value,
                    request.CorrelationId,
                    registration.ProviderContactId,
                    normalized.PhoneNumber ?? registration.CustomerPhoneNumber,
                    normalized.Email ?? registration.CustomerEmail,
                    normalized.Name,
                    ZipCode: null,
                    attributes)
                {
                    LeadStatus = CrmOutboundLeadStatuses.Qualified,
                    LastInteractionAt = DateTimeOffset.UtcNow,
                    AllowOptOutReversal = false
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!upsert.Succeeded)
        {
            await LogAsync(
                    TelemetryEventNames.ClassRegistrationFailed,
                    request.TenantId,
                    request.CorrelationId,
                    request.SessionId,
                    "duplicate_consent_upsert_failed",
                    cancellationToken)
                .ConfigureAwait(false);
            return registration;
        }

        await RecordConsentTimelineAsync(
                request,
                registration.ProviderContactId,
                finalConsent,
                previousConsent,
                cancellationToken)
            .ConfigureAwait(false);

        var updated = registration with
        {
            ConsentStatus = finalConsent,
            Attributes = attributes,
            ConfirmationSmsStatus = null
        };

        try
        {
            return await classSessionStore.UpsertRegistrationAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(
                    TelemetryEventNames.ClassRegistrationFailed,
                    request.TenantId,
                    request.CorrelationId,
                    request.SessionId,
                    "duplicate_consent_registration_update_failed",
                    cancellationToken)
                .ConfigureAwait(false);
            return registration;
        }
    }

    private static bool IsNotificationFinal(string? status) =>
        string.Equals(status, ConfirmationChannelStatus.Sent.ToString(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, ConfirmationChannelStatus.Skipped.ToString(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, RetryScheduledStatus, StringComparison.OrdinalIgnoreCase);

    private static string? GetPersistedNotificationStatus(ConfirmationChannelResult? result)
    {
        if (result is null)
        {
            return null;
        }

        return result.Status is ConfirmationChannelStatus.Failed && result.RetryScheduled
            ? RetryScheduledStatus
            : result.Status.ToString();
    }

    private static ClassNotificationTemplateSet ToTemplateSet(ClassNotificationTemplateConfiguration? configuration) =>
        new(
            configuration?.SmsBodyTemplate,
            configuration?.EmailSubjectTemplate,
            configuration?.EmailBodyTemplate);

    private async Task RecordConsentTimelineAsync(
        ClassRegistrationRequest request,
        string providerContactId,
        string finalConsent,
        string? previousConsent,
        CancellationToken cancellationToken)
    {
        var eventType = (request.MarketingConsentGranted, finalConsent) switch
        {
            (true, CrmConsentStatuses.OptIn) => CrmTimelineEventTypes.MarketingConsentWebRegistrationGranted,
            (true, CrmConsentStatuses.OptedOut) => CrmTimelineEventTypes.MarketingConsentWebRegistrationBlockedOptedOut,
            _ => CrmTimelineEventTypes.MarketingConsentWebRegistrationDeclined
        };

        await TryAddTimelineEventAsync(
                request.TenantId,
                request.CorrelationId,
                providerContactId,
                eventType,
                "Class registration consent captured.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sessionId"] = request.SessionId,
                    ["source"] = request.Source,
                    ["consentStatus"] = finalConsent,
                    ["previousConsentStatus"] = previousConsent ?? string.Empty,
                    ["explicitConsent"] = request.MarketingConsentGranted.ToString(),
                    ["consentCapturedAt"] = request.ConsentCapturedAt?.ToUniversalTime().ToString("O") ?? string.Empty,
                    ["consentTextVersion"] = request.ConsentTextVersion ?? string.Empty
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryAddTimelineEventAsync(
        string tenantId,
        string correlationId,
        string? providerContactId,
        string eventType,
        string summary,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await crmAdapter
                .AddTimelineEventAsync(
                    new CrmTimelineEventRequest(
                        tenantId,
                        correlationId,
                        providerContactId,
                        ProviderBookingId: null,
                        eventType,
                        "ClassRegistration",
                        summary,
                        metadata),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                await LogAsync(TelemetryEventNames.CrmTimelineEventFailed, tenantId, correlationId, metadata.GetValueOrDefault("sessionId"), eventType, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.CrmTimelineEventFailed, tenantId, correlationId, metadata.GetValueOrDefault("sessionId"), eventType, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task LogAsync(
        string eventName,
        string tenantId,
        string correlationId,
        string? sessionId,
        string outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger
                .LogEventAsync(
                    eventName,
                    new SafeTelemetryProperties()
                        .Add("tenantId", tenantId)
                        .Add("correlationId", correlationId)
                        .Add("sessionId", sessionId)
                        .Add("outcome", outcome)
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Class telemetry is best-effort.
        }
    }

    private async Task<ClassRegistrationResult> FailedAsync(
        ClassRegistrationRequest request,
        ClassFailureReason reason,
        string? message,
        CancellationToken cancellationToken)
    {
        await LogAsync(
                TelemetryEventNames.ClassRegistrationFailed,
                request.TenantId,
                request.CorrelationId,
                request.SessionId,
                reason.ToString(),
                cancellationToken)
            .ConfigureAwait(false);
        return new ClassRegistrationResult(false, null, null, null, null, reason, message);
    }

    private async Task TryReleaseReservationAsync(
        ClassRegistrationRequest request,
        string registrationId,
        string outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await classSessionStore
                .ReleaseRegistrationReservationAsync(
                    request.TenantId,
                    request.SessionId,
                    registrationId,
                    request.CorrelationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(
                    TelemetryEventNames.ClassRegistrationFailed,
                    request.TenantId,
                    request.CorrelationId,
                    request.SessionId,
                    $"{outcome}_reservation_release_failed",
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static NormalizedRegistration NormalizeRegistration(ClassRegistrationRequest request)
    {
        var name = NormalizeOptional(request.CustomerName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return NormalizedRegistration.Failed(ClassFailureReason.InvalidRequest, "Customer name is required.");
        }

        string? normalizedPhone = null;
        if (!string.IsNullOrWhiteSpace(request.CustomerPhoneNumber))
        {
            if (!LeadPhoneNormalizer.TryNormalizeToE164(request.CustomerPhoneNumber, out var phone))
            {
                return NormalizedRegistration.Failed(ClassFailureReason.InvalidRequest, "Customer phone number is invalid.");
            }

            normalizedPhone = phone;
        }

        var email = NormalizeOptional(request.CustomerEmail);
        if (!string.IsNullOrWhiteSpace(email) && !IsValidEmail(email))
        {
            return NormalizedRegistration.Failed(ClassFailureReason.InvalidRequest, "Customer email is invalid.");
        }

        if (string.IsNullOrWhiteSpace(normalizedPhone) && string.IsNullOrWhiteSpace(email))
        {
            return NormalizedRegistration.Failed(ClassFailureReason.MissingContactIdentifier, "Phone number or email is required.");
        }

        if (name.Length > 200
            || email?.Length > 320
            || request.Source.Length > 100
            || request.CampaignId?.Length > 128)
        {
            return NormalizedRegistration.Failed(ClassFailureReason.InvalidRequest, "One or more fields exceed the allowed length.");
        }

        if (request.MarketingConsentGranted
            && (request.ConsentCapturedAt is null || string.IsNullOrWhiteSpace(request.ConsentTextVersion)))
        {
            return NormalizedRegistration.Failed(ClassFailureReason.InvalidRequest, "Explicit marketing consent requires capturedAt and textVersion evidence.");
        }

        if (request.ConsentCapturedAt > DateTimeOffset.UtcNow.AddMinutes(5)
            || request.ConsentTextVersion?.Length > 128
            || request.Attributes.Count > MaxAttributeCount
            || request.Attributes.Any(item =>
                string.IsNullOrWhiteSpace(item.Key)
                || item.Value is null
                || item.Key.Length > 64
                || item.Value.Length > MaxAttributeValueLength))
        {
            return NormalizedRegistration.Failed(ClassFailureReason.InvalidRequest, "Consent evidence or attributes are invalid.");
        }

        return new NormalizedRegistration(name, normalizedPhone, email, null, null);
    }

    private static string? ValidateSession(ClassSessionUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId)
            || string.IsNullOrWhiteSpace(request.SessionId)
            || string.IsNullOrWhiteSpace(request.Title)
            || string.IsNullOrWhiteSpace(request.TimeZone)
            || string.IsNullOrWhiteSpace(request.ZoomUrl))
        {
            return "tenantId, sessionId, title, timeZone, and zoomUrl are required.";
        }

        if (!ClassSessionStatuses.IsSupported(request.Status))
        {
            return "status must be draft, published, closed, or cancelled.";
        }

        if (request.TenantId.Length > 128
            || request.SessionId.Length > 128
            || request.SessionId.IndexOfAny(['/', '\\', '#', '?']) >= 0
            || request.Title.Length > 200
            || request.TimeZone.Length > 100
            || request.ZoomUrl.Length > 2048
            || request.CampaignId?.Length > 128
            || request.Attributes.Count > MaxAttributeCount
            || request.Attributes.Any(item =>
                string.IsNullOrWhiteSpace(item.Key)
                || item.Value is null
                || item.Key.Length > 64
                || item.Value.Length > MaxAttributeValueLength))
        {
            return "One or more class session fields exceed the allowed format or length.";
        }

        if (string.Equals(request.Status, ClassSessionStatuses.Published, StringComparison.OrdinalIgnoreCase)
            && request.StartsAt <= DateTimeOffset.UtcNow)
        {
            return "published class sessions must start in the future.";
        }

        if (!Uri.TryCreate(request.ZoomUrl, UriKind.Absolute, out var zoomUri)
            || !string.Equals(zoomUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "zoomUrl must be an absolute HTTPS URL.";
        }

        if (request.EndsAt.HasValue && request.EndsAt <= request.StartsAt)
        {
            return "endsAt must be after startsAt.";
        }

        if (request.Capacity is <= 0)
        {
            return "capacity must be greater than zero.";
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return "timeZone is invalid.";
        }
        catch (InvalidTimeZoneException)
        {
            return "timeZone is invalid.";
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> CreateContactAttributes(
        ClassRegistrationRequest request,
        ClassSessionRecord session,
        string registrationId,
        string consentStatus)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CrmContactAttributeNames.CampaignId] = request.CampaignId ?? session.CampaignId ?? string.Empty,
            [CrmContactAttributeNames.LeadSource] = request.Source,
            [CrmContactAttributeNames.LeadStatus] = CrmOutboundLeadStatuses.Qualified,
            [CrmContactAttributeNames.ConsentStatus] = consentStatus,
            [CrmContactAttributeNames.SourceFunnel] = "class_registration",
            [CrmContactAttributeNames.SourceSessionId] = session.SessionId,
            [CrmContactAttributeNames.SourceRegistrationId] = registrationId
        };
        if (request.ConsentCapturedAt.HasValue)
        {
            attributes[CrmContactAttributeNames.ConsentCapturedAt] = request.ConsentCapturedAt.Value.ToUniversalTime().ToString("O");
        }

        if (!string.IsNullOrWhiteSpace(request.ConsentTextVersion))
        {
            attributes[CrmContactAttributeNames.ConsentTextVersion] = request.ConsentTextVersion.Trim();
        }

        foreach (var item in request.Attributes)
        {
            if (!string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
            {
                var sanitizedName = SanitizeAttributeName(item.Key);
                if (!ReservedAttributeNames.Contains(sanitizedName))
                {
                    attributes[sanitizedName] = Truncate(item.Value);
                }
            }
        }

        return attributes;
    }

    private static string ResolveConsentStatus(string? existingConsent, bool granted)
    {
        if (string.Equals(existingConsent, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase))
        {
            return CrmConsentStatuses.OptedOut;
        }

        if (granted || string.Equals(existingConsent, CrmConsentStatuses.OptIn, StringComparison.OrdinalIgnoreCase))
        {
            return CrmConsentStatuses.OptIn;
        }

        return CrmConsentStatuses.Unknown;
    }

    private static string CreateRegistrationId(string tenantId, string sessionId, string providerContactId) =>
        $"reg_{CreateStableId(tenantId, sessionId, providerContactId)}";

    private static string CreateStableId(params string?[] values)
    {
        var input = string.Join("|", values.Select(value => value?.Trim().ToLowerInvariant() ?? string.Empty));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static string SanitizeAttributeName(string value)
    {
        var characters = value
            .Where(char.IsLetterOrDigit)
            .Take(48)
            .ToArray();
        return characters.Length == 0 ? "attribute" : new string(characters);
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxAttributeValueLength ? trimmed : trimmed[..MaxAttributeValueLength];
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsValidEmail(string value)
    {
        try
        {
            _ = new MailAddress(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record NormalizedRegistration(
        string Name,
        string? PhoneNumber,
        string? Email,
        ClassFailureReason? FailureReason,
        string? Message)
    {
        public static NormalizedRegistration Failed(ClassFailureReason reason, string message) =>
            new(string.Empty, null, null, reason, message);
    }
}
