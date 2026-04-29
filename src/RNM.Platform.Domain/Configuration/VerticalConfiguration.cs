using RNM.Platform.Domain.Tenancy;

namespace RNM.Platform.Domain.Configuration;

public sealed record VerticalConfiguration(
    VerticalId VerticalId,
    string DisplayName,
    IReadOnlyCollection<string> QualificationFields,
    IReadOnlyCollection<string> SupportedCallTypes,
    ServiceAreaFieldAliasConfiguration ServiceAreaFieldAliases);

public sealed record ServiceAreaFieldAliasConfiguration(
    IReadOnlyCollection<string> ZipCodeFields,
    IReadOnlyCollection<string> AddressFields)
{
    public static ServiceAreaFieldAliasConfiguration Defaults() =>
        new(
            ["zipCode", "zip", "postalCode", "serviceZipCode"],
            ["serviceAddress"]);
}
