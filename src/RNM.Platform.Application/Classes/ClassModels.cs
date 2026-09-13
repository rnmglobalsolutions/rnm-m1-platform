using RNM.Platform.Application.Confirmations;

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
    IReadOnlyDictionary<string, string> Attributes);

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
}

public sealed record ClassRegistrationResult(
    bool Succeeded,
    ClassSessionRecord? Session,
    ClassRegistrationRecord? Registration,
    ConfirmationChannelResult? Sms,
    ConfirmationChannelResult? Email,
    ClassFailureReason? FailureReason = null,
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
    string CorrelationId);

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
    IReadOnlyCollection<int> ReminderOffsetsMinutes);

public sealed record ClassNotificationRequest(
    string TenantId,
    string CorrelationId,
    ClassSessionRecord Session,
    ClassRegistrationRecord Registration,
    ClassNotificationTemplateSet Templates,
    ClassNotificationKind Kind);

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
    ContactOptedOut = 7
}

public static class ClassRegistrationStatuses
{
    public const string Registered = "registered";
}

public static class ClassReminderStatuses
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Sent = "sent";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
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
