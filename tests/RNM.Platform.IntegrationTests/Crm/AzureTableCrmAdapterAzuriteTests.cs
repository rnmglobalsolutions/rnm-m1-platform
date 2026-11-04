using RNM.Platform.Application.Crm;
using Xunit;

namespace RNM.Platform.IntegrationTests.Crm;

[Collection(AzuriteCollection.Name)]
[Trait("Category", "Azurite")]
public sealed class AzureTableCrmAdapterAzuriteTests
{
    private readonly AzuriteTableFixture fixture;

    public AzureTableCrmAdapterAzuriteTests(AzuriteTableFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task UpsertContact_ThenFind_ReturnsContactWithAttributes()
    {
        var adapter = fixture.CreateAdapter();
        var tenant = fixture.NewTenantId("a");
        var cancellation = CancellationToken.None;

        var upsert = await adapter.UpsertContactAsync(
            NewRequest(tenant, "+15551230001", "lead@example.test", "new"),
            cancellation);

        Assert.True(upsert.Succeeded, upsert.Message);
        Assert.True(upsert.Created);
        Assert.False(string.IsNullOrWhiteSpace(upsert.ProviderContactId));

        var byPhone = await adapter.FindContactByPhoneOrEmailAsync(
            new CrmContactLookupRequest(tenant, "corr-1", "+15551230001", null), cancellation);
        var byEmail = await adapter.FindContactByPhoneOrEmailAsync(
            new CrmContactLookupRequest(tenant, "corr-2", null, "LEAD@example.test"), cancellation);

        foreach (var found in new[] { byPhone, byEmail })
        {
            Assert.True(found.Found);
            Assert.Equal(upsert.ProviderContactId, found.ProviderContactId);
            Assert.NotNull(found.Contact);
            Assert.Equal(tenant, found.Contact!.TenantId);
            Assert.Equal("Test Lead", found.Contact.Name);
            Assert.Equal("78701", found.Contact.ZipCode);
            Assert.Equal("new", found.Contact.Attributes["leadStatus"]);
            Assert.Equal("blue-door", found.Contact.Attributes["favoriteColor"]);
        }
    }

    [Fact]
    public async Task UpsertContact_LeadStatusAttribute_IsQueryableByStatus()
    {
        var adapter = fixture.CreateAdapter();
        var tenant = fixture.NewTenantId("a");
        var cancellation = CancellationToken.None;

        var upsert = await adapter.UpsertContactAsync(
            NewRequest(tenant, "+15551230002", "status@example.test", "qualified"),
            cancellation);
        Assert.True(upsert.Succeeded, upsert.Message);

        var match = await adapter.GetLeadsByStatusAsync(
            new CrmLeadQueryRequest(tenant, "corr-3", "qualified"), cancellation);
        var noMatch = await adapter.GetLeadsByStatusAsync(
            new CrmLeadQueryRequest(tenant, "corr-4", "won"), cancellation);

        Assert.True(match.Succeeded, match.Message);
        var lead = Assert.Single(match.Leads);
        Assert.Equal(upsert.ProviderContactId, lead.ProviderContactId);
        Assert.Equal("qualified", lead.OutboundLeadStatus);
        Assert.True(noMatch.Succeeded, noMatch.Message);
        Assert.Empty(noMatch.Leads);
    }

    [Fact]
    public async Task Contact_IsNotVisibleToAnotherTenant()
    {
        var adapter = fixture.CreateAdapter();
        var tenantA = fixture.NewTenantId("a");
        var tenantB = fixture.NewTenantId("b");
        var cancellation = CancellationToken.None;

        var created = await adapter.UpsertContactAsync(
            NewRequest(tenantA, "+15551230003", "shared@example.test", "qualified"),
            cancellation);
        Assert.True(created.Succeeded, created.Message);

        var byPhone = await adapter.FindContactByPhoneOrEmailAsync(
            new CrmContactLookupRequest(tenantB, "corr-5", "+15551230003", null), cancellation);
        var byEmail = await adapter.FindContactByPhoneOrEmailAsync(
            new CrmContactLookupRequest(tenantB, "corr-6", null, "shared@example.test"), cancellation);
        var byStatus = await adapter.GetLeadsByStatusAsync(
            new CrmLeadQueryRequest(tenantB, "corr-7", "qualified"), cancellation);

        Assert.False(byPhone.Found);
        Assert.False(byEmail.Found);
        Assert.True(byStatus.Succeeded, byStatus.Message);
        Assert.Empty(byStatus.Leads);

        // Same email in tenant B is a new contact, and tenant A stays unchanged.
        var other = await adapter.UpsertContactAsync(
            NewRequest(tenantB, "+15551230004", "shared@example.test", "new"),
            cancellation);
        Assert.True(other.Succeeded, other.Message);
        Assert.True(other.Created);

        var tenantAAgain = await adapter.FindContactByPhoneOrEmailAsync(
            new CrmContactLookupRequest(tenantA, "corr-8", null, "shared@example.test"), cancellation);
        Assert.True(tenantAAgain.Found);
        Assert.Equal(tenantA, tenantAAgain.Contact!.TenantId);
        Assert.Equal("qualified", tenantAAgain.Contact.Attributes["leadStatus"]);
    }

    private static CrmContactUpsertRequest NewRequest(string tenantId, string phone, string email, string leadStatus) =>
        new(
            tenantId,
            "test-vertical",
            "corr-upsert",
            null,
            phone,
            email,
            "Test Lead",
            "78701",
            new Dictionary<string, string>
            {
                ["leadStatus"] = leadStatus,
                ["favoriteColor"] = "blue-door"
            });
}
