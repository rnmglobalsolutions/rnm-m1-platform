namespace RNM.Platform.Contracts.Webhooks;

public sealed record TwilioSmsStatusWebhook(
    string TenantId,
    string MessageSid,
    string MessageStatus,
    string? ErrorCode,
    string? To,
    string? From);
