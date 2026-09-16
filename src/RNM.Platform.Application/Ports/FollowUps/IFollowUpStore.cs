using RNM.Platform.Application.FollowUps;

namespace RNM.Platform.Application.Ports.FollowUps;

public interface IFollowUpStore
{
    Task ScheduleFollowUpAsync(
        FollowUpDueRecord followUp,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FollowUpDueRecord>> GetDueFollowUpsAsync(
        string tenantId,
        DateTimeOffset dueAt,
        int maxItems,
        CancellationToken cancellationToken);

    Task<bool> TryClaimFollowUpAsync(
        string tenantId,
        string rowKey,
        string correlationId,
        CancellationToken cancellationToken);

    Task MarkFollowUpAsync(
        string tenantId,
        string rowKey,
        string status,
        string correlationId,
        string? reason,
        CancellationToken cancellationToken);

    Task<int> CountSentForContactOnDateAsync(
        string tenantId,
        string providerContactId,
        DateOnly localDate,
        string timeZone,
        CancellationToken cancellationToken);
}
