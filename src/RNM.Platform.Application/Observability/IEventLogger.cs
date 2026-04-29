namespace RNM.Platform.Application.Observability;

public interface IEventLogger
{
    Task LogEventAsync(
        string eventName,
        IReadOnlyDictionary<string, string> properties,
        CancellationToken cancellationToken);
}
