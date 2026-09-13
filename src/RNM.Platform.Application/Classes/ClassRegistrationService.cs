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

        await tenantConfigurationProvider
            .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
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
            return Failed(request, ClassFailureReason.MissingClassConfiguration, "Class automation is not configured for this tenant.");
        }

        var normalized = NormalizeRegistration(request);
        if (normalized.FailureReason is not null)
        {
            return Failed(request, normalized.FailureReason.Value, normalized.Message);
        }

        var session = await classSessionStore
            .GetSessionAsync(request.TenantId, request.SessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return Failed(request, ClassFailureReason.MissingSession, "Class session was not found.");
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

        ClassRegistrationRecord? existingRegistration = null;
        if (!string.IsNullOrWhiteSpace(lookup.ProviderContactId))
        {
            existingRegistration = await classSessionStore
                .FindRegistrationAsync(request.TenantId, session.SessionId, lookup.ProviderContactId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (session.Capacity.HasValue && existingRegistration is null)
        {
            var registrations = await classSessionStore
                .GetRegistrationsBySessionAsync(request.TenantId, session.SessionId, cancellationToken)
                .ConfigureAwait(false);
            if (registrations.Count >= session.Capacity.Value)
            {
                return Failed(request, ClassFailureReason.CapacityReached, "Class session capacity has been reached.");
            }
        }

        var providerContactId = lookup.ProviderContactId;
        var provisionalContactId = providerContactId ?? CreateStableId(request.TenantId, normalized.PhoneNumber ?? normalized.Email ?? normalized.Name);
        var registrationId = CreateRegistrationId(request.TenantId, request.SessionId, provisionalContactId);
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
            return Failed(request, ClassFailureReason.CrmWriteFailed, "CRM contact upsert failed.");
        }

        await RecordConsentTimelineAsync(request, upsert.ProviderContactId, finalConsent, lookup.Contact?.ConsentStatus, cancellationToken)
            .ConfigureAwait(false);

        var registration = (existingRegistration ?? new ClassRegistrationRecord(
                request.TenantId,
                CreateRegistrationId(request.TenantId, request.SessionId, upsert.ProviderContactId),
                session.SessionId,
                upsert.ProviderContactId,
                normalized.Name,
                normalized.PhoneNumber,
                normalized.Email,
                request.Source,
                request.CampaignId ?? session.CampaignId,
                finalConsent,
                DateTimeOffset.UtcNow,
                attributes))
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
            return Failed(request, ClassFailureReason.StorageFailure, "Class registration write failed.");
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
                        notification.Sms?.Status.ToString(),
                        notification.Email?.Status.ToString()),
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
        var eventType = finalConsent switch
        {
            CrmConsentStatuses.OptIn => CrmTimelineEventTypes.MarketingConsentWebRegistrationGranted,
            CrmConsentStatuses.OptedOut when request.MarketingConsentGranted => CrmTimelineEventTypes.MarketingConsentWebRegistrationBlockedOptedOut,
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
                    ["explicitConsent"] = request.MarketingConsentGranted.ToString()
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

    private static ClassRegistrationResult Failed(
        ClassRegistrationRequest request,
        ClassFailureReason reason,
        string? message) =>
        new(false, null, null, null, null, reason, message);

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

        if (!Uri.TryCreate(request.ZoomUrl, UriKind.Absolute, out _))
        {
            return "zoomUrl must be an absolute URL.";
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
            [CrmContactAttributeNames.SourceFunnel] = "masterclass",
            [CrmContactAttributeNames.SourceSessionId] = session.SessionId,
            [CrmContactAttributeNames.SourceRegistrationId] = registrationId
        };

        foreach (var item in request.Attributes)
        {
            if (!string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
            {
                attributes[SanitizeAttributeName(item.Key)] = Truncate(item.Value);
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

        return granted ? CrmConsentStatuses.OptIn : CrmConsentStatuses.Unknown;
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
