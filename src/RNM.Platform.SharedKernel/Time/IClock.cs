namespace RNM.Platform.SharedKernel.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
