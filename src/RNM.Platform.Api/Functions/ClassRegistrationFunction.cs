using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Classes;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Observability;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Functions;

public sealed class ClassRegistrationFunction
{
    private const string ApiKeyHeaderName = "x-rnm-api-key";
    private const string RegistrationSecretHeaderName = "X-RNM-Class-Registration-Secret";
    private const int MaxBodyBytes = 32768;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, RateLimitCounter> RateLimitCounters = new(StringComparer.Ordinal);

    private readonly ClassRegistrationService classRegistrationService;
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly RnmRuntimeConfiguration runtimeConfiguration;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly LimitedRequestBodyReader requestBodyReader;
    private readonly ISecretProvider secretProvider;
    private readonly IEventLogger eventLogger;

    public ClassRegistrationFunction(
        ClassRegistrationService classRegistrationService,
        ITenantConfigurationProvider tenantConfigurationProvider,
        ApiKeyRequestValidator apiKeyRequestValidator,
        RnmRuntimeConfiguration runtimeConfiguration,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory,
        LimitedRequestBodyReader requestBodyReader,
        ISecretProvider secretProvider,
        IEventLogger eventLogger)
    {
        this.classRegistrationService = classRegistrationService;
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.apiKeyRequestValidator = apiKeyRequestValidator;
        this.runtimeConfiguration = runtimeConfiguration;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
        this.requestBodyReader = requestBodyReader;
        this.secretProvider = secretProvider;
        this.eventLogger = eventLogger;
    }

    [Function("ClassRegistrationCreate")]
    public async Task<HttpResponseData> RegisterAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            "options",
            Route = "tenants/{tenantId}/classes/{sessionId}/registrations")]
        HttpRequestData request,
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var correlationId = correlationContextFactory.FromRequest(request).Value;
        TenantConfiguration tenant;
        try
        {
            tenant = await tenantConfigurationProvider.GetTenantConfigurationAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ConfigurationException)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogEndpointFailureAsync(tenantId, correlationId, "tenant_configuration_unavailable", cancellationToken).ConfigureAwait(false);
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.ServiceUnavailable,
                safeErrorResponseFactory.CreateServiceUnavailable(correlationId));
        }

        var allowedOrigin = GetAllowedOrigin(request, tenant);
        if (string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            if (allowedOrigin is null)
            {
                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.Forbidden,
                    safeErrorResponseFactory.CreateUnauthorized(correlationId));
            }

            var preflight = request.CreateResponse(HttpStatusCode.NoContent);
            AddCorsHeaders(preflight, allowedOrigin);
            return preflight;
        }

        var internalAuthorized = IsInternalAuthorized(request);
        if (!internalAuthorized && !AllowRequest(tenantId, tenant.Classes?.EffectiveMaxRegistrationsPerMinute ?? 60))
        {
            var rateLimited = responseWriter.WriteSafeError(
                request,
                HttpStatusCode.TooManyRequests,
                safeErrorResponseFactory.CreateRateLimited(correlationId));
            AddCorsHeaders(rateLimited, allowedOrigin);
            return rateLimited;
        }

        var tenantAuthorization = internalAuthorized
            ? TenantWebhookAuthorization.Authorized
            : await AuthorizeTenantWebhookAsync(request, tenant, tenantId, correlationId, cancellationToken).ConfigureAwait(false);
        if (tenantAuthorization is TenantWebhookAuthorization.Unavailable)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.ServiceUnavailable,
                safeErrorResponseFactory.CreateServiceUnavailable(correlationId));
        }

        if (tenantAuthorization is not TenantWebhookAuthorization.Authorized)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Unauthorized,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }

        var body = await requestBodyReader.ReadAsStringAsync(request, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (body.IsTooLarge)
        {
            var response = responseWriter.WriteSafeError(
                request,
                HttpStatusCode.RequestEntityTooLarge,
                safeErrorResponseFactory.CreatePayloadTooLarge(correlationId));
            AddCorsHeaders(response, allowedOrigin);
            return response;
        }

        ClassRegistrationBody? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ClassRegistrationBody>(body.Body, JsonOptions);
        }
        catch (JsonException)
        {
            var response = responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
            AddCorsHeaders(response, allowedOrigin);
            return response;
        }

        if (parsed is null)
        {
            var response = responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
            AddCorsHeaders(response, allowedOrigin);
            return response;
        }

        ClassRegistrationResult result;
        try
        {
            result = await classRegistrationService
                .RegisterAsync(
                    new ClassRegistrationRequest(
                        tenantId,
                        correlationId,
                        sessionId,
                        parsed.CustomerName ?? parsed.Name ?? string.Empty,
                        parsed.CustomerPhoneNumber ?? parsed.PhoneNumber,
                        parsed.CustomerEmail ?? parsed.Email)
                    {
                        Source = parsed.Source ?? "WebRegistration",
                        CampaignId = parsed.CampaignId,
                        MarketingConsentGranted = parsed.MarketingConsentGranted ?? false,
                        ConsentCapturedAt = parsed.ConsentCapturedAt,
                        ConsentTextVersion = parsed.ConsentTextVersion,
                        Attributes = parsed.Attributes ?? new Dictionary<string, string>()
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogEndpointFailureAsync(tenantId, correlationId, "registration_service_unavailable", cancellationToken).ConfigureAwait(false);
            var unavailable = responseWriter.WriteSafeError(
                request,
                HttpStatusCode.ServiceUnavailable,
                safeErrorResponseFactory.CreateServiceUnavailable(correlationId));
            AddCorsHeaders(unavailable, allowedOrigin);
            return unavailable;
        }

        var resultResponse = responseWriter.WriteJson(
            request,
            MapStatusCode(result),
            result,
            correlationId);
        AddCorsHeaders(resultResponse, allowedOrigin);
        return resultResponse;
    }

    private bool IsInternalAuthorized(HttpRequestData request) =>
        request.Headers.TryGetValues(ApiKeyHeaderName, out var values)
        && apiKeyRequestValidator.IsValid(values.FirstOrDefault(), runtimeConfiguration.InternalApiKey);

    private static string? GetAllowedOrigin(
        HttpRequestData request,
        TenantConfiguration tenant)
    {
        var origin = request.GetHeaderValue("Origin")?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        var allowedOrigins = tenant.Classes?.AllowedRegistrationOrigins ?? [];
        return allowedOrigins
            .Select(value => value.Trim().TrimEnd('/'))
            .Any(value => string.Equals(value, origin, StringComparison.OrdinalIgnoreCase))
                ? origin
                : null;
    }

    private static void AddCorsHeaders(HttpResponseData response, string? allowedOrigin)
    {
        if (string.IsNullOrWhiteSpace(allowedOrigin))
        {
            return;
        }

        response.Headers.Add("Access-Control-Allow-Origin", allowedOrigin);
        response.Headers.Add("Vary", "Origin");
        response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", $"Content-Type, x-correlation-id, {RegistrationSecretHeaderName}");
    }

    private async Task<TenantWebhookAuthorization> AuthorizeTenantWebhookAsync(
        HttpRequestData request,
        TenantConfiguration tenant,
        string tenantId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var secretName = tenant.SecretNames.ClassRegistrationWebhookSecret;
        if (string.IsNullOrWhiteSpace(secretName))
        {
            await LogAuthFailureAsync(tenantId, correlationId, "class_registration_secret_not_configured", cancellationToken).ConfigureAwait(false);
            return TenantWebhookAuthorization.Unauthorized;
        }

        try
        {
            var expected = await secretProvider.GetSecretAsync(secretName, cancellationToken).ConfigureAwait(false);
            var provided = request.GetHeaderValue(RegistrationSecretHeaderName);
            var valid = apiKeyRequestValidator.IsValid(provided, expected);
            if (!valid)
            {
                await LogAuthFailureAsync(tenantId, correlationId, "invalid_class_registration_secret", cancellationToken).ConfigureAwait(false);
            }

            return valid
                ? TenantWebhookAuthorization.Authorized
                : TenantWebhookAuthorization.Unauthorized;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAuthFailureAsync(tenantId, correlationId, "class_registration_secret_unavailable", cancellationToken).ConfigureAwait(false);
            return TenantWebhookAuthorization.Unavailable;
        }
    }

    private static bool AllowRequest(string tenantId, int limit)
    {
        var now = DateTimeOffset.UtcNow;
        var counter = RateLimitCounters.GetOrAdd(tenantId, _ => new RateLimitCounter(now, 0));
        lock (counter)
        {
            if (now - counter.WindowStartedAt >= TimeSpan.FromMinutes(1))
            {
                counter.WindowStartedAt = now;
                counter.Count = 0;
            }

            counter.Count++;
            return counter.Count <= limit;
        }
    }

    private static HttpStatusCode MapStatusCode(ClassRegistrationResult result)
    {
        if (result.Succeeded)
        {
            return HttpStatusCode.OK;
        }

        return result.FailureReason switch
        {
            ClassFailureReason.MissingSession => HttpStatusCode.NotFound,
            ClassFailureReason.CapacityReached => HttpStatusCode.Conflict,
            ClassFailureReason.CrmWriteFailed or ClassFailureReason.StorageFailure => HttpStatusCode.ServiceUnavailable,
            _ => HttpStatusCode.BadRequest
        };
    }

    private async Task LogAuthFailureAsync(
        string tenantId,
        string correlationId,
        string outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger.LogEventAsync(
                    TelemetryEventNames.SecurityAuthFailed,
                    new SafeTelemetryProperties()
                        .Add("tenantId", tenantId)
                        .Add("correlationId", correlationId)
                        .Add("endpoint", "class_registration")
                        .Add("outcome", outcome)
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Security telemetry is best-effort.
        }
    }

    private async Task LogEndpointFailureAsync(
        string tenantId,
        string correlationId,
        string outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger.LogEventAsync(
                    TelemetryEventNames.ClassRegistrationFailed,
                    new SafeTelemetryProperties()
                        .Add("tenantId", tenantId)
                        .Add("correlationId", correlationId)
                        .Add("outcome", outcome)
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Failure telemetry is best-effort.
        }
    }

    private sealed record ClassRegistrationBody(
        string? CustomerName,
        string? Name,
        string? CustomerPhoneNumber,
        string? PhoneNumber,
        string? CustomerEmail,
        string? Email,
        string? Source,
        string? CampaignId,
        bool? MarketingConsentGranted,
        DateTimeOffset? ConsentCapturedAt,
        string? ConsentTextVersion,
        IReadOnlyDictionary<string, string>? Attributes);

    private sealed class RateLimitCounter(DateTimeOffset windowStartedAt, int count)
    {
        public DateTimeOffset WindowStartedAt { get; set; } = windowStartedAt;

        public int Count { get; set; } = count;
    }

    private enum TenantWebhookAuthorization
    {
        Unauthorized = 0,
        Authorized = 1,
        Unavailable = 2
    }
}
