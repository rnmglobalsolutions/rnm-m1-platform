namespace RNM.Platform.Application.LeadImport;

public sealed record LeadCsvImportRequest(
    string TenantId,
    string CampaignId,
    string CorrelationId,
    string CsvContent)
{
    public string DefaultLeadSource { get; init; } = "csv_import";
}

public sealed record LeadCsvImportResult(
    string TenantId,
    string CampaignId,
    string CorrelationId,
    int TotalRows,
    int Created,
    int Updated,
    int Skipped,
    LeadCsvConsentBreakdown ConsentBreakdown,
    IReadOnlyCollection<LeadCsvImportError> Errors);

public sealed record LeadCsvConsentBreakdown(
    int OptIn,
    int Unknown,
    int OptedOut);

public sealed record LeadCsvImportError(
    int RowNumber,
    string Reason);

internal sealed record ParsedLeadCsvRow(
    int RowNumber,
    string FirstName,
    string LastName,
    string Phone,
    string? Email,
    string? LeadSource,
    string? Intent,
    string? TargetPropertyAddress,
    string? AssignedAgent,
    string? EstimatedValue,
    string? TimeZone,
    string ConsentStatus);
