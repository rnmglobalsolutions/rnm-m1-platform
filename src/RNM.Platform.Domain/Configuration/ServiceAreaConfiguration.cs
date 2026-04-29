namespace RNM.Platform.Domain.Configuration;

public sealed record ServiceAreaConfiguration(
    IReadOnlyCollection<string> ZipCodes,
    IReadOnlyCollection<string> Cities,
    string? ReferralMessage);
