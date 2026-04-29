namespace RNM.Platform.Contracts.Messaging;

public sealed record EmailMessageRequest(
    string TenantId,
    string ToEmail,
    string Subject,
    string Body,
    string CorrelationId,
    CalendarInviteDto? CalendarInvite);

public sealed record CalendarInviteDto(
    string FileName,
    string ContentType,
    string Content);
