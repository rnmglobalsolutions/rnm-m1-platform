using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.LeadIntake;
using RNM.Platform.Application.Observability;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Functions;

public sealed class ManyChatLeadWebhookFunction
{
    private const string SecretHeaderName = "X-RNM-ManyChat-Secret";
    private const int MaxBodyBytes = 32768;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, RateLimitCounter> RateLimits = new(StringComparer.Ordinal);

    private readonly InboundLeadIntakeService intakeService;
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ISecretProvider secretProvider;
    private readonly ApiKeyRequestValidator secretValidator;
    private readonly LimitedRequestBodyReader bodyReader;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly SafeErrorResponseFactory errorFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly IEventLogger eventLogger;

    public ManyChatLeadWebhookFunction(
        InboundLeadIntakeService intakeService,
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISecretProvider secretProvider,
        ApiKeyRequestValidator secretValidator,
        LimitedRequestBodyReader bodyReader,
        CorrelationContextFactory correlationContextFactory,
        SafeErrorResponseFactory errorFactory,
        SafeHttpResponseWriter responseWriter,
        IEventLogger eventLogger)
    {
        this.intakeService = intakeService;
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.secretProvider = secretProvider;
        this.secretValidator = secretValidator;
        this.bodyReader = bodyReader;
        this.correlationContextFactory = correlationContextFactory;
        this.errorFactory = errorFactory;
        this.responseWriter = responseWriter;
        this.eventLogger = eventLogger;
    }

    [Function("ManyChatLeadWebhook")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            Route = "tenants/{tenantId}/webhooks/manychat/leads")]
        HttpRequestData request,
        string tenantId,
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
            return responseWriter.WriteSafeError(request, HttpStatusCode.BadRequest, errorFactory.CreateBadRequest(correlationId));
        }

        var integration = tenant.Integrations?.ManyChat;
        if (integration?.EffectiveEnabled is not true)
        {
            return responseWriter.WriteSafeError(request, HttpStatusCode.Forbidden, errorFactory.CreateUnauthorized(correlationId));
        }

        if (string.IsNullOrWhiteSpace(tenant.SecretNames.ManyChatWebhookSecret))
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.ServiceUnavailable,
                errorFactory.CreateServiceUnavailable(correlationId));
        }

        string expectedSecret;
        try
        {
            expectedSecret = await secretProvider.GetSecretAsync(tenant.SecretNames.ManyChatWebhookSecret, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.LeadIntakeFailed, tenantId, correlationId, "secret_unavailable", cancellationToken)
                .ConfigureAwait(false);
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.ServiceUnavailable,
                errorFactory.CreateServiceUnavailable(correlationId));
        }

        var providedSecret = GetHeader(request, SecretHeaderName);
        if (!secretValidator.IsValid(providedSecret, expectedSecret))
        {
            await LogAsync(TelemetryEventNames.SecurityAuthFailed, tenantId, correlationId, "invalid_manychat_secret", cancellationToken)
                .ConfigureAwait(false);
            return responseWriter.WriteSafeError(request, HttpStatusCode.Unauthorized, errorFactory.CreateUnauthorized(correlationId));
        }

        if (!AllowRequest(tenantId, integration.EffectiveMaxRequestsPerMinute))
        {
            return responseWriter.WriteSafeError(request, HttpStatusCode.TooManyRequests, errorFactory.CreateRateLimited(correlationId));
        }

        var body = await bodyReader.ReadAsStringAsync(request, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (body.IsTooLarge)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.RequestEntityTooLarge,
                errorFactory.CreatePayloadTooLarge(correlationId));
        }

        ManyChatLeadBody? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ManyChatLeadBody>(body.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return responseWriter.WriteSafeError(request, HttpStatusCode.BadRequest, errorFactory.CreateBadRequest(correlationId));
        }

        if (payload is null || !TryReadAttributes(payload.Attributes, out var attributes))
        {
            return responseWriter.WriteSafeError(request, HttpStatusCode.BadRequest, errorFactory.CreateBadRequest(correlationId));
        }

        await LogAsync(TelemetryEventNames.LeadIntakeAuthenticated, tenantId, correlationId, "authenticated", cancellationToken)
            .ConfigureAwait(false);
        var result = await intakeService.ProcessAsync(
                new InboundLeadIntakeRequest(
                    tenantId,
                    tenant.VerticalId.Value,
                    correlationId,
                    "ManyChat",
                    payload.ExternalEventId ?? string.Empty,
                    payload.ExternalContactId ?? payload.SubscriberId ?? string.Empty,
                    payload.CustomerName ?? payload.Name,
                    payload.CustomerPhoneNumber ?? payload.PhoneNumber,
                    payload.CustomerEmail ?? payload.Email,
                    payload.CampaignId,
                    payload.MarketingConsentGranted,
                    payload.ConsentCapturedAt,
                    payload.ConsentTextVersion,
                    attributes,
                    integration.EffectiveScheduleFollowUp),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            var status = result.FailureCode is "dependency_unavailable" or "crm_upsert_failed" or "timeline_write_failed" or "consent_audit_failed" or "processing_failed"
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.BadRequest;
            return responseWriter.WriteSafeError(
                request,
                status,
                status == HttpStatusCode.ServiceUnavailable
                    ? errorFactory.CreateServiceUnavailable(correlationId)
                    : errorFactory.CreateBadRequest(correlationId));
        }

        return responseWriter.WriteJson(
            request,
            result.Processing ? HttpStatusCode.Accepted : HttpStatusCode.OK,
            new
            {
                accepted = true,
                duplicate = result.Duplicate,
                processing = result.Processing,
                followUpRequested = result.FollowUpRequested,
                businessNotificationQueued = result.BusinessNotificationQueued,
                tenantId,
                correlationId
            },
            correlationId);
    }

    private static bool TryReadAttributes(
        IReadOnlyDictionary<string, JsonElement>? source,
        out IReadOnlyDictionary<string, string> attributes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source ?? new Dictionary<string, JsonElement>())
        {
            var value = pair.Value.ValueKind switch
            {
                JsonValueKind.String => pair.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => pair.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                _ => null
            };
            if (value is null)
            {
                attributes = new Dictionary<string, string>();
                return false;
            }

            result[pair.Key] = value;
        }

        attributes = result;
        return true;
    }

    private static bool AllowRequest(string tenantId, int limit)
    {
        var now = DateTimeOffset.UtcNow;
        var counter = RateLimits.GetOrAdd(tenantId, _ => new RateLimitCounter(now, 0));
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

    private static string? GetHeader(HttpRequestData request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private async Task LogAsync(
        string eventName,
        string tenantId,
        string correlationId,
        string outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger.LogEventAsync(
                    eventName,
                    new SafeTelemetryProperties()
                        .Add("tenantId", tenantId)
                        .Add("correlationId", correlationId)
                        .Add("provider", "manychat")
                        .Add("outcome", outcome)
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Telemetry is best-effort.
        }
    }

    private sealed record ManyChatLeadBody(
        string? ExternalEventId,
        string? ExternalContactId,
        string? SubscriberId,
        string? CustomerName,
        string? Name,
        string? CustomerPhoneNumber,
        string? PhoneNumber,
        string? CustomerEmail,
        string? Email,
        string? CampaignId,
        bool MarketingConsentGranted,
        DateTimeOffset? ConsentCapturedAt,
        string? ConsentTextVersion,
        IReadOnlyDictionary<string, JsonElement>? Attributes);

    private sealed class RateLimitCounter(DateTimeOffset windowStartedAt, int count)
    {
        public DateTimeOffset WindowStartedAt { get; set; } = windowStartedAt;

        public int Count { get; set; } = count;
    }
}
