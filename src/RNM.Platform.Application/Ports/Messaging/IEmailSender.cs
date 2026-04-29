using RNM.Platform.Application.Confirmations;

namespace RNM.Platform.Application.Ports.Messaging;

public interface IEmailSender
{
    Task<EmailSendResult> SendEmailAsync(
        EmailMessageRequest request,
        CancellationToken cancellationToken);
}
