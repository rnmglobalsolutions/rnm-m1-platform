namespace RNM.Platform.Domain.Leads;

public sealed record QualificationProfile(
    string? ServiceNeed,
    string? PropertyType,
    string? Urgency,
    string? PreferredTime,
    bool IsInServiceArea);
