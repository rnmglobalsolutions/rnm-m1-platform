using RNM.Platform.Contracts.Voice;

namespace RNM.Platform.Api.Voice;

public sealed record VapiWebhookParseResult(
    bool IsValid,
    VapiWebhookEnvelope? Envelope,
    string? ErrorCode)
{
    public static VapiWebhookParseResult Success(VapiWebhookEnvelope envelope) =>
        new(IsValid: true, envelope, ErrorCode: null);

    public static VapiWebhookParseResult Invalid(string errorCode) =>
        new(IsValid: false, Envelope: null, errorCode);
}
