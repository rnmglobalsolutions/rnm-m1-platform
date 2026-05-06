using System.Xml.Linq;
using Xunit;

namespace RNM.Platform.UnitTests.Infrastructure;

public sealed class PackagingAndHostingTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ApiProject_CopiesRepoConfigIntoPublishOutput()
    {
        var projectPath = Path.Combine(RepositoryRoot, "src", "RNM.Platform.Api", "RNM.Platform.Api.csproj");
        var project = XDocument.Load(projectPath);

        var configContent = project
            .Descendants("Content")
            .SingleOrDefault(element => element.Attribute("Include")?.Value == @"..\..\config\**\*");

        Assert.NotNull(configContent);
        Assert.Equal("PreserveNewest", configContent.Element("CopyToPublishDirectory")?.Value);
        Assert.Equal(@"config\%(RecursiveDir)%(Filename)%(Extension)", configContent.Element("TargetPath")?.Value);
    }

    [Fact]
    public void FunctionAppBicep_UsesFlexConsumptionForDotNetTenIsolated()
    {
        var bicepPath = Path.Combine(RepositoryRoot, "infra", "modules", "functionApp.bicep");
        var bicep = File.ReadAllText(bicepPath);

        Assert.Contains("name: 'FC1'", bicep);
        Assert.Contains("tier: 'FlexConsumption'", bicep);
        Assert.DoesNotContain("name: 'Y1'", bicep);
        Assert.Contains("type: 'SystemAssignedIdentity'", bicep);
        Assert.Contains("name: 'dotnet-isolated'", bicep);
        Assert.Contains("version: '10.0'", bicep);
        Assert.Contains("instanceMemoryMB int = 512", bicep);
    }

    [Fact]
    public void FunctionAppBicep_ConfigRootMatchesPublishedConfigPath()
    {
        var bicepPath = Path.Combine(RepositoryRoot, "infra", "modules", "functionApp.bicep");
        var bicep = File.ReadAllText(bicepPath);

        Assert.Contains("param configRoot string = '/home/site/wwwroot/config'", bicep);
        Assert.Contains("RNM_CONFIG_ROOT: configRoot", bicep);
    }

    [Fact]
    public void FunctionAppBicep_UsesKeyVaultReferencesForResolvedAppSecrets()
    {
        var bicepPath = Path.Combine(RepositoryRoot, "infra", "modules", "functionApp.bicep");
        var bicep = File.ReadAllText(bicepPath);

        Assert.Contains("RNM_INTERNAL_API_KEY_SECRET_NAME: '@Microsoft.KeyVault(SecretUri=${keyVaultUri}secrets/${internalApiKeySecretName}/)'", bicep);
        Assert.Contains("SENDGRID_API_KEY: '@Microsoft.KeyVault(SecretUri=${keyVaultUri}secrets/${sendGridApiKeySecretName}/)'", bicep);
    }

    [Fact]
    public void FunctionAppBicep_ClearsAppWideCors()
    {
        var bicepPath = Path.Combine(RepositoryRoot, "infra", "modules", "functionApp.bicep");
        var bicep = File.ReadAllText(bicepPath);

        Assert.DoesNotContain("allowedCorsOrigins", bicep);
        Assert.Contains("cors:", bicep);
        Assert.Contains("allowedOrigins: []", bicep);
        Assert.Contains("supportCredentials: false", bicep);
        Assert.DoesNotContain("'*'", bicep);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RNM.Platform.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
