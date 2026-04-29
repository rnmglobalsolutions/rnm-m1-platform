namespace RNM.Platform.Application.Configuration;

public sealed record ConfigurationValidationResult(IReadOnlyCollection<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static ConfigurationValidationResult Valid { get; } = new([]);

    public static ConfigurationValidationResult Invalid(params string[] errors) => new(errors);
}
