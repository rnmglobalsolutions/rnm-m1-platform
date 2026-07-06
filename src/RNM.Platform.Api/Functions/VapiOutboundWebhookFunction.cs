using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Api.Voice;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Tenancy;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Functions;

public sealed class VapiOutboundWebhookFunction
{
    private readonly TenantResolver tenantResolver;
    private readonly VapiWebhookValidator vapiWebhookValidator;
    private readonly ISecretProvider secretProvider;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly IEventLogger eventLogger;
    private readonly ICrmAdapter crmAdapter;
    private readonly LimitedRequestBodyReader requestBodyReader;
    private readonly VapiWebhookOptions options;

    public VapiOutboundWebhookFunction(
        TenantResolver tenantResolver,
        VapiWebhookValidator vapiWebhookValidator,
        ISecretProvider secretProvider,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory,
        IEventLogger eventLogger,
        ICrmAdapter crmAdapter,
        LimitedRequestBodyReader requestBodyReader,
        VapiWebhookOptions options)
    {
        this.tenantResolver = tenantResolver;
        this.vapiWebhookValidator = vapiWebhookValidator;
        this.secretProvider = secretProvider;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
        this.eventLogger = eventLogger;
        this.crmAdapter = crmAdapter;
        this.requestBodyReader = requestBodyReader;
        this.options = options;
    }

    [Function("VapiOutboundWebhook")]
    public async Task<HttpResponseData> HandleAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            Route = "tenants/{tenantId}/webhooks/vapi/outbound")]
        HttpRequestData request,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var query = ParseQuery(request.Url.Query);
        var correlationId = query.GetValueOrDefault("correlationId")
            ?? correlationContextFactory.FromRequest(request).Value;

        try
        {
            await LogWebhookAsync(
                    TelemetryEventNames.WebhookReceived,
                    tenantId,
                    correlationId,
                    "received",
                    cancellationToken)
                .ConfigureAwait(false);

            var tenantContext = await tenantResolver.ResolveAsync(tenantId, cancellationToken).ConfigureAwait(false);
            var webhookSecret = await secretProvider
                .GetSecretAsync(tenantContext.SecretNames.VoiceWebhookSecret, cancellationToken)
                .ConfigureAwait(false);
            var bodyReadResult = await requestBodyReader
                .ReadAsStringAsync(request, options.MaxBodyBytes, cancellationToken)
                .ConfigureAwait(false);
            if (bodyReadResult.IsTooLarge)
            {
                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.RequestEntityTooLarge,
                    safeErrorResponseFactory.CreatePayloadTooLarge(correlationId));
            }

            var rawBody = bodyReadResult.Body;
            var isValid = vapiWebhookValidator.IsValidBearerToken(request.GetHeaderValue("Authorization"), webhookSecret)
                || vapiWebhookValidator.IsValidLegacySecret(request.GetHeaderValue("X-Vapi-Secret"), webhookSecret)
                || vapiWebhookValidator.IsValidHmacSha256(rawBody, request.GetHeaderValue("x-signature"), webhookSecret);
            if (!isValid)
            {
                await LogWebhookAsync(
                        TelemetryEventNames.WebhookValidationFailed,
                        tenantId,
                        correlationId,
                        "invalid_signature",
                        cancellationToken)
                    .ConfigureAwait(false);
                await LogWebhookAsync(
                        TelemetryEventNames.SecurityAuthFailed,
                        tenantId,
                        correlationId,
                        "invalid_signature",
                        cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.Unauthorized,
                    safeErrorResponseFactory.CreateUnauthorized(correlationId));
            }

            await LogWebhookAsync(
                    TelemetryEventNames.WebhookValidationSucceeded,
                    tenantId,
                    correlationId,
                    "valid",
                    cancellationToken)
                .ConfigureAwait(false);

            var parsed = ParseOutboundEvent(rawBody, query);
            if (!parsed.IsValid)
            {
                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.BadRequest,
                    safeErrorResponseFactory.CreateBadRequest(correlationId));
            }

            if (!parsed.IsTerminal || string.IsNullOrWhiteSpace(parsed.ProviderContactId))
            {
                await LogWebhookAsync(
                        TelemetryEventNames.ApiRequestCompleted,
                        tenantId,
                        correlationId,
                        parsed.Outcome ?? "ignored_non_terminal",
                        cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteJson(
                    request,
                    HttpStatusCode.Accepted,
                    new
                    {
                        accepted = true,
                        processed = false,
                        correlationId,
                        tenantId,
                        outcome = parsed.Outcome ?? "ignored_non_terminal"
                    },
                    correlationId);
            }

            var recordResult = await crmAdapter
                .RecordOutboundAttemptAsync(
                    new CrmOutboundAttemptRequest(
                        tenantId,
                        correlationId,
                        parsed.ProviderContactId,
                        parsed.Outcome ?? "completed")
                    {
                        Note = $"Outbound Vapi call completed: {parsed.Outcome ?? "completed"}"
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var outcome = recordResult.Succeeded
                ? "completed"
                : recordResult.FailureReason?.ToString() ?? "record_failed";
            await LogWebhookAsync(
                    recordResult.Succeeded
                        ? TelemetryEventNames.OutboundCallCompleted
                        : TelemetryEventNames.OutboundCallFailed,
                    tenantId,
                    correlationId,
                    outcome,
                    cancellationToken,
                    parsed.ProviderContactId)
                .ConfigureAwait(false);

            return responseWriter.WriteJson(
                request,
                HttpStatusCode.Accepted,
                new
                {
                    accepted = true,
                    processed = recordResult.Succeeded,
                    correlationId,
                    tenantId,
                    providerContactId = parsed.ProviderContactId,
                    outcome
                },
                correlationId);
        }
        catch (ConfigurationException)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.InternalServerError,
                safeErrorResponseFactory.CreateInternalServerError(correlationId));
        }
    }

    private static OutboundWebhookParseResult ParseOutboundEvent(
        string rawBody,
        IReadOnlyDictionary<string, string> query)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return OutboundWebhookParseResult.Invalid();
            }

            var message = TryGetObject(root, "message");
            var call = TryGetObject(root, "call") ?? TryGetObject(message, "call");
            var metadata = TryGetObject(root, "metadata")
                ?? TryGetObject(call, "metadata")
                ?? TryGetObject(message, "metadata");
            var rawEventType = FirstNonEmpty(
                TryGetString(root, "event"),
                TryGetString(root, "type"),
                TryGetString(message, "type"),
                TryGetString(root, "eventType"),
                TryGetString(call, "status"));
            var endedReason = FirstNonEmpty(
                TryGetString(root, "endedReason"),
                TryGetString(call, "endedReason"),
                TryGetString(message, "endedReason"));
            var status = FirstNonEmpty(
                TryGetString(root, "status"),
                TryGetString(call, "status"),
                TryGetString(message, "status"));
            var providerContactId = query.GetValueOrDefault("contactId")
                ?? query.GetValueOrDefault("providerContactId")
                ?? TryGetString(metadata, "providerContactId")
                ?? TryGetString(metadata, "contactId");

            var outcome = MapTerminalOutcome(rawEventType, endedReason, status);
            return new OutboundWebhookParseResult(
                true,
                providerContactId,
                outcome,
                outcome is not null);
        }
        catch (JsonException)
        {
            return OutboundWebhookParseResult.Invalid();
        }
    }

    private static string? MapTerminalOutcome(
        string? rawEventType,
        string? endedReason,
        string? status)
    {
        var text = string.Join(
            " ",
            rawEventType ?? string.Empty,
            endedReason ?? string.Empty,
            status ?? string.Empty).ToLowerInvariant();

        if (text.Contains("voicemail", StringComparison.Ordinal))
        {
            return "voicemail";
        }

        if (text.Contains("no-answer", StringComparison.Ordinal)
            || text.Contains("no_answer", StringComparison.Ordinal)
            || text.Contains("did-not-answer", StringComparison.Ordinal)
            || text.Contains("not-answer", StringComparison.Ordinal))
        {
            return "no_answer";
        }

        if (text.Contains("end-of-call-report", StringComparison.Ordinal)
            || text.Contains("completed", StringComparison.Ordinal)
            || text.Contains("callended", StringComparison.Ordinal)
            || text.Contains("call-ended", StringComparison.Ordinal)
            || text.Contains("ended", StringComparison.Ordinal))
        {
            return "contacted";
        }

        return null;
    }

    private Task LogWebhookAsync(
        string eventName,
        string tenantId,
        string correlationId,
        string outcome,
        CancellationToken cancellationToken,
        string? providerContactId = null)
    {
        var properties = new SafeTelemetryProperties()
            .Add("tenantId", tenantId)
            .Add("correlationId", correlationId)
            .Add("provider", "vapi")
            .Add("endpoint", "webhooks/vapi/outbound")
            .Add("outcome", outcome)
            .Add("providerContactId", providerContactId)
            .ToDictionary();

        return eventLogger.LogEventAsync(eventName, properties, cancellationToken);
    }

    private static JsonElement? TryGetObject(JsonElement? element, string propertyName)
    {
        return element is { ValueKind: JsonValueKind.Object }
            && element.Value.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.Object
            ? property
            : null;
    }

    private static string? TryGetString(JsonElement? element, string propertyName)
    {
        return element is { ValueKind: JsonValueKind.Object }
            && element.Value.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new Dictionary<string, string>();
        }

        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);
    }

    private sealed record OutboundWebhookParseResult(
        bool IsValid,
        string? ProviderContactId,
        string? Outcome,
        bool IsTerminal)
    {
        public static OutboundWebhookParseResult Invalid() => new(false, null, null, false);
    }
}
