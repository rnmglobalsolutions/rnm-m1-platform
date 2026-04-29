namespace RNM.Platform.Contracts.Messaging;

public sealed record SmsMessageRequest(
    string TenantId,
    string ToPhoneNumber,
    string Body,
    string CorrelationId);
