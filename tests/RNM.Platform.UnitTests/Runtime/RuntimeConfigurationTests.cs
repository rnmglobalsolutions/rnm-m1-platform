using RNM.Platform.Api.Runtime;
using RNM.Platform.Infrastructure.Secrets;
using Xunit;

namespace RNM.Platform.UnitTests.Runtime;

public sealed class RuntimeConfigurationTests
{
    [Theory]
    [InlineData("Local")]
    [InlineData("Development")]
    public void CreateSecretProvider_UsesLocalProvider_ForLocalEnvironments(string environmentName)
    {
        var configuration = CreateConfiguration(environmentName, keyVaultUri: null);

        var provider = RuntimeSecretProviderFactory.Create(configuration);

        Assert.IsType<EnvironmentSecretProvider>(provider);
        Assert.False(configuration.UseKeyVaultSecrets);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("staging")]
    [InlineData("prod")]
    public void CreateSecretProvider_UsesKeyVaultProvider_ForAzureEnvironments(string environmentName)
    {
        var configuration = CreateConfiguration(environmentName, "https://example-vault.vault.azure.net/");

        var provider = RuntimeSecretProviderFactory.Create(configuration);

        Assert.IsType<KeyVaultSecretProvider>(provider);
        Assert.True(configuration.UseKeyVaultSecrets);
    }

    [Fact]
    public void CreateSecretProvider_UsesCompositeProvider_WhenAzureFallbackIsExplicitlyEnabled()
    {
        var configuration = CreateConfiguration(
            "dev",
            "https://example-vault.vault.azure.net/",
            allowFallback: true);

        var provider = RuntimeSecretProviderFactory.Create(configuration);

        Assert.IsType<CompositeSecretProvider>(provider);
    }

    [Fact]
    public void Validate_RequiresInternalApiKey_ByDefault()
    {
        var configuration = CreateConfiguration(
            "dev",
            "https://example-vault.vault.azure.net/",
            internalApiKey: string.Empty);

        var exception = Assert.Throws<InvalidOperationException>(configuration.Validate);

        Assert.Equal("RNM_INTERNAL_API_KEY_SECRET_NAME is required.", exception.Message);
    }

    [Fact]
    public void Validate_AllowsMissingInternalApiKey_WhenRequirementDisabled()
    {
        var configuration = CreateConfiguration(
            "dev",
            "https://example-vault.vault.azure.net/",
            internalApiKey: string.Empty,
            requireInternalApiKey: false);

        configuration.Validate();
    }

    private static RnmRuntimeConfiguration CreateConfiguration(
        string environmentName,
        string? keyVaultUri,
        bool allowFallback = false,
        string internalApiKey = "internal-api-key",
        bool requireInternalApiKey = true)
    {
        return new RnmRuntimeConfiguration(
            environmentName,
            "../../../config",
            internalApiKey,
            keyVaultUri,
            allowFallback,
            requireInternalApiKey);
    }
}
