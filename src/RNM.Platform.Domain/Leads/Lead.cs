namespace RNM.Platform.Domain.Leads;

public sealed record Lead(
    string Name,
    string PhoneNumber,
    string? Email,
    string? Address,
    QualificationProfile Qualification);
