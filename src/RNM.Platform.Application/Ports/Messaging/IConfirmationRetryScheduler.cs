using RNM.Platform.Application.Confirmations;

namespace RNM.Platform.Application.Ports.Messaging;

public interface IConfirmationRetryScheduler
{
    Task<bool> ScheduleAsync(
        ConfirmationRetryRequest request,
        CancellationToken cancellationToken);
}
