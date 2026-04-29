using RNM.Platform.Application.Confirmations;

namespace RNM.Platform.Application.Ports.Messaging;

public interface ISmsSender
{
    Task<SmsSendResult> SendSmsAsync(
        SmsMessageRequest request,
        CancellationToken cancellationToken);
}
