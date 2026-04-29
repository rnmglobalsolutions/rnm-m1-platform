namespace RNM.Platform.Api.Voice;

public sealed class VapiWebhookOptions
{
    public const int DefaultMaxBodyBytes = 262_144;
    public const int DefaultJsonMaxDepth = 32;

    public int MaxBodyBytes { get; init; } = DefaultMaxBodyBytes;

    public int JsonMaxDepth { get; init; } = DefaultJsonMaxDepth;

    public static VapiWebhookOptions FromEnvironment()
    {
        return new VapiWebhookOptions
        {
            MaxBodyBytes = ReadPositiveInt("RNM_VAPI_WEBHOOK_MAX_BODY_BYTES", DefaultMaxBodyBytes),
            JsonMaxDepth = ReadPositiveInt("RNM_VAPI_WEBHOOK_JSON_MAX_DEPTH", DefaultJsonMaxDepth)
        };
    }

    private static int ReadPositiveInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsedValue) && parsedValue > 0
            ? parsedValue
            : defaultValue;
    }
}
