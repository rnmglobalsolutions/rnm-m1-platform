using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.EmailPreferences;
using RNM.Platform.Application.Observability;
using RNM.Platform.Infrastructure.Messaging;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Functions;

/// <summary>
/// Receives the SendGrid Event Webhook for the whole account. Each event carries the tenant in custom_args,
/// so complaints, unsubscribes and hard bounces update only that tenant's CRM.
/// </summary>
public sealed class SendGridEventWebhookFunction
{
    private const int MaxBodyBytes = 5 * 1024 * 1024;
    private readonly SendGridEventWebhookValidator validator;
    private readonly ISecretProvider secretProvider;
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly EmailPreferenceService emailPreferences;
    private readonly LimitedRequestBodyReader bodyReader;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly SafeErrorResponseFactory errorFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly IEventLogger eventLogger;
    private readonly TimeProvider timeProvider;

    public SendGridEventWebhookFunction(
        SendGridEventWebhookValidator validator,
        ISecretProvider secretProvider,
        ITenantConfigurationProvider tenantConfigurationProvider,
        EmailPreferenceService emailPreferences,
        LimitedRequestBodyReader bodyReader,
        CorrelationContextFactory correlationContextFactory,
        SafeErrorResponseFactory errorFactory,
        SafeHttpResponseWriter responseWriter,
        IEventLogger eventLogger,
        TimeProvider timeProvider)
    {
        this.validator = validator;
        this.secretProvider = secretProvider;
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.emailPreferences = emailPreferences;
        this.bodyReader = bodyReader;
        this.correlationContextFactory = correlationContextFactory;
        this.errorFactory = errorFactory;
        this.responseWriter = responseWriter;
        this.eventLogger = eventLogger;
        this.timeProvider = timeProvider;
    }

    [Function("SendGridEventWebhook")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhooks/sendgrid/events")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var correlationId = correlationContextFactory.FromRequest(request).Value;
        await LogAsync(TelemetryEventNames.WebhookReceived, correlationId, "received", cancellationToken).ConfigureAwait(false);

        var body = await bodyReader.ReadAsStringAsync(request, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (body.IsTooLarge)
        {
            return responseWriter.WriteSafeError(request, HttpStatusCode.RequestEntityTooLarge, errorFactory.CreateBadRequest(correlationId));
        }

        string publicKey;
        try
        {
            publicKey = await secretProvider
                .GetSecretAsync(SendGridEventWebhookValidator.VerificationKeySecretName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Misconfiguration: fail so SendGrid retries once the key is in Key Vault.
            await LogAsync(TelemetryEventNames.ApiRequestFailed, correlationId, "verification_key_unavailable", cancellationToken).ConfigureAwait(false);
            return responseWriter.WriteSafeError(request, HttpStatusCode.ServiceUnavailable, errorFactory.CreateBadRequest(correlationId));
        }

        if (!validator.IsValid(
                publicKey,
                request.GetHeaderValue(SendGridEventWebhookValidator.SignatureHeader),
                request.GetHeaderValue(SendGridEventWebhookValidator.TimestampHeader),
                Encoding.UTF8.GetBytes(body.Body),
                timeProvider.GetUtcNow()))
        {
            await LogAsync(TelemetryEventNames.WebhookValidationFailed, correlationId, "invalid_signature", cancellationToken).ConfigureAwait(false);
            await LogAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, "invalid_signature", cancellationToken).ConfigureAwait(false);
            return responseWriter.WriteSafeError(request, HttpStatusCode.Unauthorized, errorFactory.CreateUnauthorized(correlationId));
        }

        IReadOnlyList<SendGridEvent> events;
        try
        {
            events = SendGridEvent.ParseBatch(body.Body);
        }
        catch (JsonException)
        {
            return responseWriter.WriteSafeError(request, HttpStatusCode.BadRequest, errorFactory.CreateBadRequest(correlationId));
        }

        var knownTenants = new Dictionary<string, bool>(StringComparer.Ordinal);
        var applied = 0;
        var ignored = 0;
        var failed = 0;
        foreach (var item in events)
        {
            var action = item.Action;
            if (action is SendGridEventAction.Ignore
                || string.IsNullOrWhiteSpace(item.TenantId)
                || string.IsNullOrWhiteSpace(item.Email)
                || !await IsKnownTenantAsync(item.TenantId, knownTenants, cancellationToken).ConfigureAwait(false))
            {
                ignored++;
                continue;
            }

            var eventCorrelationId = item.CorrelationId ?? correlationId;
            var succeeded = action is SendGridEventAction.Unsubscribe
                ? await emailPreferences
                    .UnsubscribeAsync(item.TenantId, eventCorrelationId, item.Email, "SendGrid", item.EventType, cancellationToken)
                    .ConfigureAwait(false)
                : await emailPreferences
                    .RecordBounceAsync(item.TenantId, eventCorrelationId, item.Email, item.Reason ?? "bounce", cancellationToken)
                    .ConfigureAwait(false);
            if (succeeded)
            {
                applied++;
            }
            else
            {
                failed++;
            }
        }

        if (failed > 0)
        {
            // SendGrid retries the whole batch on non-2xx; opt-outs are idempotent.
            await LogAsync(TelemetryEventNames.ApiRequestFailed, correlationId, "crm_update_failed", cancellationToken).ConfigureAwait(false);
            return responseWriter.WriteSafeError(request, HttpStatusCode.ServiceUnavailable, errorFactory.CreateBadRequest(correlationId));
        }

        await LogAsync(TelemetryEventNames.ApiRequestCompleted, correlationId, "accepted", cancellationToken).ConfigureAwait(false);
        return responseWriter.WriteJson(request, HttpStatusCode.OK, new { accepted = true, applied, ignored, correlationId }, correlationId);
    }

    private async Task<bool> IsKnownTenantAsync(string tenantId, Dictionary<string, bool> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(tenantId, out var known))
        {
            return known;
        }

        try
        {
            await tenantConfigurationProvider.GetTenantConfigurationAsync(tenantId, cancellationToken).ConfigureAwait(false);
            known = true;
        }
        catch (ConfigurationException)
        {
            known = false;
        }

        cache[tenantId] = known;
        return known;
    }

    private async Task LogAsync(string eventName, string correlationId, string outcome, CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger.LogEventAsync(
                    eventName,
                    new SafeTelemetryProperties()
                        .Add("correlationId", correlationId)
                        .Add("provider", "sendgrid")
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
}

internal enum SendGridEventAction
{
    Ignore = 0,
    Unsubscribe = 1,
    Bounce = 2
}

internal sealed record SendGridEvent(
    string EventType,
    string? Email,
    string? TenantId,
    string? CorrelationId,
    string? BounceType,
    string? Reason)
{
    /// <summary>
    /// Spam reports and unsubscribes opt the address out of email; only hard bounces ("bounce", not "blocked")
    /// are recorded. Everything else (delivered, deferred, opens...) is ignored.
    /// </summary>
    public SendGridEventAction Action => EventType switch
    {
        "spamreport" or "unsubscribe" or "group_unsubscribe" => SendGridEventAction.Unsubscribe,
        "bounce" when !string.Equals(BounceType, "blocked", StringComparison.OrdinalIgnoreCase) => SendGridEventAction.Bounce,
        _ => SendGridEventAction.Ignore
    };

    public static IReadOnlyList<SendGridEvent> ParseBatch(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("SendGrid events must be a JSON array.");
        }

        return document.RootElement
            .EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.Object)
            .Select(element => new SendGridEvent(
                Read(element, "event") ?? string.Empty,
                Read(element, "email"),
                Read(element, SendGridCustomArgs.TenantId),
                Read(element, SendGridCustomArgs.CorrelationId),
                Read(element, "type"),
                Read(element, "reason")))
            .ToArray();
    }

    private static string? Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
