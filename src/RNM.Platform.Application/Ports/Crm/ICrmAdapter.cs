using RNM.Platform.Application.Crm;

namespace RNM.Platform.Application.Ports.Crm;

public interface ICrmAdapter
{
    Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(
        CrmContactLookupRequest request,
        CancellationToken cancellationToken);

    Task<CrmContactUpsertResult> UpsertContactAsync(
        CrmContactUpsertRequest request,
        CancellationToken cancellationToken);

    Task<CrmOperationResult> AddInteractionNoteAsync(
        CrmInteractionNoteRequest request,
        CancellationToken cancellationToken);

    Task<CrmOperationResult> ApplyTagsAsync(
        CrmTagRequest request,
        CancellationToken cancellationToken);

    Task<CrmOperationResult> LinkBookingToContactAsync(
        CrmBookingLinkRequest request,
        CancellationToken cancellationToken);

    Task<CrmOperationResult> AddTimelineEventAsync(
        CrmTimelineEventRequest request,
        CancellationToken cancellationToken);

    Task<CrmOperationResult> MarkFollowUpRequiredAsync(
        CrmFollowUpRequest request,
        CancellationToken cancellationToken);

    Task<CrmLeadQueryResult> GetLeadsByStatusAsync(
        CrmLeadQueryRequest request,
        CancellationToken cancellationToken);

    Task<CrmLeadQueryResult> GetLeadsByCampaignAsync(
        CrmLeadQueryRequest request,
        CancellationToken cancellationToken);

    Task<CrmNextLeadToCallResult> GetNextLeadToCallAsync(
        CrmNextLeadToCallRequest request,
        CancellationToken cancellationToken);

    Task<CrmOperationResult> RecordOutboundAttemptAsync(
        CrmOutboundAttemptRequest request,
        CancellationToken cancellationToken);

    Task<CrmOperationResult> MarkLeadReactivatedAsync(
        CrmLeadReactivationRequest request,
        CancellationToken cancellationToken);

    Task<CrmOperationResult> MarkOptOutAsync(
        CrmOptOutRequest request,
        CancellationToken cancellationToken);
}
