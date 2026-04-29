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

    private static RnmRuntimeConfiguration CreateConfiguration(
        string environmentName,
        string? keyVaultUri,
        bool allowFallback = false)
    {
        return new RnmRuntimeConfiguration(
            environmentName,
            "../../../config",
            "internal-api-key",
            keyVaultUri,
            allowFallback);
    }
}
