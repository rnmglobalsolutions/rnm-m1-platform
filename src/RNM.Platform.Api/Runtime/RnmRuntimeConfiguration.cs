using RNM.Platform.Api.Security;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Runtime;

internal sealed record RnmRuntimeConfiguration(
    string EnvironmentName,
    string ConfigRoot,
    string InternalApiKey,
    string? KeyVaultUri,
    bool AllowEnvironmentSecretFallback)
{
    public bool IsLocal =>
        string.Equals(EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase)
        || string.Equals(EnvironmentName, "Local", StringComparison.OrdinalIgnoreCase);

    public bool UseKeyVaultSecrets => !IsLocal;

    public static RnmRuntimeConfiguration FromEnvironment()
    {
        return new RnmRuntimeConfiguration(
            Environment.GetEnvironmentVariable("RNM_ENVIRONMENT") ?? "Development",
            Environment.GetEnvironmentVariable("RNM_CONFIG_ROOT") ?? "../../../config",
            Environment.GetEnvironmentVariable(ApiSecretNames.InternalApiKeySecretNameSetting) ?? string.Empty,
            Environment.GetEnvironmentVariable("RNM_KEY_VAULT_URI"),
            string.Equals(
                Environment.GetEnvironmentVariable("RNM_ALLOW_ENV_SECRET_FALLBACK"),
                "true",
                StringComparison.OrdinalIgnoreCase));
    }

    public static Uri CreateUriFromEnvironment(string environmentVariableName, string fallback)
    {
        var configuredValue = Environment.GetEnvironmentVariable(environmentVariableName);
        var uriValue = string.IsNullOrWhiteSpace(configuredValue)
            ? fallback
            : configuredValue;

        return Uri.TryCreate(uriValue, UriKind.Absolute, out var uri)
            ? uri
            : new Uri(fallback);
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EnvironmentName))
        {
            throw new InvalidOperationException("RNM_ENVIRONMENT is required.");
        }

        if (string.IsNullOrWhiteSpace(ConfigRoot))
        {
            throw new InvalidOperationException("RNM_CONFIG_ROOT is required.");
        }

        if (string.IsNullOrWhiteSpace(InternalApiKey))
        {
            throw new InvalidOperationException($"{ApiSecretNames.InternalApiKeySetting} is required.");
        }

        if (UseKeyVaultSecrets && string.IsNullOrWhiteSpace(KeyVaultUri))
        {
            throw new InvalidOperationException("RNM_KEY_VAULT_URI is required outside local development.");
        }

        if (UseKeyVaultSecrets && !Uri.TryCreate(KeyVaultUri, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("RNM_KEY_VAULT_URI must be an absolute URI.");
        }
    }
}

internal static class RuntimeSecretProviderFactory
{
    public static ISecretProvider Create(RnmRuntimeConfiguration runtimeConfiguration)
    {
        var environmentSecretProvider = new EnvironmentSecretProvider();
        if (!runtimeConfiguration.UseKeyVaultSecrets)
        {
            return environmentSecretProvider;
        }

        var keyVaultSecretProvider = new KeyVaultSecretProvider(new Uri(runtimeConfiguration.KeyVaultUri!));
        return runtimeConfiguration.AllowEnvironmentSecretFallback
            ? new CompositeSecretProvider(keyVaultSecretProvider, environmentSecretProvider)
            : keyVaultSecretProvider;
    }
}
