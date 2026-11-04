using RNM.Platform.SharedKernel.Correlation;

namespace RNM.Platform.Application.Observability;

/// <summary>
/// Ambient per-call telemetry scope. The Vapi webhook opens it only after the webhook
/// signature is validated and the payload is parsed; the event logger adds the provider
/// call ID to every event emitted inside the scope. The correlation ID stays per request.
/// Telemetry only: never use this as a storage key or a filter.
/// </summary>
public static class CallTelemetryContext
{
    public const string CallIdPropertyName = "callId";

    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string? CurrentCallId => CurrentValue.Value;

    /// <summary>
    /// Starts a call scope. Values that fail <see cref="CorrelationId.TryNormalize"/> are
    /// ignored, and the returned scope then leaves the current value unchanged.
    /// </summary>
    public static IDisposable BeginCall(string? callId)
    {
        if (!CorrelationId.TryNormalize(callId, out var normalizedCallId))
        {
            return new Scope(CurrentValue.Value, changed: false);
        }

        var scope = new Scope(CurrentValue.Value, changed: true);
        CurrentValue.Value = normalizedCallId;
        return scope;
    }

    /// <summary>
    /// Returns the properties with the current call ID added. An explicit callId property
    /// wins; outside a scope the input is returned unchanged.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Enrich(IReadOnlyDictionary<string, string> properties)
    {
        var callId = CurrentValue.Value;
        if (callId is null
            || properties.Keys.Any(key => string.Equals(key, CallIdPropertyName, StringComparison.OrdinalIgnoreCase)))
        {
            return properties;
        }

        var enriched = new Dictionary<string, string>(properties, StringComparer.Ordinal)
        {
            [CallIdPropertyName] = callId
        };
        return enriched;
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? previous;
        private readonly bool changed;
        private bool disposed;

        public Scope(string? previous, bool changed)
        {
            this.previous = previous;
            this.changed = changed;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (changed)
            {
                CurrentValue.Value = previous;
            }
        }
    }
}
