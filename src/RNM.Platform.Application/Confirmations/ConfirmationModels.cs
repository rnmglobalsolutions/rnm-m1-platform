using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Crm;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Confirmations;

public sealed record BookingConfirmationRequest(
    string TenantId,
    string VerticalId,
    string CorrelationId,
    BookingDecisionResult BookingDecision,
    CrmSyncResult CrmSyncResult,
    string? CustomerPhoneNumber,
    string? CustomerEmail,
    string? ServiceType,
    ConfirmationTemplateSet Templates);

public sealed record ConfirmationTemplateSet(
    string SmsBodyTemplate,
    string? EmailSubjectTemplate = null,
    string? EmailBodyTemplate = null)
{
    public static ConfirmationTemplateSet FromConfiguration(
        ConfirmationTemplateConfiguration configuration)
    {
        return new ConfirmationTemplateSet(
            configuration.SmsBodyTemplate,
            configuration.EmailSubjectTemplate,
            configuration.EmailBodyTemplate);
    }
}

public sealed record BookingConfirmationResult(
    ConfirmationChannelResult Sms,
    ConfirmationChannelResult Email)
{
    public bool SmsSent => Sms.Status is ConfirmationChannelStatus.Sent;

    public bool EmailSent => Email.Status is ConfirmationChannelStatus.Sent;
}

public sealed record ConfirmationChannelResult(
    ConfirmationChannel Channel,
    ConfirmationChannelStatus Status,
    ConfirmationFailureReason? FailureReason = null,
    string? ProviderMessageId = null);

public enum ConfirmationChannel
{
    Sms = 0,
    Email = 1
}

public enum ConfirmationChannelStatus
{
    Sent = 0,
    Failed = 1,
    Skipped = 2
}

public enum ConfirmationFailureReason
{
    BookingNotCompleted = 0,
    MissingPhoneNumber = 1,
    MissingSmsTemplate = 2,
    SmsSendFailed = 3,
    SmsSenderException = 4,
    MissingEmail = 5,
    MissingEmailTemplate = 6,
    EmailSendFailed = 7,
    EmailSenderException = 8
}

public sealed record SmsMessageRequest(
    string TenantId,
    string CorrelationId,
    string ToPhoneNumber,
    string Body);

public sealed record SmsSendResult(
    bool Succeeded,
    string? ProviderMessageId = null,
    string? Message = null);

public sealed record EmailMessageRequest(
    string TenantId,
    string CorrelationId,
    string ToEmail,
    string Subject,
    string Body);

public sealed record EmailSendResult(
    bool Succeeded,
    string? ProviderMessageId = null,
    string? Message = null);
