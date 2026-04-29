using RNM.Platform.Application.Inbound;

namespace RNM.Platform.Application.Qualification;

public sealed record QualificationRequest(
    string TenantId,
    string VerticalId,
    string CorrelationId,
    InboundCallEvent InboundCallEvent,
    IReadOnlyCollection<string> RequiredFields,
    IReadOnlyCollection<string> AllowedZipCodes,
    IReadOnlyDictionary<string, string>? StructuredFields = null,
    ServiceAreaFieldAliases? ServiceAreaFieldAliases = null);

public sealed record QualificationResult(
    QualificationResultState State,
    QualifiedLeadData LeadData,
    IReadOnlyCollection<RequiredFieldStatus> RequiredFields,
    IReadOnlyCollection<string> MissingRequiredFields,
    ServiceAreaDecision ServiceAreaDecision)
{
    public bool IsQualified => State is QualificationResultState.Qualified;
}

public enum QualificationResultState
{
    Qualified = 0,
    MissingRequiredFields = 1,
    OutOfServiceArea = 2,
    InvalidInput = 3,
    NeedsEscalation = 4
}

public sealed record RequiredFieldStatus(
    string FieldName,
    bool IsPresent);

public sealed record QualifiedLeadData(
    IReadOnlyDictionary<string, string> Fields,
    string? ZipCode,
    string? CallerPhoneNumber);

public sealed record ServiceAreaDecision(
    ServiceAreaDecisionState State)
{
    public static ServiceAreaDecision InServiceArea() => new(ServiceAreaDecisionState.InServiceArea);

    public static ServiceAreaDecision OutOfServiceArea() => new(ServiceAreaDecisionState.OutOfServiceArea);

    public static ServiceAreaDecision MissingZipCode() => new(ServiceAreaDecisionState.MissingZipCode);

    public static ServiceAreaDecision InvalidZipCode() => new(ServiceAreaDecisionState.InvalidZipCode);

    public static ServiceAreaDecision NeedsEscalation() => new(ServiceAreaDecisionState.NeedsEscalation);
}

public enum ServiceAreaDecisionState
{
    InServiceArea = 0,
    OutOfServiceArea = 1,
    MissingZipCode = 2,
    InvalidZipCode = 3,
    NeedsEscalation = 4
}

public sealed record ServiceAreaFieldAliases(
    IReadOnlyCollection<string> ZipCodeFields,
    IReadOnlyCollection<string> AddressFields)
{
    public static ServiceAreaFieldAliases Defaults() =>
        new(
            ["zipCode", "zip", "postalCode", "serviceZipCode"],
            ["serviceAddress"]);
}
