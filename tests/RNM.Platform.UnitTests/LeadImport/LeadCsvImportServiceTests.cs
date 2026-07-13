using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.LeadImport;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.LeadImport;

public sealed class LeadCsvImportServiceTests
{
    [Fact]
    public async Task ImportAsync_CreatesValidLeadWithCampaignAndNewStatus()
    {
        var crm = new InMemoryCrmAdapter();
        var service = CreateService(crm);

        var result = await service.ImportAsync(
            CreateRequest("firstName,lastName,phone,email,leadSource,intent,targetPropertyAddress,assignedAgent,estimatedValue,timeZone,consentStatus\nJane,Seller,3052445176,jane@example.com,zillow,seller,\"123 Main St\",Alex,450000,America/New_York,opt_in"),
            CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Skipped);
        var contact = Assert.Single(crm.Contacts["tenant-a"].Values);
        Assert.Equal("+13052445176", contact.PhoneNumber);
        Assert.Equal("Jane Seller", contact.Name);
        Assert.Equal("campaign-a", contact.Attributes[CrmContactAttributeNames.CampaignId]);
        Assert.Equal(CrmOutboundLeadStatuses.New, contact.Attributes[CrmContactAttributeNames.LeadStatus]);
        Assert.Equal(CrmConsentStatuses.OptIn, contact.Attributes[CrmContactAttributeNames.ConsentStatus]);
        Assert.Equal("seller", contact.Attributes[CrmContactAttributeNames.Intent]);
        Assert.Equal("America/New_York", contact.Attributes["timeZone"]);
        Assert.Single(crm.TimelineEvents);
        Assert.Equal(CrmTimelineEventTypes.LeadImported, crm.TimelineEvents.Single().EventType);
    }

    [Fact]
    public async Task ImportAsync_DuplicatePhoneUpdatesExistingContact()
    {
        var crm = new InMemoryCrmAdapter();
        var service = CreateService(crm);
        await service.ImportAsync(
            CreateRequest("firstName,lastName,phone,consentStatus\nJane,Seller,3052445176,unknown"),
            CancellationToken.None);

        var result = await service.ImportAsync(
            CreateRequest("firstName,lastName,phone,email,leadSource,consentStatus\nJane,Seller,305-244-5176,jane@example.com,zillow,opt_in"),
            CancellationToken.None);

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Single(crm.Contacts["tenant-a"]);
        var contact = crm.Contacts["tenant-a"].Values.Single();
        Assert.Equal("jane@example.com", contact.Email);
        Assert.Equal("zillow", contact.Attributes[CrmContactAttributeNames.LeadSource]);
    }

    [Fact]
    public async Task ImportAsync_MissingConsentImportsAsUnknown()
    {
        var crm = new InMemoryCrmAdapter();
        var service = CreateService(crm);

        var result = await service.ImportAsync(
            CreateRequest("firstName,lastName,phone\nJane,Seller,3052445176"),
            CancellationToken.None);

        Assert.Equal(1, result.ConsentBreakdown.Unknown);
        var contact = crm.Contacts["tenant-a"].Values.Single();
        Assert.Equal(CrmConsentStatuses.Unknown, contact.Attributes[CrmContactAttributeNames.ConsentStatus]);
    }

    [Fact]
    public async Task ImportAsync_OptedOutLeadIsImportedNotDropped()
    {
        var crm = new InMemoryCrmAdapter();
        var service = CreateService(crm);

        var result = await service.ImportAsync(
            CreateRequest("firstName,lastName,phone,consentStatus\nJane,Seller,3052445176,opted_out"),
            CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.ConsentBreakdown.OptedOut);
        var contact = crm.Contacts["tenant-a"].Values.Single();
        Assert.Equal(CrmConsentStatuses.OptedOut, contact.Attributes[CrmContactAttributeNames.ConsentStatus]);
    }

    [Fact]
    public async Task ImportAsync_ExistingOptedOutIsNotDowngradedToOptIn()
    {
        var crm = new InMemoryCrmAdapter();
        var service = CreateService(crm);
        await service.ImportAsync(
            CreateRequest("firstName,lastName,phone,consentStatus\nJane,Seller,3052445176,opted_out"),
            CancellationToken.None);

        var result = await service.ImportAsync(
            CreateRequest("firstName,lastName,phone,consentStatus\nJane,Seller,3052445176,opt_in"),
            CancellationToken.None);

        Assert.Equal(0, result.ConsentBreakdown.OptIn);
        Assert.Equal(1, result.ConsentBreakdown.OptedOut);
        var contact = crm.Contacts["tenant-a"].Values.Single();
        Assert.Equal(CrmConsentStatuses.OptedOut, contact.Attributes[CrmContactAttributeNames.ConsentStatus]);
    }

    [Fact]
    public async Task ImportAsync_InvalidRowIsSkippedAndImportContinues()
    {
        var crm = new InMemoryCrmAdapter();
        var service = CreateService(crm);

        var result = await service.ImportAsync(
            CreateRequest("firstName,lastName,phone,consentStatus\nJane,Seller,,opt_in\nBob,Buyer,3055550100,unknown"),
            CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.Contains(result.Errors, error => error.RowNumber == 2 && error.Reason == "phone is required");
    }

    [Theory]
    [InlineData("3052445176", "+13052445176")]
    [InlineData("1 (305) 244-5176", "+13052445176")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    public void TryNormalizeToE164_NormalizesSupportedPhoneFormats(string input, string expected)
    {
        var valid = LeadPhoneNormalizer.TryNormalizeToE164(input, out var normalized);

        Assert.True(valid);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public async Task ImportAsync_IsTenantScoped()
    {
        var crm = new InMemoryCrmAdapter();
        var service = CreateService(crm);

        await service.ImportAsync(
            CreateRequest("firstName,lastName,phone,consentStatus\nJane,Seller,3052445176,opt_in", tenantId: "tenant-a"),
            CancellationToken.None);
        await service.ImportAsync(
            CreateRequest("firstName,lastName,phone,consentStatus\nJane,Seller,3052445176,opt_in", tenantId: "tenant-b"),
            CancellationToken.None);

        Assert.Single(crm.Contacts["tenant-a"]);
        Assert.Single(crm.Contacts["tenant-b"]);
    }

    private static LeadCsvImportService CreateService(InMemoryCrmAdapter crm)
    {
        return new LeadCsvImportService(
            new StubTenantConfigurationProvider(),
            crm,
            new StubEventLogger());
    }

    private static LeadCsvImportRequest CreateRequest(string csv, string tenantId = "tenant-a") =>
        new(tenantId, "campaign-a", "corr-1", csv);

    private sealed class StubTenantConfigurationProvider : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TenantConfiguration(
                new TenantId(tenantId),
                new VerticalId("real-estate"),
                "Tenant",
                "America/Chicago",
                new ServiceAreaConfiguration(["*"], [], null),
                new ProviderConfiguration("AzureTable", "GoogleCalendar", "Twilio", "SendGrid"),
                new SecretNameConfiguration("crm", "booking", "voice", "sid", "token", "email"),
                new CommunicationConfiguration("+15550001000", null, new ConfirmationTemplateConfiguration("sms"))));
        }
    }

    private sealed class StubEventLogger : IEventLogger
    {
        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class InMemoryCrmAdapter : ICrmAdapter
    {
        public Dictionary<string, Dictionary<string, CrmContactRecord>> Contacts { get; } = new(StringComparer.Ordinal);

        public List<CrmTimelineEventRequest> TimelineEvents { get; } = [];

        public Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(
            CrmContactLookupRequest request,
            CancellationToken cancellationToken)
        {
            if (!Contacts.TryGetValue(request.TenantId, out var tenantContacts))
            {
                return Task.FromResult(new CrmContactLookupResult(false, null));
            }

            var contact = tenantContacts.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.PhoneNumber, request.PhoneNumber, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(request.Email)
                    && string.Equals(candidate.Email, request.Email, StringComparison.OrdinalIgnoreCase)));
            return contact is null
                ? Task.FromResult(new CrmContactLookupResult(false, null))
                : Task.FromResult(new CrmContactLookupResult(true, contact.ProviderContactId)
                {
                    Contact = contact
                });
        }

        public Task<CrmContactUpsertResult> UpsertContactAsync(
            CrmContactUpsertRequest request,
            CancellationToken cancellationToken)
        {
            if (!Contacts.TryGetValue(request.TenantId, out var tenantContacts))
            {
                tenantContacts = new Dictionary<string, CrmContactRecord>(StringComparer.Ordinal);
                Contacts[request.TenantId] = tenantContacts;
            }

            var providerContactId = request.ProviderContactId ?? $"contact-{tenantContacts.Count + 1}";
            var created = !tenantContacts.ContainsKey(providerContactId);
            var existing = created ? null : tenantContacts[providerContactId];
            var attributes = existing is null
                ? new Dictionary<string, string>(request.Attributes, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(existing.Attributes, StringComparer.OrdinalIgnoreCase);
            foreach (var attribute in request.Attributes)
            {
                attributes[attribute.Key] = string.Equals(attribute.Key, CrmContactAttributeNames.ConsentStatus, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing?.ConsentStatus, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(attribute.Value, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase)
                        ? CrmConsentStatuses.OptedOut
                        : attribute.Value;
            }

            tenantContacts[providerContactId] = new CrmContactRecord(
                request.TenantId,
                providerContactId,
                request.PhoneNumber ?? existing?.PhoneNumber,
                request.Email ?? existing?.Email,
                request.Name ?? existing?.Name,
                request.ZipCode ?? existing?.ZipCode,
                attributes);

            return Task.FromResult(new CrmContactUpsertResult(true, created, providerContactId));
        }

        public Task<CrmOperationResult> AddTimelineEventAsync(CrmTimelineEventRequest request, CancellationToken cancellationToken)
        {
            TimelineEvents.Add(request);
            return Task.FromResult(new CrmOperationResult(true));
        }

        public Task<CrmOperationResult> AddInteractionNoteAsync(CrmInteractionNoteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> ApplyTagsAsync(CrmTagRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> LinkBookingToContactAsync(CrmBookingLinkRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkFollowUpRequiredAsync(CrmFollowUpRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmLeadQueryResult> GetLeadsByStatusAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmLeadQueryResult> GetLeadsByCampaignAsync(CrmLeadQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmNextLeadToCallResult> GetNextLeadToCallAsync(CrmNextLeadToCallRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> RecordOutboundAttemptAsync(CrmOutboundAttemptRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkLeadReactivatedAsync(CrmLeadReactivationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CrmOperationResult> MarkOptOutAsync(CrmOptOutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
