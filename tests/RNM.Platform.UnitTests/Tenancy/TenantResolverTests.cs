using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Tenancy;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.Tenancy;

public sealed class TenantResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsTenantContext_WhenTenantAndVerticalExist()
    {
        var tenantConfiguration = new TenantConfiguration(
            new TenantId("tenant-a"),
            new VerticalId("vertical-a"),
            "Tenant A",
            "America/Chicago",
            new ServiceAreaConfiguration(["75001"], ["Addison"], null),
            new ProviderConfiguration("Crm", "Booking", "Sms", "Email"),
            new SecretNameConfiguration("crm", "booking", "vapi", "sid", "token", "email"),
            new CommunicationConfiguration(
                "+15550001000",
                "booking@example.com",
                new ConfirmationTemplateConfiguration(
                    "SMS template {{bookingDate}}",
                    "Email subject {{bookingDate}}",
                    "Email body {{bookingStart}}")));
        var verticalConfiguration = new VerticalConfiguration(
            new VerticalId("vertical-a"),
            "Vertical A",
            ["serviceNeed"],
            ["GeneralInquiry"],
            ServiceAreaFieldAliasConfiguration.Defaults());
        var resolver = new TenantResolver(
            new StubTenantConfigurationProvider(tenantConfiguration),
            new StubVerticalConfigurationProvider(verticalConfiguration));

        var context = await resolver.ResolveAsync("tenant-a", CancellationToken.None);

        Assert.Equal("tenant-a", context.TenantId);
        Assert.Equal("vertical-a", context.VerticalId);
        Assert.Equal("Tenant A", context.BusinessName);
        Assert.Contains("75001", context.ServiceAreaZipCodes);
        Assert.Equal("vapi", context.SecretNames.VoiceWebhookSecret);
    }

    [Fact]
    public async Task ResolveAsync_ThrowsTenantResolutionException_WhenTenantIdIsMissing()
    {
        var resolver = new TenantResolver(
            new StubTenantConfigurationProvider(null),
            new StubVerticalConfigurationProvider(null));

        await Assert.ThrowsAsync<TenantResolutionException>(
            () => resolver.ResolveAsync("", CancellationToken.None));
    }

    private sealed class StubTenantConfigurationProvider : ITenantConfigurationProvider
    {
        private readonly TenantConfiguration? configuration;

        public StubTenantConfigurationProvider(TenantConfiguration? configuration)
        {
            this.configuration = configuration;
        }

        public Task<TenantConfiguration> GetTenantConfigurationAsync(string tenantId, CancellationToken cancellationToken)
        {
            return Task.FromResult(configuration ?? throw new ConfigurationException("Tenant not found."));
        }
    }

    private sealed class StubVerticalConfigurationProvider : IVerticalConfigurationProvider
    {
        private readonly VerticalConfiguration? configuration;

        public StubVerticalConfigurationProvider(VerticalConfiguration? configuration)
        {
            this.configuration = configuration;
        }

        public Task<VerticalConfiguration> GetVerticalConfigurationAsync(string verticalId, CancellationToken cancellationToken)
        {
            return Task.FromResult(configuration ?? throw new ConfigurationException("Vertical not found."));
        }
    }
}
