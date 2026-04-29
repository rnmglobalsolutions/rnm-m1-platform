namespace RNM.Platform.Contracts.Voice;

public sealed record VapiWebhookResponse(
    bool Accepted,
    string CorrelationId,
    string? Message);
