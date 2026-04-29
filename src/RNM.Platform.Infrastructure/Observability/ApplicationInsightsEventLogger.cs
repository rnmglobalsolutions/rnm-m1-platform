using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using RNM.Platform.Application.Observability;

namespace RNM.Platform.Infrastructure.Observability;

public sealed class ApplicationInsightsEventLogger : IEventLogger
{
    private readonly TelemetryClient telemetryClient;
    private readonly ILogger<ApplicationInsightsEventLogger> logger;

    public ApplicationInsightsEventLogger(
        TelemetryClient telemetryClient,
        ILogger<ApplicationInsightsEventLogger> logger)
    {
        this.telemetryClient = telemetryClient;
        this.logger = logger;
    }

    public Task LogEventAsync(
        string eventName,
        IReadOnlyDictionary<string, string> properties,
        CancellationToken cancellationToken)
    {
        try
        {
            var safeProperties = properties
            .Where(property => !SafeTelemetryProperties.IsSensitiveName(property.Key))
            .ToDictionary(
                property => property.Key,
                property => SafeTelemetryProperties.SanitizeValue(property.Value),
                StringComparer.Ordinal);

            safeProperties["eventName"] = SafeTelemetryProperties.SanitizeValue(eventName);

            telemetryClient.TrackEvent(eventName, safeProperties);

            using var scope = logger.BeginScope(safeProperties);
            logger.LogInformation("Telemetry event {EventName}", eventName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Telemetry event logging failed for {EventName}", eventName);
        }

        return Task.CompletedTask;
    }
}
