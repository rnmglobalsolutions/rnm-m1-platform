using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Classes;

public sealed record ClassSessionUpsertRequest(
    string TenantId,
    string CorrelationId,
    string SessionId,
    string Title,
    DateTimeOffset StartsAt,
    string TimeZone,
    string ZoomUrl)
{
    public string Status { get; init; } = ClassSessionStatuses.Published;

    public DateTimeOffset? EndsAt { get; init; }

    public int? Capacity { get; init; }

    public string? CampaignId { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ClassSessionRecord(
    string TenantId,
    string SessionId,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    string TimeZone,
    string ZoomUrl,
    int? Capacity,
    string? CampaignId,
    IReadOnlyDictionary<string, string> Attributes)
{
    public string Status { get; init; } = ClassSessionStatuses.Published;
}

public sealed record ClassSessionUpsertResult(
    bool Succeeded,
    ClassSessionRecord? Session,
    ClassFailureReason? FailureReason = null,
    string? Message = null);

public sealed record ClassRegistrationRequest(
    string TenantId,
    string CorrelationId,
    string SessionId,
    string CustomerName,
    string? CustomerPhoneNumber,
    string? CustomerEmail)
{
    public string Source { get; init; } = "WebRegistration";

    public string? CampaignId { get; init; }

    public bool MarketingConsentGranted { get; init; }

    public DateTimeOffset? ConsentCapturedAt { get; init; }

    public string? ConsentTextVersion { get; init; }

    public ChannelConsentCapture? SmsConsent { get; init; }

    public ChannelConsentCapture? EmailConsent { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ClassRegistrationRecord(
    string TenantId,
    string RegistrationId,
    string SessionId,
    string ProviderContactId,
    string CustomerName,
    string? CustomerPhoneNumber,
    string? CustomerEmail,
    string Source,
    string? CampaignId,
    string ConsentStatus,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string> Attributes)
{
    public string? ConfirmationSmsStatus { get; init; }

    public string? ConfirmationEmailStatus { get; init; }

    public string SmsConsentStatus => ChannelConsent.ResolveStatus(Attributes, ConsentStatus, ConsentChannel.Sms);

    public string EmailConsentStatus => ChannelConsent.ResolveStatus(Attributes, ConsentStatus, ConsentChannel.Email);
}

public sealed record ClassRegistrationResult(
    bool Succeeded,
    ClassSessionRecord? Session,
    ClassRegistrationRecord? Registration,
    ConfirmationChannelResult? Sms,
    ConfirmationChannelResult? Email,
    ClassFailureReason? FailureReason = null,
    string? Message = null)
{
    public bool Duplicate { get; init; }
}

public sealed record ClassRegistrationReservationResult(
    bool Succeeded,
    bool AlreadyReserved = false,
    bool CapacityReached = false,
    string? Message = null);

public sealed record ClassReminderRecord(
    string TenantId,
    string RowKey,
    string RegistrationId,
    string SessionId,
    string ProviderContactId,
    string ReminderKind,
    DateTimeOffset DueAt,
    string Status,
    string CorrelationId)
{
    public DateTimeOffset? ClaimedAt { get; init; }

    public string? SkipReason { get; init; }

    public string TargetType { get; init; } = ReminderTargetTypes.ClassSession;

    public string TargetId { get; init; } = string.Empty;

    public string? CustomerName { get; init; }

    public string? CustomerPhoneNumber { get; init; }

    public string? CustomerEmail { get; init; }

    public string? BookingLabel { get; init; }

    public DateTimeOffset? StartsAt { get; init; }

    public DateTimeOffset? EndsAt { get; init; }

    public string? TimeZone { get; init; }

    public string? OnlineMeetingUrl { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string EffectiveTargetType =>
        string.IsNullOrWhiteSpace(TargetType) ? ReminderTargetTypes.ClassSession : TargetType;

    public string EffectiveTargetId =>
        string.IsNullOrWhiteSpace(TargetId) ? SessionId : TargetId;
}

public sealed record AppointmentReminderScheduleRequest(
    string TenantId,
    string CorrelationId,
    string ProviderContactId,
    string ProviderBookingId,
    string? CustomerName,
    string? CustomerPhoneNumber,
    string? CustomerEmail,
    string? BookingLabel,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    string TimeZone,
    string? OnlineMeetingUrl,
    IReadOnlyCollection<int> ReminderOffsetsMinutes,
    IReadOnlyDictionary<string, string> Attributes);

public sealed record ClassReminderRunRequest(
    string TenantId,
    string CorrelationId,
    DateTimeOffset DueAt)
{
    public int MaxItems { get; init; } = 25;
}

public sealed record ClassReminderRunResult(
    string TenantId,
    string CorrelationId,
    int Scanned,
    int Sent,
    int Skipped,
    int Failed);

public sealed record ClassReportRequest(
    string TenantId,
    string CorrelationId,
    string SessionId);

public sealed record ClassReportResult(
    string TenantId,
    string CorrelationId,
    string SessionId,
    int Registrations,
    int ConfirmedEmailsSent,
    int ConfirmedSmsSent,
    int RemindersSent,
    int OptedOutRegistrations,
    string Summary);

public sealed record ClassNotificationStatusUpdate(
    string TenantId,
    string RegistrationId,
    string CorrelationId,
    string? SmsStatus,
    string? EmailStatus);

public sealed record ClassReminderScheduleRequest(
    string TenantId,
    string CorrelationId,
    ClassSessionRecord Session,
    ClassRegistrationRecord Registration,
    IReadOnlyCollection<int> ReminderOffsetsMinutes)
{
    public bool ReplaceExisting { get; init; }
}

public sealed record ClassNotificationRequest(
    string TenantId,
    string CorrelationId,
    ClassSessionRecord Session,
    ClassRegistrationRecord Registration,
    ClassNotificationTemplateSet Templates,
    ClassNotificationKind Kind)
{
    public ConfirmationFailureReason? SmsSuppressionReason { get; init; }

    public ConfirmationFailureReason? EmailSuppressionReason { get; init; }
}

public sealed record AppointmentReminderNotificationRequest(
    string TenantId,
    string CorrelationId,
    string ProviderContactId,
    string ProviderBookingId,
    string BusinessName,
    string? CustomerName,
    string? CustomerPhoneNumber,
    string? CustomerEmail,
    string? ServiceType,
    string? PropertyType,
    string? ServiceAddress,
    string? ZipCode,
    string? Urgency,
    string? BookingLabel,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    string TimeZone,
    string? OnlineMeetingUrl,
    ConfirmationTemplateConfiguration? Templates,
    string ConsentStatus,
    IReadOnlyDictionary<string, string> Attributes)
{
    public ConfirmationFailureReason? SmsSuppressionReason { get; init; }

    public string SmsConsentStatus => ChannelConsent.ResolveStatus(Attributes, ConsentStatus, ConsentChannel.Sms);

    public string EmailConsentStatus => ChannelConsent.ResolveStatus(Attributes, ConsentStatus, ConsentChannel.Email);
}

public sealed record ClassNotificationTemplateSet(
    string? SmsBodyTemplate,
    string? EmailSubjectTemplate,
    string? EmailBodyTemplate);

public enum ClassNotificationKind
{
    RegistrationConfirmation = 0,
    Reminder = 1
}

public enum ClassFailureReason
{
    MissingClassConfiguration = 0,
    MissingSession = 1,
    InvalidRequest = 2,
    MissingContactIdentifier = 3,
    CrmWriteFailed = 4,
    StorageFailure = 5,
    CapacityReached = 6,
    ContactOptedOut = 7,
    SessionNotOpen = 8,
    SessionAlreadyStarted = 9
}

public static class ClassRegistrationStatuses
{
    public const string Registered = "registered";
}

public static class ClassSessionStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Closed = "closed";
    public const string Cancelled = "cancelled";

    public static bool IsSupported(string? value) =>
        string.Equals(value, Draft, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Published, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Closed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Cancelled, StringComparison.OrdinalIgnoreCase);
}

public static class ClassReminderStatuses
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Sent = "sent";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

public static class ReminderTargetTypes
{
    public const string ClassSession = "class_session";
    public const string Appointment = "appointment";
}

public static class ClassTimelineEventTypes
{
    public const string SessionUpserted = "class.session_upserted";
    public const string RegistrationCreated = "class.registration_created";
    public const string RegistrationConfirmationSent = "class.registration_confirmation.sent";
    public const string ReminderScheduled = "class.reminder.scheduled";
    public const string ReminderSent = "class.reminder.sent";
    public const string ReminderSkipped = "class.reminder.skipped";
    public const string ReminderFailed = "class.reminder.failed";
}
