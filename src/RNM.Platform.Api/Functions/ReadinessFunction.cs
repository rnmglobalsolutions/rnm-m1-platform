using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Infrastructure.Booking;
using RNM.Platform.Infrastructure.Crm;
using RNM.Platform.Infrastructure.Providers;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Functions;

public sealed class ReadinessFunction
{
    private const string ApiKeyHeaderName = "x-rnm-api-key";
    private const string ActiveTenantsEnvironmentVariable = "RNM_ACTIVE_TENANTS";
    private const string RequiredSeverity = "required";
    private const string WarningSeverity = "warning";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly IVerticalConfigurationProvider verticalConfigurationProvider;
    private readonly ISecretProvider secretProvider;
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly RnmRuntimeConfiguration runtimeConfiguration;
    private readonly IReadOnlyCollection<IBookingProviderAdapter> bookingProviderAdapters;
    private readonly IReadOnlyCollection<ICrmProviderAdapter> crmProviderAdapters;

    public ReadinessFunction(
        ITenantConfigurationProvider tenantConfigurationProvider,
        IVerticalConfigurationProvider verticalConfigurationProvider,
        ISecretProvider secretProvider,
        ApiKeyRequestValidator apiKeyRequestValidator,
        RnmRuntimeConfiguration runtimeConfiguration,
        IEnumerable<IBookingProviderAdapter> bookingProviderAdapters,
        IEnumerable<ICrmProviderAdapter> crmProviderAdapters)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.verticalConfigurationProvider = verticalConfigurationProvider;
        this.secretProvider = secretProvider;
        this.apiKeyRequestValidator = apiKeyRequestValidator;
        this.runtimeConfiguration = runtimeConfiguration;
        this.bookingProviderAdapters = bookingProviderAdapters.ToArray();
        this.crmProviderAdapters = crmProviderAdapters.ToArray();
    }

    [Function("Readiness")]
    public async Task<HttpResponseData> HandleAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = "tenants/{tenantId}/readiness")]
        HttpRequestData request,
        string tenantId,
        CancellationToken cancellationToken) =>
        await HandleCoreAsync(request, tenantId, cancellationToken).ConfigureAwait(false);

    [Function("ReadinessLegacy")]
    public async Task<HttpResponseData> HandleLegacyAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = "tenants/{tenantId}/ready")]
        HttpRequestData request,
        string tenantId,
        CancellationToken cancellationToken) =>
        await HandleCoreAsync(request, tenantId, cancellationToken).ConfigureAwait(false);

    private async Task<HttpResponseData> HandleCoreAsync(
        HttpRequestData request,
        string tenantId,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValues(ApiKeyHeaderName, out var values)
            || !apiKeyRequestValidator.IsValid(values.FirstOrDefault(), runtimeConfiguration.InternalApiKey))
        {
            return WriteResponse(
                request,
                HttpStatusCode.Unauthorized,
                new ReadinessResponse("unauthorized", tenantId, []));
        }

        var checks = new List<ReadinessCheck>();
        try
        {
            var tenant = await tenantConfigurationProvider
                .GetTenantConfigurationAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
            Add(checks, "tenantConfiguration", true, detail: "Tenant configuration loaded.");

            try
            {
                await verticalConfigurationProvider
                    .GetVerticalConfigurationAsync(tenant.VerticalId.Value, cancellationToken)
                    .ConfigureAwait(false);
                Add(checks, "verticalConfiguration", true, detail: "Vertical configuration loaded.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                Add(checks, "verticalConfiguration", false, detail: "Vertical configuration is missing or invalid.");
            }

            Add(checks,
                "storage",
                IsResolvedSetting(Environment.GetEnvironmentVariable("AzureWebJobsStorage"))
                || IsResolvedSetting(Environment.GetEnvironmentVariable("RNM_CRM_TABLE_STORAGE_CONNECTION_STRING")),
                detail: "Azure Table/Queue storage setting is present.");
            Add(checks,
                "bookingProvider",
                SupportsProvider(bookingProviderAdapters, tenant.Providers.BookingProvider),
                detail: $"Booking provider '{tenant.Providers.BookingProvider}' is registered.");
            Add(checks,
                "crmProvider",
                SupportsProvider(crmProviderAdapters, tenant.Providers.CrmProvider),
                detail: $"CRM provider '{tenant.Providers.CrmProvider}' is registered.");
            Add(checks,
                "smsProvider",
                string.Equals(tenant.Providers.SmsProvider, ProviderNames.Twilio, StringComparison.OrdinalIgnoreCase),
                detail: $"SMS provider '{tenant.Providers.SmsProvider}' is supported.");
            Add(checks,
                "emailProvider",
                string.Equals(tenant.Providers.EmailProvider, ProviderNames.SendGrid, StringComparison.OrdinalIgnoreCase),
                detail: $"Email provider '{tenant.Providers.EmailProvider}' is supported.");
            Add(checks,
                "sendGridApiKey",
                !string.Equals(tenant.Providers.EmailProvider, ProviderNames.SendGrid, StringComparison.OrdinalIgnoreCase)
                || IsResolvedSetting(Environment.GetEnvironmentVariable("SENDGRID_API_KEY")),
                detail: "SENDGRID_API_KEY is present when SendGrid is configured.");
            Add(checks,
                "emailConfiguration",
                !string.IsNullOrWhiteSpace(tenant.Communication.EmailFromAddress)
                && !string.IsNullOrWhiteSpace(tenant.Communication.ConfirmationTemplates.EmailSubjectTemplate)
                && !string.IsNullOrWhiteSpace(tenant.Communication.ConfirmationTemplates.EmailBodyTemplate),
                detail: "Customer email sender and confirmation templates are configured.");
            Add(checks,
                "smsConfiguration",
                !string.IsNullOrWhiteSpace(tenant.Communication.SmsFromPhoneNumber)
                && !string.IsNullOrWhiteSpace(tenant.Communication.ConfirmationTemplates.SmsBodyTemplate),
                detail: "Customer SMS sender and confirmation template are configured.");
            Add(checks,
                "businessNotifications",
                !string.IsNullOrWhiteSpace(tenant.Communication.BusinessNotificationEmail)
                || !string.IsNullOrWhiteSpace(tenant.Communication.BusinessNotificationPhoneNumber),
                WarningSeverity,
                "At least one business notification recipient should be configured.");

            var secretResults = await CheckSecretsAsync(tenant, cancellationToken).ConfigureAwait(false);
            checks.AddRange(secretResults);

            AddAutomationChecks(checks, tenant);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            Add(checks, "tenantConfiguration", false, detail: "Tenant configuration could not be loaded.");
        }

        var blocked = checks.Any(check => !check.Ready && string.Equals(check.Severity, RequiredSeverity, StringComparison.OrdinalIgnoreCase));
        var degraded = !blocked && checks.Any(check => !check.Ready);
        var status = blocked ? "blocked" : degraded ? "degraded" : "ready";
        return WriteResponse(
            request,
            blocked ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
            new ReadinessResponse(status, tenantId, checks));
    }

    private async Task<IReadOnlyCollection<ReadinessCheck>> CheckSecretsAsync(
        TenantConfiguration tenant,
        CancellationToken cancellationToken)
    {
        var checks = new List<ReadinessCheck>();
        var secrets = RequiredSecrets(tenant)
            .Where(secret => !string.IsNullOrWhiteSpace(secret.SecretName))
            .DistinctBy(secret => secret.SecretName, StringComparer.Ordinal)
            .ToArray();

        var allSecretsPresent = true;
        foreach (var secret in secrets)
        {
            var secretValue = await TryGetSecretAsync(secret.SecretName!, cancellationToken).ConfigureAwait(false);
            var present = !string.IsNullOrWhiteSpace(secretValue);
            allSecretsPresent &= present;
            Add(
                checks,
                $"secret.{secret.Name}",
                present,
                detail: present ? "Secret is present." : "Secret is missing or unreadable.");

            if (present && string.Equals(secret.Name, "bookingCredentials", StringComparison.OrdinalIgnoreCase))
            {
                AddBookingCredentialShapeCheck(checks, tenant, secretValue!);
            }

            if (present && string.Equals(secret.Name, "crmCredentials", StringComparison.OrdinalIgnoreCase))
            {
                AddCrmCredentialShapeCheck(checks, tenant, secretValue!);
            }
        }

        Add(checks, "providerSecrets", allSecretsPresent, detail: "All required provider secrets are readable.");
        return checks;
    }

    private static IReadOnlyCollection<RequiredSecret> RequiredSecrets(TenantConfiguration tenant)
    {
        var secrets = new List<RequiredSecret>
        {
            new("voiceWebhookSecret", tenant.SecretNames.VoiceWebhookSecret)
        };

        if (string.Equals(tenant.Providers.SmsProvider, ProviderNames.Twilio, StringComparison.OrdinalIgnoreCase))
        {
            secrets.Add(new RequiredSecret("twilioAccountSid", tenant.SecretNames.TwilioAccountSid));
            secrets.Add(new RequiredSecret("twilioAuthToken", tenant.SecretNames.TwilioAuthToken));
        }

        if (string.Equals(tenant.Providers.BookingProvider, ProviderNames.GoogleCalendar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tenant.Providers.BookingProvider, ProviderNames.GoHighLevelCalendar, StringComparison.OrdinalIgnoreCase))
        {
            secrets.Add(new RequiredSecret("bookingCredentials", tenant.SecretNames.BookingCredentials ?? tenant.SecretNames.BookingApiKey));
        }

        if (!string.Equals(tenant.Providers.CrmProvider, ProviderNames.AzureTable, StringComparison.OrdinalIgnoreCase))
        {
            secrets.Add(new RequiredSecret("crmCredentials", tenant.SecretNames.CrmCredentials ?? tenant.SecretNames.CrmApiKey));
        }

        if (!string.IsNullOrWhiteSpace(tenant.Voice?.Outbound?.VapiApiKeySecretName))
        {
            secrets.Add(new RequiredSecret("vapiOutboundApiKey", tenant.Voice.Outbound.VapiApiKeySecretName));
        }

        if (tenant.Integrations?.ManyChat?.EffectiveEnabled is true)
        {
            secrets.Add(new RequiredSecret("manyChatWebhookSecret", tenant.SecretNames.ManyChatWebhookSecret));
        }


        if (tenant.Classes is not null)
        {
            secrets.Add(new RequiredSecret("classRegistrationWebhookSecret", tenant.SecretNames.ClassRegistrationWebhookSecret));
        }

        return secrets;
    }

    private async Task<string?> TryGetSecretAsync(
        string secretName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await secretProvider.GetSecretAsync(secretName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static void AddBookingCredentialShapeCheck(
        ICollection<ReadinessCheck> checks,
        TenantConfiguration tenant,
        string secretValue)
    {
        if (string.Equals(tenant.Providers.BookingProvider, ProviderNames.GoogleCalendar, StringComparison.OrdinalIgnoreCase))
        {
            Add(checks, "bookingCredentialsShape", LooksLikeGoogleCalendarCredentials(secretValue), detail: "Google Calendar credentials include calendarId and usable OAuth token material.");
            return;
        }

        if (string.Equals(tenant.Providers.BookingProvider, ProviderNames.GoHighLevelCalendar, StringComparison.OrdinalIgnoreCase))
        {
            Add(checks, "bookingCredentialsShape", LooksLikeGoHighLevelCredentials(secretValue), detail: "GoHighLevel booking credentials include an access token.");
        }
    }

    private static void AddCrmCredentialShapeCheck(
        ICollection<ReadinessCheck> checks,
        TenantConfiguration tenant,
        string secretValue)
    {
        if (string.Equals(tenant.Providers.CrmProvider, ProviderNames.GoHighLevel, StringComparison.OrdinalIgnoreCase))
        {
            Add(checks, "crmCredentialsShape", LooksLikeGoHighLevelCredentials(secretValue), detail: "GoHighLevel CRM credentials include an access token.");
        }
    }

    private static void AddAutomationChecks(
        ICollection<ReadinessCheck> checks,
        TenantConfiguration tenant)
    {
        var needsTimer =
            tenant.Classes is not null
            || tenant.Communication.AppointmentReminders is not null
            || tenant.FollowUps?.EffectiveEnabled is true;
        Add(
            checks,
            "automationActiveTenant",
            !needsTimer || ActiveTenantsContain(tenant.TenantId.Value),
            needsTimer ? RequiredSeverity : WarningSeverity,
            needsTimer
                ? $"{ActiveTenantsEnvironmentVariable} must include this tenant for reminders/follow-ups."
                : "No timer-based automation is explicitly configured.");

        if (tenant.Classes is not null)
        {
            Add(
                checks,
                "classAutomationConfiguration",
                tenant.Classes.RegistrationTemplates is not null && tenant.Classes.ReminderTemplates is not null,
                detail: "Class registration and reminder templates are configured.");
        }

        if (tenant.Communication.AppointmentReminders is not null)
        {
            Add(
                checks,
                "appointmentReminderConfiguration",
                tenant.Communication.AppointmentReminders.Templates is not null,
                detail: "Appointment reminder templates are configured.");
        }

        if (tenant.FollowUps?.EffectiveEnabled is true)
        {
            Add(
                checks,
                "followUpConfiguration",
                tenant.FollowUps.EffectiveSequences.Count > 0,
                detail: "Follow-up automation has at least one sequence.");
        }
    }

    private static bool SupportsProvider<TAdapter>(
        IEnumerable<TAdapter> adapters,
        string providerName)
        where TAdapter : RNM.Platform.Infrastructure.Providers.IProviderAdapter
    {
        return adapters.Any(adapter =>
            adapter.ProviderName.Equals(providerName?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsResolvedSetting(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !value.TrimStart().StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ActiveTenantsContain(string tenantId)
    {
        var raw = Environment.GetEnvironmentVariable(ActiveTenantsEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(raw)
            && raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(value => string.Equals(value, tenantId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeGoogleCalendarCredentials(string secretValue)
    {
        try
        {
            using var document = JsonDocument.Parse(secretValue);
            var root = document.RootElement;
            var hasCalendar = !string.IsNullOrWhiteSpace(ReadString(root, "calendarId"));
            var hasAccessToken = !string.IsNullOrWhiteSpace(ReadString(root, "accessToken"));
            var hasRefreshFlow =
                !string.IsNullOrWhiteSpace(ReadString(root, "refreshToken"))
                && !string.IsNullOrWhiteSpace(ReadString(root, "clientId"))
                && !string.IsNullOrWhiteSpace(ReadString(root, "clientSecret"));
            return hasCalendar && (hasAccessToken || hasRefreshFlow);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeGoHighLevelCredentials(string secretValue)
    {
        if (string.IsNullOrWhiteSpace(secretValue))
        {
            return false;
        }

        var trimmed = secretValue.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            return !string.IsNullOrWhiteSpace(ReadString(root, "accessToken"))
                || !string.IsNullOrWhiteSpace(ReadString(root, "apiKey"))
                || !string.IsNullOrWhiteSpace(ReadString(root, "token"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static void Add(
        ICollection<ReadinessCheck> checks,
        string name,
        bool ready,
        string severity = RequiredSeverity,
        string? detail = null)
    {
        checks.Add(new ReadinessCheck(name, ready, severity, detail));
    }

    private static HttpResponseData WriteResponse(
        HttpRequestData request,
        HttpStatusCode statusCode,
        ReadinessResponse body)
    {
        var response = request.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json");
        response.WriteString(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }

    private sealed record ReadinessResponse(
        string Status,
        string TenantId,
        IReadOnlyCollection<ReadinessCheck> Checks);

    private sealed record ReadinessCheck(
        string Name,
        bool Ready,
        string Severity,
        string? Detail);

    private sealed record RequiredSecret(string Name, string? SecretName);
}
