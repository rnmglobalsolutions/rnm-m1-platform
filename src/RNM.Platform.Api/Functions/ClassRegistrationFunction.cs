using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Classes;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Api.Functions;

public sealed class ClassRegistrationFunction
{
    private const string ApiKeyHeaderName = "x-rnm-api-key";
    private const int MaxBodyBytes = 32768;
    private const int PublicRateLimitPerMinute = 30;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object RateLimitLock = new();
    private static readonly Dictionary<string, RateLimitCounter> RateLimitCounters = new(StringComparer.Ordinal);

    private readonly ClassRegistrationService classRegistrationService;
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly RnmRuntimeConfiguration runtimeConfiguration;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly LimitedRequestBodyReader requestBodyReader;

    public ClassRegistrationFunction(
        ClassRegistrationService classRegistrationService,
        ITenantConfigurationProvider tenantConfigurationProvider,
        ApiKeyRequestValidator apiKeyRequestValidator,
        RnmRuntimeConfiguration runtimeConfiguration,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory,
        LimitedRequestBodyReader requestBodyReader)
    {
        this.classRegistrationService = classRegistrationService;
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.apiKeyRequestValidator = apiKeyRequestValidator;
        this.runtimeConfiguration = runtimeConfiguration;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
        this.requestBodyReader = requestBodyReader;
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
        if (!internalAuthorized && allowedOrigin is null)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Forbidden,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }

        if (!internalAuthorized && !AllowPublicRequest(request, tenantId, allowedOrigin!))
        {
            var rateLimited = responseWriter.WriteSafeError(
                request,
                HttpStatusCode.TooManyRequests,
                safeErrorResponseFactory.CreateRateLimited(correlationId));
            AddCorsHeaders(rateLimited, allowedOrigin);
            return rateLimited;
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

        var result = await classRegistrationService
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
                    Attributes = parsed.Attributes ?? new Dictionary<string, string>()
                },
                cancellationToken)
            .ConfigureAwait(false);

        var resultResponse = responseWriter.WriteJson(
            request,
            result.Succeeded ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
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
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, x-correlation-id");
    }

    private static bool AllowPublicRequest(
        HttpRequestData request,
        string tenantId,
        string allowedOrigin)
    {
        var key = $"{tenantId}|{allowedOrigin}|{GetClientAddress(request)}";
        var now = DateTimeOffset.UtcNow;

        lock (RateLimitLock)
        {
            if (!RateLimitCounters.TryGetValue(key, out var counter)
                || now - counter.WindowStartedAt >= TimeSpan.FromMinutes(1))
            {
                RateLimitCounters[key] = new RateLimitCounter(now, 1);
                return true;
            }

            if (counter.Count >= PublicRateLimitPerMinute)
            {
                return false;
            }

            RateLimitCounters[key] = counter with { Count = counter.Count + 1 };
            return true;
        }
    }

    private static string GetClientAddress(HttpRequestData request)
    {
        var forwardedFor = request.GetHeaderValue("X-Forwarded-For");
        if (string.IsNullOrWhiteSpace(forwardedFor))
        {
            return "unknown";
        }

        return forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            ?? "unknown";
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
        IReadOnlyDictionary<string, string>? Attributes);

    private sealed record RateLimitCounter(
        DateTimeOffset WindowStartedAt,
        int Count);
}
