namespace RNM.Platform.SharedKernel.Results;

public sealed record Error(string Code, string Message)
{
    public static Error None { get; } = new("none", string.Empty);
}
