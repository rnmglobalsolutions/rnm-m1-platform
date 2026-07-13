using RNM.Platform.Application.Crm;

namespace RNM.Platform.Application.Ports.Crm;

public interface IContactPhoneIndexBackfillAdapter
{
    Task<CrmContactPhoneIndexBackfillResult> BackfillPhoneIndexAsync(
        CrmContactPhoneIndexBackfillRequest request,
        CancellationToken cancellationToken);
}
