using Azure.Data.Tables;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using Xunit;

namespace RNM.Platform.IntegrationTests.Crm;

/// <summary>
/// Backlog item 17. Azurite does not enforce Azure Table Storage's 255-property limit, so every test here
/// inspects the stored row itself and counts its raw properties.
/// </summary>
[Collection(AzuriteCollection.Name)]
[Trait("Category", "Azurite")]
public sealed class AzureTableCrmAdapterAttributeLimitAzuriteTests
{
    private const int TableStorageMaxProperties = 255;

    private readonly AzuriteTableFixture fixture;

    public AzureTableCrmAdapterAttributeLimitAzuriteTests(AzuriteTableFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task UpsertContact_With300Attributes_SavesAndReadsBackAllOfThem()
    {
        var adapter = fixture.CreateAdapter();
        var tenant = fixture.NewTenantId("limit");
        var cancellation = CancellationToken.None;
        var attributes = ManyAttributes(300);

        var upsert = await adapter.UpsertContactAsync(
            NewRequest(tenant, "+15551240001", "many@example.test", attributes),
            cancellation);

        Assert.True(upsert.Succeeded, upsert.Message);
        Assert.True(upsert.Created);

        var byPhone = await adapter.FindContactByPhoneOrEmailAsync(
            new CrmContactLookupRequest(tenant, "corr-1", "+15551240001", null), cancellation);
        var byEmail = await adapter.FindContactByPhoneOrEmailAsync(
            new CrmContactLookupRequest(tenant, "corr-2", null, "MANY@example.test"), cancellation);

        foreach (var found in new[] { byPhone, byEmail })
        {
            Assert.True(found.Found);
            Assert.Equal(upsert.ProviderContactId, found.ProviderContactId);
            foreach (var (name, value) in attributes)
            {
                Assert.Equal(value, found.Contact!.Attributes[name]);
            }
        }

        var row = await ReadRowAsync(tenant, upsert.ProviderContactId!);
        Assert.True(
            CountStoredProperties(row) <= TableStorageMaxProperties,
            $"Stored row has {CountStoredProperties(row)} properties.");
        Assert.True(row.ContainsKey("AttributesOverflowJson"));

        var otherTenant = fixture.NewTenantId("other");
        var hidden = await adapter.FindContactByPhoneOrEmailAsync(
            new CrmContactLookupRequest(otherTenant, "corr-3", "+15551240001", "many@example.test"), cancellation);
        Assert.False(hidden.Found);
    }

    [Fact]
    public async Task UpsertContact_UpdatingOverflowedAttribute_DoesNotCreateAColumn()
    {
        var adapter = fixture.CreateAdapter();
        var tenant = fixture.NewTenantId("limit");
        var cancellation = CancellationToken.None;
        var first = await adapter.UpsertContactAsync(
            NewRequest(tenant, "+15551240002", "update@example.test", ManyAttributes(300)),
            cancellation);
        Assert.True(first.Succeeded, first.Message);
        var before = CountStoredProperties(await ReadRowAsync(tenant, first.ProviderContactId!));

        var second = await adapter.UpsertContactAsync(
            NewRequest(
                tenant,
                "+15551240002",
                "update@example.test",
                new Dictionary<string, string> { ["extra299"] = "changed", ["extra000"] = "changed-column" }),
            cancellation);
        Assert.True(second.Succeeded, second.Message);
        Assert.False(second.Created);
        Assert.Equal(first.ProviderContactId, second.ProviderContactId);

        var row = await ReadRowAsync(tenant, first.ProviderContactId!);
        Assert.False(row.ContainsKey("Attr_extra299"));
        Assert.Equal(before, CountStoredProperties(row));

        var found = await adapter.FindContactByPhoneOrEmailAsync(
            new CrmContactLookupRequest(tenant, "corr-4", "+15551240002", null), cancellation);
        Assert.Equal("changed", found.Contact!.Attributes["extra299"]);
        Assert.Equal("changed-column", found.Contact.Attributes["extra000"]);
        Assert.Equal("v150", found.Contact.Attributes["extra150"]);
    }

    [Fact]
    public async Task LeadStatus_StaysAColumn_AndOutboundWritersKeepWorkingOnAnOverflowedRow()
    {
        var adapter = fixture.CreateAdapter();
        var tenant = fixture.NewTenantId("limit");
        var cancellation = CancellationToken.None;
        var attributes = ManyAttributes(300);
        attributes[CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptIn;
        attributes[CrmContactAttributeNames.LeadStatus] = "qualified";
        var upsert = await adapter.UpsertContactAsync(
            NewRequest(tenant, "+15551240003", "status@example.test", attributes),
            cancellation);
        Assert.True(upsert.Succeeded, upsert.Message);

        var row = await ReadRowAsync(tenant, upsert.ProviderContactId!);
        Assert.Equal("qualified", row["Attr_leadStatus"]);
        Assert.Equal(CrmConsentStatuses.OptIn, row["Attr_consentStatus"]);

        var byStatus = await adapter.GetLeadsByStatusAsync(
            new CrmLeadQueryRequest(tenant, "corr-5", "qualified"), cancellation);
        Assert.True(byStatus.Succeeded, byStatus.Message);
        Assert.Contains(byStatus.Leads, lead => lead.ProviderContactId == upsert.ProviderContactId);
        Assert.Equal("v299", byStatus.Leads.Single().Attributes["extra299"]);

        var attempt = await adapter.RecordOutboundAttemptAsync(
            new CrmOutboundAttemptRequest(tenant, "corr-6", upsert.ProviderContactId!, "no_answer"),
            cancellation);
        Assert.True(attempt.Succeeded, attempt.Message);
        var contacted = await adapter.GetLeadsByStatusAsync(
            new CrmLeadQueryRequest(tenant, "corr-7", CrmOutboundLeadStatuses.Contacted), cancellation);
        Assert.Single(contacted.Leads);
        Assert.Equal("1", contacted.Leads.Single().Attributes[CrmContactAttributeNames.OutboundAttemptCount]);

        var reactivated = await adapter.MarkLeadReactivatedAsync(
            new CrmLeadReactivationRequest(tenant, "corr-8", upsert.ProviderContactId!),
            cancellation);
        Assert.True(reactivated.Succeeded, reactivated.Message);
        var afterReactivation = await adapter.GetLeadsByStatusAsync(
            new CrmLeadQueryRequest(tenant, "corr-9", CrmOutboundLeadStatuses.Reactivated), cancellation);
        Assert.Single(afterReactivation.Leads);
        Assert.Equal("v299", afterReactivation.Leads.Single().Attributes["extra299"]);
    }

    [Fact]
    public async Task WorstCaseRow_AllKnownAttributesAndAllWriters_StaysWithinTableStorageLimit()
    {
        var adapter = fixture.CreateAdapter();
        var tenant = fixture.NewTenantId("limit");
        var cancellation = CancellationToken.None;
        var attributes = ManyAttributes(400);
        foreach (var name in CrmContactAttributeNames.PlatformManaged)
        {
            attributes[name] = "known";
        }

        attributes[CrmContactAttributeNames.ConsentStatus] = CrmConsentStatuses.OptIn;
        var upsert = await adapter.UpsertContactAsync(
            NewRequest(tenant, "+15551240004", "worst@example.test", attributes) with
            {
                NeedsFollowUp = true,
                FollowUpReason = "reason",
                FollowUpAt = DateTimeOffset.UtcNow.AddDays(1)
            },
            cancellation);
        Assert.True(upsert.Succeeded, upsert.Message);
        var id = upsert.ProviderContactId!;

        Assert.True((await adapter.ApplyTagsAsync(new CrmTagRequest(tenant, "c", id, ["a", "b"]), cancellation)).Succeeded);
        Assert.True((await adapter.RecordOutboundAttemptAsync(
            new CrmOutboundAttemptRequest(tenant, "c", id, "no_answer"), cancellation)).Succeeded);
        Assert.True((await adapter.MarkLeadReactivatedAsync(
            new CrmLeadReactivationRequest(tenant, "c", id), cancellation)).Succeeded);
        Assert.True((await adapter.MarkOptOutAsync(new CrmOptOutRequest(tenant, "c", id), cancellation)).Succeeded);
        Assert.True((await adapter.MarkOptOutAsync(
            new CrmOptOutRequest(tenant, "c", id) { Channel = ConsentChannel.Email }, cancellation)).Succeeded);

        var row = await ReadRowAsync(tenant, id);
        var count = CountStoredProperties(row);
        Assert.True(count <= TableStorageMaxProperties, $"Stored row has {count} properties.");
        Assert.True(count >= 200, $"Expected a worst-case row, got {count} properties.");
    }

    [Fact]
    public async Task UpsertContact_WhenAttributesOverflow_LogsOneEventWithCountsOnly()
    {
        var logger = new RecordingEventLogger();
        var adapter = fixture.CreateAdapter(logger);
        var tenant = fixture.NewTenantId("limit");

        var upsert = await adapter.UpsertContactAsync(
            NewRequest(tenant, "+15551240005", "event@example.test", ManyAttributes(300)),
            CancellationToken.None);
        Assert.True(upsert.Succeeded, upsert.Message);

        var logged = Assert.Single(logger.Events, e => e.Name == TelemetryEventNames.CrmContactAttributesOverflowed);
        Assert.Equal(tenant, logged.Properties["tenantId"]);
        Assert.Equal("corr-upsert", logged.Properties["correlationId"]);
        Assert.Equal("true", logged.Properties["contactCreated"]);
        Assert.True(int.Parse(logged.Properties["overflowAttributeCount"]) > 0);
        Assert.Equal(
            logged.Properties["overflowAttributeCount"],
            logged.Properties["newOverflowAttributeCount"]);
        Assert.Equal("0", logged.Properties["droppedAttributeCount"]);
        Assert.Equal("0", logged.Properties["truncatedValueCount"]);
        Assert.True(int.Parse(logged.Properties["attributeColumnCount"]) > 0);
        Assert.True(int.Parse(logged.Properties["overflowJsonLength"]) > 0);
        Assert.DoesNotContain(
            logged.Properties,
            property => property.Value.Contains("extra", StringComparison.Ordinal)
                || property.Value.Contains(upsert.ProviderContactId!, StringComparison.Ordinal)
                || property.Key.Contains("providerContactId", StringComparison.OrdinalIgnoreCase));

        // A later upsert that adds no new overflow does not log again.
        var again = await adapter.UpsertContactAsync(
            NewRequest(
                tenant,
                "+15551240005",
                "event@example.test",
                new Dictionary<string, string> { ["extra299"] = "changed" }),
            CancellationToken.None);
        Assert.True(again.Succeeded, again.Message);
        Assert.Single(logger.Events, e => e.Name == TelemetryEventNames.CrmContactAttributesOverflowed);    }

    [Fact]
    public async Task UpsertContact_WithFewAttributes_LogsNoOverflowEventAndWritesNoOverflowColumn()
    {
        var logger = new RecordingEventLogger();
        var adapter = fixture.CreateAdapter(logger);
        var tenant = fixture.NewTenantId("limit");

        var upsert = await adapter.UpsertContactAsync(
            NewRequest(tenant, "+15551240006", "few@example.test", ManyAttributes(5)),
            CancellationToken.None);
        Assert.True(upsert.Succeeded, upsert.Message);

        Assert.DoesNotContain(logger.Events, e => e.Name == TelemetryEventNames.CrmContactAttributesOverflowed);
        var row = await ReadRowAsync(tenant, upsert.ProviderContactId!);
        Assert.False(row.ContainsKey("AttributesOverflowJson"));
    }

    private async Task<TableEntity> ReadRowAsync(string tenantId, string rowKey)
    {
        var response = await fixture.GetContactsTableClient().GetEntityAsync<TableEntity>(tenantId, rowKey);
        return response.Value;
    }

    /// <summary>
    /// Counts what Azure Table Storage counts: custom properties plus PartitionKey, RowKey, Timestamp.
    /// <see cref="TableEntity"/> also exposes client-side metadata keys (<c>odata.etag</c>,
    /// <c>*@odata.type</c>) that are not stored properties and are excluded.
    /// </summary>
    private static int CountStoredProperties(TableEntity row) =>
        row.Keys.Count(key =>
            !key.StartsWith("odata.", StringComparison.Ordinal)
            && !key.EndsWith("@odata.type", StringComparison.Ordinal));

    private static Dictionary<string, string> ManyAttributes(int count) =>
        Enumerable.Range(0, count).ToDictionary(i => $"extra{i:000}", i => $"v{i}");

    private static CrmContactUpsertRequest NewRequest(
        string tenantId,
        string phone,
        string email,
        IReadOnlyDictionary<string, string> attributes) =>
        new(tenantId, "test-vertical", "corr-upsert", null, phone, email, "Test Lead", "78701", attributes);

    private sealed class RecordingEventLogger : IEventLogger
    {
        public List<(string Name, IReadOnlyDictionary<string, string> Properties)> Events { get; } = [];

        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken)
        {
            Events.Add((eventName, properties));
            return Task.CompletedTask;
        }
    }
}
