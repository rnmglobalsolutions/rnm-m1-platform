using RNM.Platform.Application.Qualification;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.Qualification;

public sealed class ServiceAreaPolicyTests
{
    [Fact]
    public void Resolve_DefaultsToZip_WhenNoModeIsConfiguredAnywhere()
    {
        var policy = ServiceAreaPolicy.Resolve(Vertical(null), Tenant(new ServiceAreaConfiguration(["75001"], [], null)));

        Assert.Equal(ServiceAreaModes.Zip, policy.Mode);
        Assert.Equal(["75001"], policy.ZipCodes);
        Assert.True(policy.IsConfigured);
    }

    [Fact]
    public void Resolve_UsesVerticalModeAndStates_WhenTenantDeclaresNone()
    {
        var policy = ServiceAreaPolicy.Resolve(
            Vertical(new VerticalServiceAreaConfiguration(ServiceAreaModes.State, ["TX", "CA"])),
            Tenant(new ServiceAreaConfiguration([], [], null)));

        Assert.Equal(ServiceAreaModes.State, policy.Mode);
        Assert.Equal(["TX", "CA"], policy.States);
        Assert.True(policy.IsConfigured);
    }

    [Fact]
    public void Resolve_TenantModeOverridesVertical()
    {
        var policy = ServiceAreaPolicy.Resolve(
            Vertical(new VerticalServiceAreaConfiguration(ServiceAreaModes.State, ["TX"])),
            Tenant(new ServiceAreaConfiguration([], [], null, ServiceAreaModes.None)));

        Assert.Equal(ServiceAreaModes.None, policy.Mode);
        Assert.True(policy.IsConfigured);
    }

    [Fact]
    public void Resolve_TenantStatesOverrideVerticalStates()
    {
        var policy = ServiceAreaPolicy.Resolve(
            Vertical(new VerticalServiceAreaConfiguration(ServiceAreaModes.State, ["TX"])),
            Tenant(new ServiceAreaConfiguration([], [], null, null, ["CA", "FL"])));

        Assert.Equal(ServiceAreaModes.State, policy.Mode);
        Assert.Equal(["CA", "FL"], policy.States);
    }

    [Fact]
    public void Resolve_TenantZipModeOverridesVerticalState()
    {
        var policy = ServiceAreaPolicy.Resolve(
            Vertical(new VerticalServiceAreaConfiguration(ServiceAreaModes.State, ["TX"])),
            Tenant(new ServiceAreaConfiguration(["78040"], [], null, ServiceAreaModes.Zip)));

        Assert.Equal(ServiceAreaModes.Zip, policy.Mode);
        Assert.Equal(["78040"], policy.ZipCodes);
    }

    [Theory]
    [InlineData(ServiceAreaModes.Zip)]
    [InlineData(ServiceAreaModes.State)]
    public void Resolve_IsNotConfigured_WhenModeListIsEmpty(string mode)
    {
        var policy = ServiceAreaPolicy.Resolve(null, Tenant(new ServiceAreaConfiguration([], ["Austin"], null, mode)));

        Assert.Equal(mode, policy.Mode);
        Assert.False(policy.IsConfigured);
    }

    [Fact]
    public void Resolve_IsConfigured_WhenModeIsNoneWithoutLists()
    {
        var policy = ServiceAreaPolicy.Resolve(null, Tenant(new ServiceAreaConfiguration([], [], null, ServiceAreaModes.None)));

        Assert.True(policy.IsConfigured);
    }

    [Theory]
    [InlineData("TX", "TX")]
    [InlineData("tx", "TX")]
    [InlineData("  tx  ", "TX")]
    [InlineData("T.X.", "TX")]
    [InlineData("Texas", "TX")]
    [InlineData("new york", "NY")]
    [InlineData("District of Columbia", "DC")]
    [InlineData("D.C.", "DC")]
    [InlineData("Puerto Rico", "PR")]
    [InlineData("guam", "GU")]
    [InlineData("U.S. Virgin Islands", "VI")]
    [InlineData("American Samoa", "AS")]
    [InlineData("Northern Mariana Islands", "MP")]
    [InlineData("Nebraska", "NE")]
    public void TryNormalize_AcceptsCodesAndEnglishNames(string input, string expected)
    {
        Assert.True(UsStates.TryNormalize(input, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ZZ")]
    [InlineData("Tex")]
    [InlineData("AE")]
    [InlineData("AA")]
    [InlineData("AP")]
    [InlineData("Ontario")]
    [InlineData("TX 75001")]
    [InlineData("*")]
    public void TryNormalize_RejectsUnknownInput(string? input)
    {
        Assert.False(UsStates.TryNormalize(input, out _));
    }

    [Fact]
    public void Codes_ContainsFiftyStatesDcAndFiveTerritories()
    {
        Assert.Equal(56,UsStates.Codes.Count);
        Assert.Contains("DC", UsStates.Codes);
        Assert.Contains("MP", UsStates.Codes);
    }

    private static VerticalConfiguration Vertical(VerticalServiceAreaConfiguration? serviceArea) =>
        new(
            new VerticalId("vertical-a"),
            "Vertical A",
            ["name"],
            ["GeneralInquiry"],
            ServiceAreaFieldAliasConfiguration.Defaults(),
            ServiceArea: serviceArea);

    private static TenantConfiguration Tenant(ServiceAreaConfiguration serviceArea) =>
        new(
            new TenantId("tenant-a"),
            new VerticalId("vertical-a"),
            "Tenant A",
            "America/Chicago",
            serviceArea,
            new ProviderConfiguration("Crm", "Booking", "Sms", "Email"),
            new SecretNameConfiguration("crm", "booking", "vapi", "sid", "token", "email"),
            new CommunicationConfiguration(
                "+15550001000",
                "booking@example.com",
                new ConfirmationTemplateConfiguration("SMS", "Subject", "Body")));
}
