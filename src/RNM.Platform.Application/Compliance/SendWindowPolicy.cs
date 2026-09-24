using RNM.Platform.Application.Crm;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Compliance;

public sealed class SendWindowPolicy : ISendWindowPolicy
{
    public SendWindowEvaluation Evaluate(
        CrmContactRecord? contact,
        string tenantTimeZone,
        TcpaWindowConfiguration? tcpaWindow,
        DateTimeOffset utcNow)
    {
        var timezone = ResolveTimezone(contact, tenantTimeZone);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone.TimeZoneId);
        var window = tcpaWindow ?? new TcpaWindowConfiguration();
        var localTime = TimeZoneInfo.ConvertTime(utcNow, zone);
        var isAllowed = localTime.Hour >= window.EffectiveStartHour
            && localTime.Hour < window.EffectiveEndHour;

        return new SendWindowEvaluation(
            isAllowed,
            timezone.TimeZoneId,
            timezone.Basis,
            localTime.Hour,
            isAllowed ? null : GetNextAllowedAt(localTime, zone, window));
    }

    private static LeadTimezoneResolution ResolveTimezone(
        CrmContactRecord? contact,
        string tenantTimeZone)
    {
        var leadTimezone = contact is null
            ? null
            : FirstNonEmptyAttribute(contact, "timeZone", "timezone", "leadTimeZone", "leadTimezone");
        return string.IsNullOrWhiteSpace(leadTimezone)
            ? new LeadTimezoneResolution(tenantTimeZone, "tenant")
            : IsValidTimeZone(leadTimezone)
                ? new LeadTimezoneResolution(leadTimezone, "lead")
                : new LeadTimezoneResolution(tenantTimeZone, "tenant");
    }

    private static DateTimeOffset GetNextAllowedAt(
        DateTimeOffset localTime,
        TimeZoneInfo zone,
        TcpaWindowConfiguration window)
    {
        var localCandidate = localTime.Hour < window.EffectiveStartHour
            ? localTime.Date.AddHours(window.EffectiveStartHour)
            : localTime.Date.AddDays(1).AddHours(window.EffectiveStartHour);

        while (zone.IsInvalidTime(localCandidate))
        {
            localCandidate = localCandidate.AddMinutes(30);
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localCandidate, DateTimeKind.Unspecified),
            zone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static string? FirstNonEmptyAttribute(CrmContactRecord contact, params string[] names)
    {
        foreach (var name in names)
        {
            if (contact.Attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsValidTimeZone(string timeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private sealed record LeadTimezoneResolution(
        string TimeZoneId,
        string Basis);
}
