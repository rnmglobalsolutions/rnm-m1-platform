using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Qualification;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Infrastructure.Configuration;
using Xunit;

namespace RNM.Platform.UnitTests.Configuration;

/// <summary>
/// Pins the service area rules of the checked-in /config files.
/// </summary>
public sealed class RepositoryServiceAreaConfigurationTests
{
    [Theory]
    [InlineData("hvac", ServiceAreaModes.Zip)]
    [InlineData("commercial-real-estate", ServiceAreaModes.Zip)]
    [InlineData("residential-real-estate", ServiceAreaModes.Zip)]
    [InlineData("life-insurance", ServiceAreaModes.State)]
    public async Task RepositoryVerticals_DeclareExpectedModes(string verticalId, string expectedMode)
    {
        var vertical = await RepositoryConfiguration.LoadVerticalAsync(verticalId);

        Assert.Equal(expectedMode, vertical.ServiceArea?.Mode);
    }

    [Fact]
    public async Task LifeInsurance_DoesNotAskTheCallerForLeadSourceOrCampaignId()
    {
        var vertical = await RepositoryConfiguration.LoadVerticalAsync("life-insurance");

        Assert.DoesNotContain("leadSource", vertical.QualificationFields);
        Assert.DoesNotContain("campaignId", vertical.QualificationFields);
        Assert.Contains("state", vertical.ServiceAreaFieldAliases.StateFields ?? []);
    }

    [Fact]
    public async Task Yartex_ResolvesToStateModeWithItsApprovedStates()
    {
        var validator = new ConfigurationValidator();
        var tenant = await new JsonTenantConfigurationProvider(RepositoryConfiguration.ConfigRoot, validator)
            .GetTenantConfigurationAsync("yartex", CancellationToken.None);
        var vertical = await RepositoryConfiguration.LoadVerticalAsync(tenant.VerticalId.Value);

        var policy = ServiceAreaPolicy.Resolve(vertical, tenant);

        Assert.Equal(ServiceAreaModes.State, policy.Mode);
        Assert.True(policy.IsConfigured);
        Assert.Equal(["CA", "FL", "NE", "TX"], policy.States.OrderBy(state => state, StringComparer.Ordinal));
    }
}
