using RNM.Platform.Domain.Tenancy;

namespace RNM.Platform.Domain.Configuration;

public sealed record VerticalConfiguration(
    VerticalId VerticalId,
    string DisplayName,
    IReadOnlyCollection<string> QualificationFields,
    IReadOnlyCollection<string> SupportedCallTypes,
    ServiceAreaFieldAliasConfiguration ServiceAreaFieldAliases,
    LeadClassificationConfiguration? LeadClassification = null,
    VerticalServiceAreaConfiguration? ServiceArea = null);

public sealed record ServiceAreaFieldAliasConfiguration(
    IReadOnlyCollection<string> ZipCodeFields,
    IReadOnlyCollection<string> AddressFields,
    IReadOnlyCollection<string>? StateFields = null)
{
    public static readonly IReadOnlyCollection<string> DefaultStateFields = ["state", "stateCode", "serviceState"];

    public static ServiceAreaFieldAliasConfiguration Defaults() =>
        new(
            ["zipCode", "zip", "postalCode", "serviceZipCode"],
            ["serviceAddress"],
            DefaultStateFields);
}
