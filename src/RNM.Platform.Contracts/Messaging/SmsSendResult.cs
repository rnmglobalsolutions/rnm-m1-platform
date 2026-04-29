namespace RNM.Platform.Contracts.Messaging;

public sealed record SmsSendResult(
    bool Succeeded,
    string? ProviderMessageId,
    string? Message);
