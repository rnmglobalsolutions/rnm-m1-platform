using Microsoft.Extensions.DependencyInjection;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Infrastructure.Providers;

namespace RNM.Platform.Infrastructure.Crm;

public sealed class ConfiguredCrmAdapter : ICrmAdapter
{
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly IServiceProvider serviceProvider;

    public ConfiguredCrmAdapter(
        ITenantConfigurationProvider tenantConfigurationProvider,
        IServiceProvider serviceProvider)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.serviceProvider = serviceProvider;
    }

    public async Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(
        CrmContactLookupRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? new CrmContactLookupResult(false, null)
            : await adapter.FindContactByPhoneOrEmailAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrmContactUpsertResult> UpsertContactAsync(
        CrmContactUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedUpsert(request.ProviderContactId)
            : await adapter.UpsertContactAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrmOperationResult> AddInteractionNoteAsync(
        CrmInteractionNoteRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedOperation()
            : await adapter.AddInteractionNoteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrmOperationResult> ApplyTagsAsync(
        CrmTagRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedOperation()
            : await adapter.ApplyTagsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrmOperationResult> LinkBookingToContactAsync(
        CrmBookingLinkRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedOperation()
            : await adapter.LinkBookingToContactAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrmOperationResult> AddTimelineEventAsync(
        CrmTimelineEventRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedOperation()
            : await adapter.AddTimelineEventAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrmOperationResult> MarkFollowUpRequiredAsync(
        CrmFollowUpRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedOperation()
            : await adapter.MarkFollowUpRequiredAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrmLeadQueryResult> GetLeadsByStatusAsync(
        CrmLeadQueryRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedLeadQuery()
            : await adapter.GetLeadsByStatusAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrmLeadQueryResult> GetLeadsByCampaignAsync(
        CrmLeadQueryRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedLeadQuery()
            : await adapter.GetLeadsByCampaignAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrmNextLeadToCallResult> GetNextLeadToCallAsync(
        CrmNextLeadToCallRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedNextLead()
            : await adapter.GetNextLeadToCallAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrmOperationResult> RecordOutboundAttemptAsync(
        CrmOutboundAttemptRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedOperation()
            : await adapter.RecordOutboundAttemptAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrmOperationResult> MarkLeadReactivatedAsync(
        CrmLeadReactivationRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedOperation()
            : await adapter.MarkLeadReactivatedAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrmOperationResult> MarkOptOutAsync(
        CrmOptOutRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedOperation()
            : await adapter.MarkOptOutAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ICrmProviderAdapter?> ResolveAsync(string tenantId, CancellationToken cancellationToken)
    {
        var tenantConfiguration = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        var providerName = tenantConfiguration.Providers.CrmProvider.Trim();
        return serviceProvider.GetServices<ICrmProviderAdapter>()
            .FirstOrDefault(adapter => adapter.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase));
    }

    private static CrmContactUpsertResult UnsupportedUpsert(string? providerContactId) =>
        new(false, Created: false, providerContactId, CrmFailureReason.AdapterFailure, "CRM provider is not supported.");

    private static CrmOperationResult UnsupportedOperation() =>
        new(false, CrmFailureReason.AdapterFailure, "CRM provider is not supported.");

    private static CrmLeadQueryResult UnsupportedLeadQuery() =>
        new(false, [], CrmFailureReason.AdapterFailure, "CRM provider is not supported.");

    private static CrmNextLeadToCallResult UnsupportedNextLead() =>
        new(false, null, CrmFailureReason.AdapterFailure, "CRM provider is not supported.");
}

public interface ICrmProviderAdapter : ICrmAdapter, IProviderAdapter;
