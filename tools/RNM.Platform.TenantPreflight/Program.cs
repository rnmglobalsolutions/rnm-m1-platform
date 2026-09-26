using System.Net.Mail;
using System.Text.Json;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.LeadIntake;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Infrastructure.Configuration;
using RNM.Platform.Infrastructure.Providers;

return await TenantPreflightProgram.RunAsync(args).ConfigureAwait(false);

internal static class TenantPreflightProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        PreflightOptions options;
        try
        {
            options = PreflightOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteUsage(Console.Error);
            return 2;
        }

        if (options.ShowHelp)
        {
            WriteUsage(Console.Out);
            return 0;
        }

        var configRoot = Path.GetFullPath(options.ConfigRoot);
        var tenantDirectory = Path.Combine(configRoot, "tenants");
        if (!Directory.Exists(tenantDirectory))
        {
            Console.Error.WriteLine($"Tenant configuration directory was not found: {tenantDirectory}");
            return 2;
        }

        var tenantIds = options.All
            ? Directory.EnumerateFiles(tenantDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray()
            : [options.TenantId!];

        var validator = new ConfigurationValidator();
        var tenantProvider = new JsonTenantConfigurationProvider(configRoot, validator);
        var verticalProvider = new JsonVerticalConfigurationProvider(configRoot, validator);
        var reports = new List<TenantPreflightReport>();

        foreach (var tenantId in tenantIds)
        {
            reports.Add(await EvaluateAsync(
                    tenantId,
                    options.Environment,
                    tenantProvider,
                    verticalProvider)
                .ConfigureAwait(false));
        }

        var response = new TenantPreflightResponse(
            reports.Any(report => report.Status == PreflightStatus.Blocked)
                ? PreflightStatus.Blocked
                : reports.Any(report => report.Status == PreflightStatus.Warning)
                    ? PreflightStatus.Warning
                    : PreflightStatus.Valid,
            options.Environment,
            configRoot,
            reports,
            "This preflight validates checked-in configuration only. After deployment, call the tenant readiness endpoint to verify Key Vault and runtime dependencies.");

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        }
        else
        {
            WriteText(response);
        }

        return response.Status == PreflightStatus.Blocked ? 1 : 0;
    }

    private static async Task<TenantPreflightReport> EvaluateAsync(
        string tenantId,
        string environment,
        ITenantConfigurationProvider tenantProvider,
        IVerticalConfigurationProvider verticalProvider)
    {
        var checks = new List<PreflightCheck>();
        TenantConfiguration tenant;

        try
        {
            tenant = await tenantProvider
                .GetTenantConfigurationAsync(tenantId, CancellationToken.None)
                .ConfigureAwait(false);
            Add(checks, "tenantConfiguration", true, "Tenant JSON loaded and passed domain validation.");
        }
        catch (Exception exception) when (exception is ConfigurationException or JsonException or IOException)
        {
            Add(checks, "tenantConfiguration", false, SafeConfigurationError(exception));
            return CreateReport(tenantId, null, checks, [], []);
        }

        try
        {
            var vertical = await verticalProvider
                .GetVerticalConfigurationAsync(tenant.VerticalId.Value, CancellationToken.None)
                .ConfigureAwait(false);
            var matches = string.Equals(
                vertical.VerticalId.Value,
                tenant.VerticalId.Value,
                StringComparison.Ordinal);
            Add(
                checks,
                "verticalConfiguration",
                matches,
                matches
                    ? "Vertical JSON loaded and passed domain validation."
                    : "Vertical id does not match the referenced configuration filename.");

            var classification = LeadClassificationPolicy.Resolve(vertical.LeadClassification, tenant.LeadClassification);
            Add(
                checks,
                "leadClassification",
                classification.IsValid,
                classification.IsValid
                    ? $"Effective lead classification is valid ({classification.Policy.Funnels.Count} funnel(s))."
                    : string.Join(" ", classification.Errors));

            var routingErrors = LeadClassificationPolicy.ValidateRouting(classification.Policy, tenant);
            Add(
                checks,
                "leadClassification.routes",
                routingErrors.Count == 0,
                routingErrors.Count == 0
                    ? $"Every reachable route is actionable: {string.Join(", ", LeadClassificationPolicy.ReachableRoutes(classification.Policy))}."
                    : string.Join(" ", routingErrors));
        }
        catch (Exception exception) when (exception is ConfigurationException or JsonException or IOException)
        {
            Add(checks, "verticalConfiguration", false, SafeConfigurationError(exception));
        }

        AddSupportedProviderChecks(checks, tenant);
        AddProductionDataChecks(checks, tenant, environment);

        var requiredSecrets = GetRequiredSecrets(tenant);
        var appSettings = GetRequiredAppSettings(tenant);
        return CreateReport(
            tenant.TenantId.Value,
            tenant.VerticalId.Value,
            checks,
            requiredSecrets,
            appSettings);
    }

    private static void AddSupportedProviderChecks(
        ICollection<PreflightCheck> checks,
        TenantConfiguration tenant)
    {
        Add(
            checks,
            "crmProvider",
            IsOneOf(tenant.Providers.CrmProvider, ProviderNames.AzureTable, ProviderNames.GoHighLevel),
            $"CRM provider: {tenant.Providers.CrmProvider}.");
        Add(
            checks,
            "bookingProvider",
            IsOneOf(tenant.Providers.BookingProvider, ProviderNames.GoogleCalendar, ProviderNames.GoHighLevelCalendar),
            $"Booking provider: {tenant.Providers.BookingProvider}.");
        Add(
            checks,
            "smsProvider",
            IsOneOf(tenant.Providers.SmsProvider, ProviderNames.Twilio),
            $"SMS provider: {tenant.Providers.SmsProvider}.");
        Add(
            checks,
            "emailProvider",
            IsOneOf(tenant.Providers.EmailProvider, ProviderNames.SendGrid),
            $"Email provider: {tenant.Providers.EmailProvider}.");
    }

    private static void AddProductionDataChecks(
        ICollection<PreflightCheck> checks,
        TenantConfiguration tenant,
        string environment)
    {
        var production = string.Equals(environment, "production", StringComparison.OrdinalIgnoreCase);
        var operationalSeverity = production ? PreflightSeverity.Required : PreflightSeverity.Warning;

        var wildcardServiceArea = tenant.ServiceArea.ZipCodes.Any(
            zipCode => string.Equals(zipCode?.Trim(), "*", StringComparison.Ordinal));
        Add(
            checks,
            "serviceArea.productionSafe",
            !wildcardServiceArea,
            wildcardServiceArea
                ? "Wildcard service area is not allowed for a live tenant."
                : "Service area is explicit.",
            operationalSeverity);

        AddPhoneCheck(
            checks,
            "communication.smsFromPhoneNumber",
            tenant.Communication.SmsFromPhoneNumber,
            required: true,
            operationalSeverity);
        AddPhoneCheck(
            checks,
            "communication.businessNotificationPhoneNumber",
            tenant.Communication.BusinessNotificationPhoneNumber,
            required: false,
            operationalSeverity);

        AddEmailCheck(
            checks,
            "communication.emailFromAddress",
            tenant.Communication.EmailFromAddress,
            required: !string.IsNullOrWhiteSpace(tenant.Communication.ConfirmationTemplates.EmailBodyTemplate),
            operationalSeverity);
        AddEmailCheck(
            checks,
            "communication.businessNotificationEmail",
            tenant.Communication.BusinessNotificationEmail,
            required: false,
            operationalSeverity);

        var placeholderSecretNames = GetRequiredSecrets(tenant)
            .Where(secret => LooksLikePlaceholder(secret.SecretName))
            .Select(secret => secret.LogicalName)
            .ToArray();
        Add(
            checks,
            "secretNames.productionSafe",
            placeholderSecretNames.Length == 0,
            placeholderSecretNames.Length == 0
                ? "Required secret names do not contain placeholders."
                : $"Replace placeholder secret names: {string.Join(", ", placeholderSecretNames)}.",
            operationalSeverity);
    }

    private static void AddPhoneCheck(
        ICollection<PreflightCheck> checks,
        string name,
        string? phoneNumber,
        bool required,
        string severity)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            Add(
                checks,
                name,
                !required,
                required ? "Phone number is required." : "Optional phone number is not configured.",
                required ? severity : PreflightSeverity.Warning);
            return;
        }

        var valid = IsE164(phoneNumber) && !LooksLikePlaceholder(phoneNumber);
        Add(
            checks,
            name,
            valid,
            valid ? "Phone number is valid E.164." : "Replace the placeholder with a valid E.164 phone number.",
            severity);
    }

    private static void AddEmailCheck(
        ICollection<PreflightCheck> checks,
        string name,
        string? email,
        bool required,
        string severity)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            Add(
                checks,
                name,
                !required,
                required ? "Email address is required." : "Optional email address is not configured.",
                required ? severity : PreflightSeverity.Warning);
            return;
        }

        var valid = MailAddress.TryCreate(email, out _) && !LooksLikePlaceholder(email);
        Add(
            checks,
            name,
            valid,
            valid ? "Email address is syntactically valid." : "Replace the placeholder with a valid email address.",
            severity);
    }

    private static IReadOnlyCollection<RequiredSecret> GetRequiredSecrets(TenantConfiguration tenant)
    {
        var secrets = new List<RequiredSecret>
        {
            new("voiceWebhookSecret", tenant.SecretNames.VoiceWebhookSecret)
        };

        if (IsOneOf(tenant.Providers.SmsProvider, ProviderNames.Twilio))
        {
            secrets.Add(new RequiredSecret("twilioAccountSid", tenant.SecretNames.TwilioAccountSid));
            secrets.Add(new RequiredSecret("twilioAuthToken", tenant.SecretNames.TwilioAuthToken));
        }

        if (IsOneOf(
                tenant.Providers.BookingProvider,
                ProviderNames.GoogleCalendar,
                ProviderNames.GoHighLevelCalendar))
        {
            secrets.Add(new RequiredSecret(
                "bookingCredentials",
                tenant.SecretNames.BookingCredentials ?? tenant.SecretNames.BookingApiKey));
        }

        if (!IsOneOf(tenant.Providers.CrmProvider, ProviderNames.AzureTable))
        {
            secrets.Add(new RequiredSecret(
                "crmCredentials",
                tenant.SecretNames.CrmCredentials ?? tenant.SecretNames.CrmApiKey));
        }

        if (!string.IsNullOrWhiteSpace(tenant.Voice?.Outbound?.VapiApiKeySecretName))
        {
            secrets.Add(new RequiredSecret("vapiOutboundApiKey", tenant.Voice.Outbound.VapiApiKeySecretName!));
        }

        if (tenant.Integrations?.ManyChat?.EffectiveEnabled is true)
        {
            secrets.Add(new RequiredSecret("manyChatWebhookSecret", tenant.SecretNames.ManyChatWebhookSecret!));
        }


        if (tenant.Classes is not null)
        {
            secrets.Add(new RequiredSecret("classRegistrationWebhookSecret", tenant.SecretNames.ClassRegistrationWebhookSecret!));
        }

        return secrets
            .Where(secret => !string.IsNullOrWhiteSpace(secret.SecretName))
            .DistinctBy(secret => secret.SecretName, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyCollection<RequiredAppSetting> GetRequiredAppSettings(TenantConfiguration tenant)
    {
        var settings = new List<RequiredAppSetting>
        {
            new("AzureWebJobsStorage", "Platform storage connection/reference must be configured."),
            new("RNM_KEY_VAULT_URI", "Must point to the environment Key Vault."),
            new("RNM_INTERNAL_API_KEY_SECRET_NAME", "Protected operational endpoints require the internal API key reference.")
        };

        if (IsOneOf(tenant.Providers.EmailProvider, ProviderNames.SendGrid))
        {
            settings.Add(new RequiredAppSetting(
                "SENDGRID_API_KEY",
                "Must resolve from the environment-specific SendGrid Key Vault secret."));
        }

        if (NeedsScheduledAutomation(tenant))
        {
            settings.Add(new RequiredAppSetting(
                "RNM_ACTIVE_TENANTS",
                $"Comma-separated value must include '{tenant.TenantId.Value}'."));
        }

        return settings;
    }

    private static bool NeedsScheduledAutomation(TenantConfiguration tenant) =>
        tenant.Classes is not null
        || tenant.Communication.AppointmentReminders is not null
        || tenant.FollowUps?.EffectiveEnabled is true;

    private static TenantPreflightReport CreateReport(
        string tenantId,
        string? verticalId,
        IReadOnlyCollection<PreflightCheck> checks,
        IReadOnlyCollection<RequiredSecret> requiredSecrets,
        IReadOnlyCollection<RequiredAppSetting> requiredAppSettings)
    {
        var blocked = checks.Any(check =>
            !check.Passed
            && string.Equals(check.Severity, PreflightSeverity.Required, StringComparison.Ordinal));
        var warning = checks.Any(check => !check.Passed);
        return new TenantPreflightReport(
            tenantId,
            verticalId,
            blocked ? PreflightStatus.Blocked : warning ? PreflightStatus.Warning : PreflightStatus.Valid,
            checks,
            requiredSecrets,
            requiredAppSettings,
            $"/api/tenants/{tenantId}/readiness");
    }

    private static void Add(
        ICollection<PreflightCheck> checks,
        string name,
        bool passed,
        string detail,
        string severity = PreflightSeverity.Required) =>
        checks.Add(new PreflightCheck(name, passed, severity, detail));

    private static bool IsOneOf(string? value, params string[] expected) =>
        expected.Any(candidate => string.Equals(value?.Trim(), candidate, StringComparison.OrdinalIgnoreCase));

    private static bool IsE164(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length is >= 9 and <= 16
            && trimmed[0] == '+'
            && trimmed[1] is >= '1' and <= '9'
            && trimmed[2..].All(char.IsDigit);
    }

    private static bool LooksLikePlaceholder(string value)
    {
        var normalized = value.Trim();
        return normalized.Contains("XXXXXXXX", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("REPLACE_ME", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("TODO", StringComparison.OrdinalIgnoreCase)
            || (normalized.StartsWith('<') && normalized.EndsWith('>'));
    }

    private static string SafeConfigurationError(Exception exception) => exception switch
    {
        ConfigurationException => exception.Message,
        JsonException => "Configuration contains invalid JSON.",
        IOException => "Configuration could not be read.",
        _ => "Configuration validation failed."
    };

    private static void WriteText(TenantPreflightResponse response)
    {
        Console.WriteLine($"Tenant preflight: {response.Status}");
        Console.WriteLine($"Environment mode: {response.Environment}");
        Console.WriteLine();

        foreach (var report in response.Tenants)
        {
            Console.WriteLine($"{report.TenantId} ({report.VerticalId ?? "unknown vertical"}): {report.Status}");
            foreach (var check in report.Checks)
            {
                var marker = check.Passed ? "PASS" : check.Severity == PreflightSeverity.Required ? "BLOCK" : "WARN";
                Console.WriteLine($"  [{marker}] {check.Name}: {check.Detail}");
            }

            Console.WriteLine("  Required Key Vault secrets:");
            foreach (var secret in report.RequiredSecrets)
            {
                Console.WriteLine($"    - {secret.LogicalName}: {secret.SecretName}");
            }

            Console.WriteLine("  Required Function App settings:");
            foreach (var setting in report.RequiredAppSettings)
            {
                Console.WriteLine($"    - {setting.Name}: {setting.Requirement}");
            }

            Console.WriteLine($"  Runtime verification: {report.ReadinessPath}");
            Console.WriteLine();
        }

        Console.WriteLine(response.Note);
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Tenant configuration preflight");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  dotnet run --project tools/RNM.Platform.TenantPreflight -- --tenant <tenantId> [options]");
        writer.WriteLine("  dotnet run --project tools/RNM.Platform.TenantPreflight -- --all [options]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --config-root <path>             Configuration root (default: ./config)");
        writer.WriteLine("  --environment repository|production");
        writer.WriteLine("                                   Production blocks placeholders/wildcards (default: production)");
        writer.WriteLine("  --json                           Emit machine-readable JSON");
        writer.WriteLine("  --help                           Show help");
    }
}

internal sealed record PreflightOptions(
    string ConfigRoot,
    string Environment,
    string? TenantId,
    bool All,
    bool Json,
    bool ShowHelp)
{
    public static PreflightOptions Parse(IReadOnlyList<string> args)
    {
        var configRoot = "config";
        var environment = "production";
        string? tenantId = null;
        var all = false;
        var json = false;
        var help = false;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--config-root":
                    configRoot = NextValue(args, ref index, "--config-root");
                    break;
                case "--environment":
                    environment = NextValue(args, ref index, "--environment");
                    if (!string.Equals(environment, "repository", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(environment, "production", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("--environment must be 'repository' or 'production'.");
                    }

                    environment = environment.ToLowerInvariant();
                    break;
                case "--tenant":
                    tenantId = NextValue(args, ref index, "--tenant");
                    break;
                case "--all":
                    all = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--help" or "-h":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }

        if (!help && all == !string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("Specify exactly one of --tenant or --all.");
        }

        return new PreflightOptions(configRoot, environment, tenantId, all, json, help);
    }

    private static string NextValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }
}

internal static class PreflightStatus
{
    public const string Valid = "valid";
    public const string Warning = "warning";
    public const string Blocked = "blocked";
}

internal static class PreflightSeverity
{
    public const string Required = "required";
    public const string Warning = "warning";
}

internal sealed record TenantPreflightResponse(
    string Status,
    string Environment,
    string ConfigRoot,
    IReadOnlyCollection<TenantPreflightReport> Tenants,
    string Note);

internal sealed record TenantPreflightReport(
    string TenantId,
    string? VerticalId,
    string Status,
    IReadOnlyCollection<PreflightCheck> Checks,
    IReadOnlyCollection<RequiredSecret> RequiredSecrets,
    IReadOnlyCollection<RequiredAppSetting> RequiredAppSettings,
    string ReadinessPath);

internal sealed record PreflightCheck(
    string Name,
    bool Passed,
    string Severity,
    string Detail);

internal sealed record RequiredSecret(string LogicalName, string SecretName);

internal sealed record RequiredAppSetting(string Name, string Requirement);
