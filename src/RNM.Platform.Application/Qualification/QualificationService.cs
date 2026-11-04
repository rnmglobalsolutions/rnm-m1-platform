using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using RNM.Platform.Application.Observability;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Qualification;

public sealed class QualificationService
{
    private static readonly Regex ZipCodeRegex = new(@"\b\d{5}\b", RegexOptions.Compiled);
    private readonly ServiceAreaValidator serviceAreaValidator;
    private readonly IEventLogger eventLogger;

    public QualificationService(
        ServiceAreaValidator serviceAreaValidator,
        IEventLogger eventLogger)
    {
        this.serviceAreaValidator = serviceAreaValidator;
        this.eventLogger = eventLogger;
    }

    public async Task<QualificationResult> QualifyAsync(
        QualificationRequest request,
        CancellationToken cancellationToken)
    {
        var fields = ExtractFields(request);
        var serviceAreaMode = ResolveMode(request.ServiceAreaMode);
        var stateInput = serviceAreaMode == ServiceAreaModes.State
            ? FindState(request, fields)
            : null;
        if (stateInput is not null && UsStates.TryNormalize(stateInput, out var stateCode))
        {
            // The USPS code is what is stored and reported; the state value is never logged.
            fields["state"] = stateCode;
        }

        var requiredFields = request.RequiredFields
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var fieldStatuses = requiredFields
            .Select(field => new RequiredFieldStatus(field, HasField(fields, field)))
            .ToArray();
        var missingRequiredFields = fieldStatuses
            .Where(field => !field.IsPresent)
            .Select(field => field.FieldName)
            .ToList();

        var zipCode = FindZipCode(request, fields);
        if (serviceAreaMode != ServiceAreaModes.Zip
            && zipCode is not null
            && !ServiceAreaValidator.IsValidUsZipCode(zipCode))
        {
            // A ZIP is optional outside zip mode: keep a valid one, drop an invalid one.
            zipCode = null;
        }

        var serviceAreaDecision = serviceAreaValidator.Validate(
            serviceAreaMode,
            zipCode,
            request.AllowedZipCodes,
            stateInput,
            request.AllowedStates ?? []);
        if (serviceAreaDecision.State is ServiceAreaDecisionState.MissingZipCode)
        {
            missingRequiredFields.Add("zipCode");
        }

        if (serviceAreaDecision.State is ServiceAreaDecisionState.MissingState)
        {
            missingRequiredFields.Add("state");
        }
        var hasInvalidContactFields = HasInvalidContactFields(fields);

        var leadData = new QualifiedLeadData(
            fields,
            zipCode,
            SelectLeadPhoneNumber(fields, request.InboundCallEvent.Session.CallerPhoneNumber));

        var distinctMissingRequiredFields = missingRequiredFields
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var state = serviceAreaDecision.State switch
        {
            ServiceAreaDecisionState.InvalidZipCode => QualificationResultState.InvalidInput,
            ServiceAreaDecisionState.InvalidState => QualificationResultState.InvalidInput,
            _ when hasInvalidContactFields => QualificationResultState.InvalidInput,
            _ when distinctMissingRequiredFields.Length > 0 => QualificationResultState.MissingRequiredFields,
            ServiceAreaDecisionState.InServiceArea => QualificationResultState.Qualified,
            ServiceAreaDecisionState.NotRequired => QualificationResultState.Qualified,
            ServiceAreaDecisionState.OutOfServiceArea => QualificationResultState.OutOfServiceArea,
            ServiceAreaDecisionState.NeedsEscalation => QualificationResultState.NeedsEscalation,
            ServiceAreaDecisionState.MissingZipCode => QualificationResultState.MissingRequiredFields,
            ServiceAreaDecisionState.MissingState => QualificationResultState.MissingRequiredFields,
            _ => QualificationResultState.MissingRequiredFields
        };

        var finalResult = new QualificationResult(
            state,
            leadData,
            fieldStatuses,
            distinctMissingRequiredFields,
            serviceAreaDecision);

        await LogResultAsync(TelemetryEventNames.ServiceAreaValidated, request, finalResult, cancellationToken)
            .ConfigureAwait(false);

        var eventName = state switch
        {
            QualificationResultState.MissingRequiredFields => TelemetryEventNames.QualificationMissingFields,
            QualificationResultState.OutOfServiceArea => TelemetryEventNames.QualificationOutOfServiceArea,
            QualificationResultState.InvalidInput => TelemetryEventNames.QualificationInvalidInput,
            _ => TelemetryEventNames.QualificationCompleted
        };

        await LogResultAsync(eventName, request, finalResult, cancellationToken)
            .ConfigureAwait(false);

        if (eventName != TelemetryEventNames.QualificationCompleted)
        {
            await LogResultAsync(TelemetryEventNames.QualificationCompleted, request, finalResult, cancellationToken)
                .ConfigureAwait(false);
        }

        return finalResult;
    }

    private static string ResolveMode(string? mode)
    {
        return ServiceAreaModes.IsValid(mode) ? mode! : ServiceAreaModes.Zip;
    }

    private static string? FindState(QualificationRequest request, IReadOnlyDictionary<string, string> fields)
    {
        // State comes from a tool argument only; it is never parsed out of an address.
        var aliases = request.ServiceAreaFieldAliases?.StateFields ?? ServiceAreaFieldAliasConfiguration.DefaultStateFields;
        foreach (var fieldName in aliases)
        {
            if (fields.TryGetValue(fieldName, out var state) && !string.IsNullOrWhiteSpace(state))
            {
                return state;
            }
        }

        return null;
    }

    private static Dictionary<string, string> ExtractFields(QualificationRequest request)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (request.StructuredFields is not null)
        {
            foreach (var field in request.StructuredFields)
            {
                AddField(fields, field.Key, field.Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.InboundCallEvent.ActionRequest?.ArgumentsJson))
        {
            AddJsonFields(fields, request.InboundCallEvent.ActionRequest.ArgumentsJson);
        }

        return fields;
    }

    private static void AddJsonFields(IDictionary<string, string> fields, string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });

            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                    _ => null
                };

                AddField(fields, property.Name, value);
            }
        }
        catch (JsonException)
        {
            // Malformed structured fields are ignored; the qualification result will report missing data.
        }
    }

    private static void AddField(IDictionary<string, string> fields, string? name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        fields[name.Trim()] = value.Trim();
    }

    private static bool HasField(IReadOnlyDictionary<string, string> fields, string fieldName)
    {
        return fields.TryGetValue(fieldName, out var value) && !string.IsNullOrWhiteSpace(value);
    }

    private static string? FindZipCode(QualificationRequest request, IReadOnlyDictionary<string, string> fields)
    {
        var aliases = request.ServiceAreaFieldAliases ?? ServiceAreaFieldAliases.Defaults();
        foreach (var fieldName in aliases.ZipCodeFields)
        {
            if (fields.TryGetValue(fieldName, out var zipCode) && !string.IsNullOrWhiteSpace(zipCode))
            {
                return ServiceAreaValidator.NormalizeZipCode(zipCode);
            }
        }

        foreach (var fieldName in aliases.AddressFields)
        {
            if (fields.TryGetValue(fieldName, out var serviceAddress) && !string.IsNullOrWhiteSpace(serviceAddress))
            {
                var match = ZipCodeRegex.Match(serviceAddress);
                if (match.Success)
                {
                    return match.Value;
                }
            }
        }

        return null;
    }

    private static string? SelectLeadPhoneNumber(
        IReadOnlyDictionary<string, string> fields,
        string? sessionPhoneNumber)
    {
        return FirstNonEmpty(
            GetFieldValue(fields, "phoneNumber"),
            GetFieldValue(fields, "phone"),
            GetFieldValue(fields, "callerPhoneNumber"),
            sessionPhoneNumber);
    }

    private static string? GetFieldValue(
        IReadOnlyDictionary<string, string> fields,
        string fieldName)
    {
        return fields.TryGetValue(fieldName, out var value)
            ? value
            : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private static bool HasInvalidContactFields(IReadOnlyDictionary<string, string> fields)
    {
        var email = GetFieldValue(fields, "email");
        if (!string.IsNullOrWhiteSpace(email) && !IsValidEmail(email))
        {
            return true;
        }

        var phoneNumber = FirstNonEmpty(
            GetFieldValue(fields, "phoneNumber"),
            GetFieldValue(fields, "phone"),
            GetFieldValue(fields, "callerPhoneNumber"));
        return !string.IsNullOrWhiteSpace(phoneNumber) && CountDigits(phoneNumber) < 10;
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var parsed = new MailAddress(email);
            return string.Equals(parsed.Address, email.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static int CountDigits(string value)
    {
        return value.Count(char.IsDigit);
    }

    private async Task LogResultAsync(
        string eventName,
        QualificationRequest request,
        QualificationResult result,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", request.CorrelationId)
            .Add("tenantId", request.TenantId)
            .Add("verticalId", request.VerticalId)
            .Add("state", result.State.ToString())
            .Add("serviceAreaState", result.ServiceAreaDecision.State.ToString())
            .Add("serviceAreaMode", ResolveMode(request.ServiceAreaMode))
            .Add("requiredFieldCount", result.RequiredFields.Count.ToString())
            .Add("missingFieldCount", result.MissingRequiredFields.Count.ToString())
            .ToDictionary();

        try
        {
            await eventLogger.LogEventAsync(eventName, properties, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Qualification telemetry is best-effort.
        }
    }
}
