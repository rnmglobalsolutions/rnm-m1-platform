namespace RNM.Platform.Application.Configuration;

public sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message)
        : base(message)
    {
    }
}
