using RNM.Platform.Application.Observability;

namespace RNM.Platform.Api.Observability;

public static class SafeEventLoggerExtensions
{
    public static async Task TryLogEventAsync(
        this IEventLogger eventLogger,
        string eventName,
        IReadOnlyDictionary<string, string> properties,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger.LogEventAsync(eventName, properties, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Telemetry is best-effort and must never block request handling.
        }
    }
}
