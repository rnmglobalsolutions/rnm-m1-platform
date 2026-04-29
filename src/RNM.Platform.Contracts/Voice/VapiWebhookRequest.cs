namespace RNM.Platform.Contracts.Voice;

public sealed record VapiWebhookRequest(
    string TenantId,
    string EventType,
    string CallId,
    string? CallerPhoneNumber,
    string? Transcript,
    DateTimeOffset ReceivedAtUtc);
