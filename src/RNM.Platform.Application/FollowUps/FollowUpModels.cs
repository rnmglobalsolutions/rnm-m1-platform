using RNM.Platform.Application.Confirmations;

namespace RNM.Platform.Application.FollowUps;

public sealed record FollowUpScheduleRequest(
    string TenantId,
    string CorrelationId,
    string ProviderContactId,
    string TriggerEventType,
    string Reason)
{
    public string? CustomerName { get; init; }

    public string? CustomerPhoneNumber { get; init; }

    public string? CustomerEmail { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset TriggeredAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record FollowUpDueRecord(
    string TenantId,
    string RowKey,
    string ProviderContactId,
    string SequenceId,
    int StepIndex,
    string TriggerEventType,
    string Channel,
    DateTimeOffset DueAt,
    string Status,
    string CorrelationId)
{
    public string? CustomerName { get; init; }

    public string? CustomerPhoneNumber { get; init; }

    public string? CustomerEmail { get; init; }

    public string? Reason { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record FollowUpRunRequest(
    string TenantId,
    string CorrelationId,
    DateTimeOffset DueAt)
{
    public int MaxItems { get; init; } = 25;
}

public sealed record FollowUpRunResult(
    string TenantId,
    string CorrelationId,
    int DueCount,
    int Sent,
    int Skipped,
    int Failed);

public sealed record FollowUpDispatchResult(
    ConfirmationChannel Channel,
    ConfirmationChannelStatus Status,
    ConfirmationFailureReason? FailureReason = null,
    string? ProviderMessageId = null);

public static class FollowUpTriggers
{
    public const string LeadFollowUpRequired = "followup.required";
}

public static class FollowUpChannels
{
    public const string Sms = "sms";
    public const string Email = "email";
}

public static class FollowUpStatuses
{
    public const string Pending = "Pending";
    public const string Claimed = "Claimed";
    public const string Sent = "Sent";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";
}

public static class FollowUpSkipReasons
{
    public const string MissingContact = "missing_contact";
    public const string MissingDestination = "missing_destination";
    public const string MissingTemplate = "missing_template";
    public const string OptedOut = "opted_out";
    public const string ConsentNotGranted = "marketing_consent_not_granted";
    public const string OutsideSendWindow = "outside_send_window";
    public const string Stale = "stale";
    public const string StopConditionMatched = "stop_condition_matched";
    public const string SequenceMissing = "sequence_missing";
    public const string StepMissing = "step_missing";
}
