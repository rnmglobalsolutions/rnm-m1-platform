using System.Globalization;
using System.Text;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.LeadIntake;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.LeadImport;

public sealed class LeadCsvImportService
{
    private const int MaxFieldLength = 512;
    private const int MaxExtraColumns = 25;
    private const int MaxColumnNameLength = 64;

    // Columns with a dedicated meaning; every other valid header is imported as a contact attribute
    // so vertical classification rules can use it (for example timeline or preApproved).
    private static readonly HashSet<string> KnownColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "firstName", "lastName", "phone", "email", "leadSource", "intent", "targetPropertyAddress",
        "assignedAgent", "estimatedValue", "timeZone", "consentStatus", "consentBasis"
    };
    private static readonly HashSet<string> AllowedIntentValues = new(StringComparer.OrdinalIgnoreCase)
    {
        CrmIntentValues.Buyer,
        CrmIntentValues.Seller,
        CrmIntentValues.Renter,
        CrmIntentValues.Unknown
    };

    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ICrmAdapter crmAdapter;
    private readonly LeadClassifier leadClassifier;
    private readonly IEventLogger eventLogger;

    public LeadCsvImportService(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ICrmAdapter crmAdapter,
        LeadClassifier leadClassifier,
        IEventLogger eventLogger)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.crmAdapter = crmAdapter;
        this.leadClassifier = leadClassifier;
        this.eventLogger = eventLogger;
    }

    public async Task<LeadCsvImportResult> ImportAsync(
        LeadCsvImportRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
            .ConfigureAwait(false);
        var parsed = ParseCsv(request.CsvContent);
        // Resolved once per import; each row is then classified in memory.
        var classificationPolicy = await leadClassifier
            .ResolvePolicyAsync(request.TenantId, request.CorrelationId, cancellationToken)
            .ConfigureAwait(false);
        var temperatures = new Dictionary<string, int>(StringComparer.Ordinal);
        var unexpectedValues = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var created = 0;
        var updated = 0;
        var skipped = parsed.Errors.Count;
        var optIn = 0;
        var unknown = 0;
        var optedOut = 0;
        var errors = new List<LeadCsvImportError>(parsed.Errors);

        foreach (var row in parsed.Rows)
        {
            if (!LeadPhoneNormalizer.TryNormalizeToE164(row.Phone, out var normalizedPhone))
            {
                skipped++;
                errors.Add(new LeadCsvImportError(row.RowNumber, "phone must be a valid E.164 or US phone number"));
                continue;
            }

            var lookup = await crmAdapter
                .FindContactByPhoneOrEmailAsync(
                    new CrmContactLookupRequest(
                        request.TenantId,
                        request.CorrelationId,
                        normalizedPhone,
                        row.Email),
                    cancellationToken)
                .ConfigureAwait(false);

            var finalConsent = PreserveStrongestConsent(lookup.Contact?.ConsentStatus, row.ConsentStatus);
            // The import's consentStatus covers outbound calls and SMS; email eligibility stays as the contact has it today.
            var finalSmsConsent = PreserveStrongestConsent(lookup.Contact?.SmsConsentStatus, row.ConsentStatus);
            var emailConsent = lookup.Contact?.EmailConsentStatus ?? CrmConsentStatuses.Unknown;
            var attributes = CreateAttributes(request, row, finalConsent, finalSmsConsent, emailConsent);
            var classification = LeadClassificationPolicy.Evaluate(classificationPolicy, attributes, finalConsent);
            classification.WriteTo(attributes);
            var upsert = await crmAdapter
                .UpsertContactAsync(
                    new CrmContactUpsertRequest(
                        request.TenantId,
                        tenant.VerticalId.Value,
                        request.CorrelationId,
                        lookup.ProviderContactId,
                        normalizedPhone,
                        NormalizeOptional(row.Email),
                        $"{row.FirstName} {row.LastName}".Trim(),
                        ZipCode: null,
                        attributes)
                    {
                        LeadStatus = CrmOutboundLeadStatuses.New,
                        NeedsFollowUp = false,
                        LastInteractionAt = DateTimeOffset.UtcNow
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!upsert.Succeeded || string.IsNullOrWhiteSpace(upsert.ProviderContactId))
            {
                skipped++;
                errors.Add(new LeadCsvImportError(row.RowNumber, upsert.FailureReason?.ToString() ?? "crm_upsert_failed"));
                continue;
            }

            if (upsert.Created)
            {
                created++;
            }
            else
            {
                updated++;
            }

            IncrementConsentBreakdown(finalConsent, ref optIn, ref unknown, ref optedOut);
            temperatures[classification.Tier] = temperatures.GetValueOrDefault(classification.Tier) + 1;
            foreach (var attribute in classification.UnexpectedAttributes)
            {
                unexpectedValues[attribute] = unexpectedValues.GetValueOrDefault(attribute) + 1;
            }

            await AddImportedTimelineEventAsync(request, row, upsert.ProviderContactId, finalConsent, cancellationToken)
                .ConfigureAwait(false);
        }

        var result = new LeadCsvImportResult(
            request.TenantId,
            request.CampaignId,
            request.CorrelationId,
            parsed.TotalRows,
            created,
            updated,
            skipped,
            new LeadCsvConsentBreakdown(optIn, unknown, optedOut),
            errors)
        {
            TemperatureBreakdown = new LeadCsvTemperatureBreakdown(
                temperatures.GetValueOrDefault(LeadTiers.Hot),
                temperatures.GetValueOrDefault(LeadTiers.Warm),
                temperatures.GetValueOrDefault(LeadTiers.Cold),
                temperatures.GetValueOrDefault(LeadTiers.Disqualified)),
            IgnoredColumns = parsed.IgnoredColumns,
            UnexpectedValues = unexpectedValues
        };

        await LogImportCompletedAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static Dictionary<string, string> CreateAttributes(
        LeadCsvImportRequest request,
        ParsedLeadCsvRow row,
        string finalConsent,
        string smsConsent,
        string emailConsent)
    {
        // Extra columns go in first so the platform-managed values below always win.
        var attributes = new Dictionary<string, string>(row.ExtraAttributes, StringComparer.OrdinalIgnoreCase)
        {
            [CrmContactAttributeNames.LeadSource] = NormalizeOptional(row.LeadSource) ?? request.DefaultLeadSource,
            [CrmContactAttributeNames.CampaignId] = request.CampaignId,
            [CrmContactAttributeNames.LeadStatus] = CrmOutboundLeadStatuses.New,
            [CrmContactAttributeNames.OutboundAttemptCount] = "0",
            [CrmContactAttributeNames.Intent] = NormalizeIntent(row.Intent),
            [CrmContactAttributeNames.ConsentStatus] = finalConsent
        };

        AddOptional(attributes, CrmContactAttributeNames.TargetPropertyAddress, row.TargetPropertyAddress);
        AddOptional(attributes, CrmContactAttributeNames.AssignedAgent, row.AssignedAgent);
        AddOptional(attributes, "estimatedValue", row.EstimatedValue);
        AddOptional(attributes, "timeZone", row.TimeZone);
        ChannelConsent.WriteAttributes(attributes, ConsentChannel.Sms, smsConsent, capture: null);
        ChannelConsent.WriteAttributes(attributes, ConsentChannel.Email, emailConsent, capture: null);
        return attributes;
    }

    private static void AddOptional(IDictionary<string, string> attributes, string key, string? value)
    {
        var normalized = NormalizeOptional(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            attributes[key] = normalized;
        }
    }

    private static string NormalizeIntent(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        return normalized is not null && AllowedIntentValues.Contains(normalized)
            ? normalized
            : CrmIntentValues.Unknown;
    }

    private static string NormalizeConsent(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        return normalized switch
        {
            CrmConsentStatuses.OptIn => CrmConsentStatuses.OptIn,
            CrmConsentStatuses.OptedOut => CrmConsentStatuses.OptedOut,
            CrmConsentStatuses.Unknown => CrmConsentStatuses.Unknown,
            _ => CrmConsentStatuses.Unknown
        };
    }

    private static string PreserveStrongestConsent(string? existingConsent, string importedConsent)
    {
        var existing = NormalizeConsent(existingConsent);
        var incoming = NormalizeConsent(importedConsent);
        if (string.Equals(existing, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase))
        {
            return CrmConsentStatuses.OptedOut;
        }

        if (string.Equals(existing, CrmConsentStatuses.OptIn, StringComparison.OrdinalIgnoreCase)
            && string.Equals(incoming, CrmConsentStatuses.Unknown, StringComparison.OrdinalIgnoreCase))
        {
            return CrmConsentStatuses.OptIn;
        }

        return incoming;
    }

    private static void IncrementConsentBreakdown(
        string consentStatus,
        ref int optIn,
        ref int unknown,
        ref int optedOut)
    {
        if (string.Equals(consentStatus, CrmConsentStatuses.OptIn, StringComparison.OrdinalIgnoreCase))
        {
            optIn++;
            return;
        }

        if (string.Equals(consentStatus, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase))
        {
            optedOut++;
            return;
        }

        unknown++;
    }

    private async Task AddImportedTimelineEventAsync(
        LeadCsvImportRequest request,
        ParsedLeadCsvRow row,
        string providerContactId,
        string finalConsent,
        CancellationToken cancellationToken)
    {
        var result = await crmAdapter
            .AddTimelineEventAsync(
                new CrmTimelineEventRequest(
                    request.TenantId,
                    request.CorrelationId,
                    providerContactId,
                    ProviderBookingId: null,
                    CrmTimelineEventTypes.LeadImported,
                    "CsvLeadImport",
                    "Lead imported from CSV.",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["campaignId"] = request.CampaignId,
                        ["leadSource"] = NormalizeOptional(row.LeadSource) ?? request.DefaultLeadSource,
                        ["leadStatus"] = CrmOutboundLeadStatuses.New,
                        ["consentStatus"] = finalConsent
                    }),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            await eventLogger
                .LogEventAsync(
                    TelemetryEventNames.CrmTimelineEventFailed,
                    new SafeTelemetryProperties()
                        .Add("tenantId", request.TenantId)
                        .Add("campaignId", request.CampaignId)
                        .Add("correlationId", request.CorrelationId)
                        .Add("providerContactId", providerContactId)
                        .Add("eventType", CrmTimelineEventTypes.LeadImported)
                        .Add("failureReason", result.FailureReason?.ToString())
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task LogImportCompletedAsync(
        LeadCsvImportResult result,
        CancellationToken cancellationToken)
    {
        return eventLogger.LogEventAsync(
            "lead_import.csv.completed",
            new SafeTelemetryProperties()
                .Add("tenantId", result.TenantId)
                .Add("campaignId", result.CampaignId)
                .Add("correlationId", result.CorrelationId)
                .Add("totalRows", result.TotalRows.ToString(CultureInfo.InvariantCulture))
                .Add("created", result.Created.ToString(CultureInfo.InvariantCulture))
                .Add("updated", result.Updated.ToString(CultureInfo.InvariantCulture))
                .Add("skipped", result.Skipped.ToString(CultureInfo.InvariantCulture))
                .Add("consentOptIn", result.ConsentBreakdown.OptIn.ToString(CultureInfo.InvariantCulture))
                .Add("consentUnknown", result.ConsentBreakdown.Unknown.ToString(CultureInfo.InvariantCulture))
                .Add("consentOptedOut", result.ConsentBreakdown.OptedOut.ToString(CultureInfo.InvariantCulture))
                .Add("temperatureHot", result.TemperatureBreakdown.Hot.ToString(CultureInfo.InvariantCulture))
                .Add("temperatureWarm", result.TemperatureBreakdown.Warm.ToString(CultureInfo.InvariantCulture))
                .Add("temperatureCold", result.TemperatureBreakdown.Cold.ToString(CultureInfo.InvariantCulture))
                .Add("temperatureDisqualified", result.TemperatureBreakdown.Disqualified.ToString(CultureInfo.InvariantCulture))
                .Add("unexpectedValueAttributes", string.Join(",", result.UnexpectedValues.Keys))
                .Add("ignoredColumns", string.Join(",", result.IgnoredColumns))
                .ToDictionary(),
            cancellationToken);
    }

    private static LeadCsvParseResult ParseCsv(string csvContent)
    {
        if (string.IsNullOrWhiteSpace(csvContent))
        {
            return new LeadCsvParseResult(0, [], [new LeadCsvImportError(0, "csv body is empty")], []);
        }

        using var reader = new StringReader(csvContent);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return new LeadCsvParseResult(0, [], [new LeadCsvImportError(0, "csv header is missing")], []);
        }

        var headers = ParseCsvLine(headerLine)
            .Select((name, index) => new { Name = name.Trim(), Index = index })
            .Where(header => !string.IsNullOrWhiteSpace(header.Name))
            .ToDictionary(header => header.Name, header => header.Index, StringComparer.OrdinalIgnoreCase);
        var (extraColumns, ignoredColumns) = SelectExtraColumns(headers.Keys);
        var rows = new List<ParsedLeadCsvRow>();
        var errors = new List<LeadCsvImportError>();
        var rowNumber = 1;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var values = ParseCsvLine(line);
            var firstName = GetValue(headers, values, "firstName");
            var lastName = GetValue(headers, values, "lastName");
            var phone = GetValue(headers, values, "phone");
            if (string.IsNullOrWhiteSpace(firstName))
            {
                errors.Add(new LeadCsvImportError(rowNumber, "firstName is required"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(lastName))
            {
                errors.Add(new LeadCsvImportError(rowNumber, "lastName is required"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(phone))
            {
                errors.Add(new LeadCsvImportError(rowNumber, "phone is required"));
                continue;
            }

            rows.Add(new ParsedLeadCsvRow(
                rowNumber,
                Truncate(firstName)!,
                Truncate(lastName)!,
                Truncate(phone)!,
                Truncate(GetValue(headers, values, "email")),
                Truncate(GetValue(headers, values, "leadSource")),
                Truncate(GetValue(headers, values, "intent")),
                Truncate(GetValue(headers, values, "targetPropertyAddress")),
                Truncate(GetValue(headers, values, "assignedAgent")),
                Truncate(GetValue(headers, values, "estimatedValue")),
                Truncate(GetValue(headers, values, "timeZone")),
                NormalizeConsent(GetValue(headers, values, "consentStatus")),
                ReadExtraAttributes(headers, values, extraColumns)));
        }

        return new LeadCsvParseResult(rowNumber - 1, rows, errors, ignoredColumns);
    }

    private static (IReadOnlyList<string> Extra, IReadOnlyList<string> Ignored) SelectExtraColumns(IEnumerable<string> headers)
    {
        var extra = new List<string>();
        var ignored = new List<string>();
        foreach (var header in headers.Where(header => !KnownColumns.Contains(header)))
        {
            var valid = header.Length <= MaxColumnNameLength
                && header.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
                && !CrmContactAttributeNames.PlatformManaged.Contains(header);
            if (valid && extra.Count < MaxExtraColumns)
            {
                extra.Add(header);
            }
            else
            {
                ignored.Add(header);
            }
        }

        return (extra, ignored);
    }

    private static IReadOnlyDictionary<string, string> ReadExtraAttributes(
        IReadOnlyDictionary<string, int> headers,
        IReadOnlyList<string> values,
        IReadOnlyList<string> extraColumns)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in extraColumns)
        {
            var value = Truncate(GetValue(headers, values, column));
            if (value is not null)
            {
                attributes[column] = value;
            }
        }

        return attributes;
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (character == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        values.Add(current.ToString());
        return values;
    }

    private static string? GetValue(
        IReadOnlyDictionary<string, int> headers,
        IReadOnlyList<string> values,
        string name)
    {
        return headers.TryGetValue(name, out var index) && index < values.Count
            ? NormalizeOptional(values[index])
            : null;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? Truncate(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized is null || normalized.Length <= MaxFieldLength
            ? normalized
            : normalized[..MaxFieldLength];
    }

    private sealed record LeadCsvParseResult(
        int TotalRows,
        IReadOnlyCollection<ParsedLeadCsvRow> Rows,
        IReadOnlyCollection<LeadCsvImportError> Errors,
        IReadOnlyCollection<string> IgnoredColumns);
}

public static class LeadPhoneNormalizer
{
    public static bool TryNormalizeToE164(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var hasPlus = trimmed.StartsWith('+');
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length == 10)
        {
            normalized = $"+1{digits}";
            return true;
        }

        if (digits.Length == 11 && digits.StartsWith('1'))
        {
            normalized = $"+{digits}";
            return true;
        }

        if (hasPlus && digits.Length is >= 8 and <= 15)
        {
            normalized = $"+{digits}";
            return true;
        }

        return false;
    }
}
