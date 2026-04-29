namespace RNM.Platform.SharedKernel.Correlation;

public sealed record CorrelationContext(CorrelationId CorrelationId)
{
    public string Value => CorrelationId.Value;

    public static CorrelationContext New() => new(CorrelationId.New());

    public static CorrelationContext FromStringOrNew(string? value) => new(CorrelationId.FromStringOrNew(value));
}
