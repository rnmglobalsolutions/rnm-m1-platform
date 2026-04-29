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
}
