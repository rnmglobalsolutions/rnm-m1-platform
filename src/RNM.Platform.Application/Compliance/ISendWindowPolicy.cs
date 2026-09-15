using RNM.Platform.Application.Crm;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Compliance;

public interface ISendWindowPolicy
{
    SendWindowEvaluation Evaluate(
        CrmContactRecord? contact,
        string tenantTimeZone,
        TcpaWindowConfiguration? tcpaWindow,
        DateTimeOffset utcNow);
}

public sealed record SendWindowEvaluation(
    bool IsAllowed,
    string TimeZoneId,
    string TimeZoneBasis,
    int LocalHour,
    DateTimeOffset? NextAllowedAt);
