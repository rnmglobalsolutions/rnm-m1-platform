namespace RNM.Platform.Contracts.Messaging;

public sealed record EmailSendResult(
    bool Succeeded,
    string? ProviderMessageId,
    string? Message);
