using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Infrastructure.Booking;
using RNM.Platform.Infrastructure.Crm;
using RNM.Platform.Infrastructure.Providers;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Functions;

public sealed class ReadinessFunction
{
    private const string ApiKeyHeaderName = "x-rnm-api-key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ISecretProvider secretProvider;
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly RnmRuntimeConfiguration runtimeConfiguration;
    private readonly IReadOnlyCollection<IBookingProviderAdapter> bookingProviderAdapters;
    private readonly IReadOnlyCollection<ICrmProviderAdapter> crmProviderAdapters;

    public ReadinessFunction(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISecretProvider secretProvider,
        ApiKeyRequestValidator apiKeyRequestValidator,
        RnmRuntimeConfiguration runtimeConfiguration,
        IEnumerable<IBookingProviderAdapter> bookingProviderAdapters,
        IEnumerable<ICrmProviderAdapter> crmProviderAdapters)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
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
            Route = "tenants/{tenantId}/ready")]
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
            checks.Add(new ReadinessCheck("tenantConfiguration", true));

            checks.Add(new ReadinessCheck(
                "storage",
                IsResolvedSetting(Environment.GetEnvironmentVariable("AzureWebJobsStorage"))));
            checks.Add(new ReadinessCheck(
                "bookingProvider",
                SupportsProvider(bookingProviderAdapters, tenant.Providers.BookingProvider)));
            checks.Add(new ReadinessCheck(
                "crmProvider",
                SupportsProvider(crmProviderAdapters, tenant.Providers.CrmProvider)));
            checks.Add(new ReadinessCheck(
                "smsProvider",
                string.Equals(tenant.Providers.SmsProvider, ProviderNames.Twilio, StringComparison.OrdinalIgnoreCase)));
            checks.Add(new ReadinessCheck(
                "emailProvider",
                string.Equals(tenant.Providers.EmailProvider, ProviderNames.SendGrid, StringComparison.OrdinalIgnoreCase)));
            checks.Add(new ReadinessCheck(
                "sendGrid",
                IsResolvedSetting(Environment.GetEnvironmentVariable("SENDGRID_API_KEY"))));
            checks.Add(new ReadinessCheck(
                "emailConfiguration",
                !string.IsNullOrWhiteSpace(tenant.Communication.EmailFromAddress)
                && !string.IsNullOrWhiteSpace(tenant.Communication.ConfirmationTemplates.EmailSubjectTemplate)
                && !string.IsNullOrWhiteSpace(tenant.Communication.ConfirmationTemplates.EmailBodyTemplate)));

            var secretNames = new List<string?>
            {
                tenant.SecretNames.VoiceWebhookSecret,
                tenant.SecretNames.TwilioAccountSid,
                tenant.SecretNames.TwilioAuthToken,
                tenant.SecretNames.BookingCredentials ?? tenant.SecretNames.BookingApiKey
            };
            if (!string.Equals(
                    tenant.Providers.CrmProvider,
                    ProviderNames.AzureTable,
                    StringComparison.OrdinalIgnoreCase))
            {
                secretNames.Add(tenant.SecretNames.CrmCredentials ?? tenant.SecretNames.CrmApiKey);
            }

            var secretsReady = true;
            foreach (var secretName in secretNames
                         .OfType<string>()
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var value = await secretProvider
                        .GetSecretAsync(secretName, cancellationToken)
                        .ConfigureAwait(false);
                    secretsReady &= !string.IsNullOrWhiteSpace(value);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    secretsReady = false;
                }
            }

            checks.Add(new ReadinessCheck("providerSecrets", secretsReady));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            checks.Add(new ReadinessCheck("tenantConfiguration", false));
        }

        var ready = checks.Count > 0 && checks.All(check => check.Ready);
        return WriteResponse(
            request,
            ready ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
            new ReadinessResponse(ready ? "ready" : "not_ready", tenantId, checks));
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

    private sealed record ReadinessCheck(string Name, bool Ready);
}
