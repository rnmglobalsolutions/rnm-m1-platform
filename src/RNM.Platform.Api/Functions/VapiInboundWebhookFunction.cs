using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Observability;
using RNM.Platform.Api.Security;
using RNM.Platform.Api.Voice;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Inbound;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Tenancy;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Functions;

public sealed class VapiInboundWebhookFunction
{
    private readonly TenantResolver tenantResolver;
    private readonly VapiWebhookValidator vapiWebhookValidator;
    private readonly ISecretProvider secretProvider;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly IEventLogger eventLogger;
    private readonly VapiWebhookPayloadParser payloadParser;
    private readonly VapiWebhookMapper webhookMapper;
    private readonly IInboundBookingWorkflow inboundBookingWorkflow;
    private readonly LimitedRequestBodyReader requestBodyReader;
    private readonly VapiWebhookOptions options;

    public VapiInboundWebhookFunction(
        TenantResolver tenantResolver,
        VapiWebhookValidator vapiWebhookValidator,
        ISecretProvider secretProvider,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory,
        IEventLogger eventLogger,
        VapiWebhookPayloadParser payloadParser,
        VapiWebhookMapper webhookMapper,
        IInboundBookingWorkflow inboundBookingWorkflow,
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
        this.payloadParser = payloadParser;
        this.webhookMapper = webhookMapper;
        this.inboundBookingWorkflow = inboundBookingWorkflow;
        this.requestBodyReader = requestBodyReader;
        this.options = options;
    }

    [Function("VapiInboundWebhook")]
    public async Task<HttpResponseData> Handle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tenants/{tenantId}/webhooks/vapi/inbound")] HttpRequestData request,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var correlationContext = correlationContextFactory.FromRequest(request);
        var correlationId = correlationContext.Value;
        TenantContext? tenantContext = null;

        try
        {
            await LogWebhookAsync(TelemetryEventNames.WebhookReceived, correlationId, tenantId, null, "vapi", "received", cancellationToken)
                .ConfigureAwait(false);

            tenantContext = await tenantResolver.ResolveAsync(tenantId, cancellationToken).ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.TenantResolved, correlationId, null, tenantContext.TenantId, "vapi", "resolved", cancellationToken)
                .ConfigureAwait(false);

            var webhookSecret = await secretProvider
                .GetSecretAsync(tenantContext.SecretNames.VoiceWebhookSecret, cancellationToken)
                .ConfigureAwait(false);
            var bodyReadResult = await requestBodyReader
                .ReadAsStringAsync(request, options.MaxBodyBytes, cancellationToken)
                .ConfigureAwait(false);
            if (bodyReadResult.IsTooLarge)
            {
                await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, null, tenantContext.TenantId, "vapi", "payload_too_large", cancellationToken)
                    .ConfigureAwait(false);

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
                await LogWebhookAsync(TelemetryEventNames.WebhookValidationFailed, correlationId, null, tenantContext.TenantId, "vapi", "invalid_signature", cancellationToken)
                    .ConfigureAwait(false);
                await LogWebhookAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, null, tenantContext.TenantId, "vapi", "invalid_signature", cancellationToken)
                    .ConfigureAwait(false);
                await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, null, tenantContext.TenantId, "vapi", "unauthorized", cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.Unauthorized,
                    safeErrorResponseFactory.CreateUnauthorized(correlationId));
            }

            await LogWebhookAsync(TelemetryEventNames.WebhookValidationSucceeded, correlationId, null, tenantContext.TenantId, "vapi", "valid", cancellationToken)
                .ConfigureAwait(false);

            var parseResult = payloadParser.Parse(rawBody, DateTimeOffset.UtcNow);
            if (!parseResult.IsValid || parseResult.Envelope is null)
            {
                await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, null, tenantContext.TenantId, "vapi", parseResult.ErrorCode ?? "invalid_payload", cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.BadRequest,
                    safeErrorResponseFactory.CreateBadRequest(correlationId));
            }

            var inboundCallEvent = webhookMapper.Map(parseResult.Envelope, tenantContext, correlationId);
            if (inboundCallEvent.EventType is InboundCallEventType.Unsupported)
            {
                var unsupportedResult = InboundCallEventProcessingResult.IgnoredUnsupported();
                await LogVoiceEventAsync(
                        TelemetryEventNames.VoiceEventUnsupported,
                        correlationId,
                        tenantContext.TenantId,
                        parseResult.Envelope.RawEventType,
                        inboundCallEvent.EventType.ToString(),
                        unsupportedResult.Outcome,
                        cancellationToken)
                    .ConfigureAwait(false);

                await LogWebhookAsync(TelemetryEventNames.ApiRequestCompleted, correlationId, null, tenantContext.TenantId, "vapi", unsupportedResult.Outcome, cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteJson(
                    request,
                    HttpStatusCode.Accepted,
                    new
                    {
                        accepted = unsupportedResult.Accepted,
                        processed = unsupportedResult.Processed,
                        correlationId,
                        tenantId = tenantContext.TenantId,
                        eventType = inboundCallEvent.EventType.ToString(),
                        outcome = unsupportedResult.Outcome
                    },
                    correlationId);
            }

            var workflowResult = await inboundBookingWorkflow
                .ProcessAsync(inboundCallEvent, cancellationToken)
                .ConfigureAwait(false);
            var apiTelemetryEventName = workflowResult.Outcome is InboundBookingWorkflowOutcome.Failed
                ? TelemetryEventNames.ApiRequestFailed
                : TelemetryEventNames.ApiRequestCompleted;
            var responseStatusCode = workflowResult.Outcome is InboundBookingWorkflowOutcome.Failed
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.Accepted;
            var workflowOutcome = workflowResult.Outcome.ToString();
            var processed = workflowResult.WorkflowCompleted;

            await LogVoiceEventAsync(
                    TelemetryEventNames.VoiceEventProcessed,
                    correlationId,
                    tenantContext.TenantId,
                    parseResult.Envelope.RawEventType,
                    inboundCallEvent.EventType.ToString(),
                    workflowOutcome,
                    cancellationToken)
                .ConfigureAwait(false);

            await LogWebhookAsync(apiTelemetryEventName, correlationId, null, tenantContext.TenantId, "vapi", workflowOutcome, cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteJson(
                request,
                responseStatusCode,
                new
                {
                    accepted = true,
                    processed,
                    correlationId,
                    tenantId = tenantContext.TenantId,
                    eventType = inboundCallEvent.EventType.ToString(),
                    outcome = workflowOutcome
                },
                correlationId);
        }
        catch (ConfigurationException)
        {
            await LogWebhookAsync(TelemetryEventNames.TenantResolutionFailed, correlationId, tenantId, null, "vapi", "configuration_error", cancellationToken)
                .ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantId, null, "vapi", "bad_request", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
        catch (TenantResolutionException)
        {
            await LogWebhookAsync(TelemetryEventNames.TenantResolutionFailed, correlationId, tenantId, null, "vapi", "tenant_resolution_error", cancellationToken)
                .ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantId, null, "vapi", "bad_request", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
        catch (SecretRetrievalException)
        {
            await LogWebhookAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, tenantContext is null ? tenantId : null, tenantContext?.TenantId, "vapi", "secret_unavailable", cancellationToken)
                .ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantContext is null ? tenantId : null, tenantContext?.TenantId, "vapi", "unauthorized", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Unauthorized,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }
        catch
        {
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantContext is null ? tenantId : null, tenantContext?.TenantId, "vapi", "dependency_failure", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.InternalServerError,
                safeErrorResponseFactory.CreateInternalServerError(correlationId));
        }
    }

    private Task LogWebhookAsync(
        string eventName,
        string correlationId,
        string? routeTenantId,
        string? tenantId,
        string provider,
        string outcome,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", correlationId)
            .Add("endpoint", "webhooks/vapi/inbound")
            .Add("provider", provider)
            .Add("outcome", outcome)
            .AddIf(!string.IsNullOrWhiteSpace(routeTenantId), "routeTenantId", routeTenantId)
            .AddIf(!string.IsNullOrWhiteSpace(tenantId), "tenantId", tenantId)
            .ToDictionary();

        return eventLogger.TryLogEventAsync(eventName, properties, cancellationToken);
    }

    private Task LogVoiceEventAsync(
        string eventName,
        string correlationId,
        string tenantId,
        string providerEventType,
        string platformEventType,
        string outcome,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", correlationId)
            .Add("endpoint", "webhooks/vapi/inbound")
            .Add("provider", "vapi")
            .Add("tenantId", tenantId)
            .Add("providerEventType", providerEventType)
            .Add("platformEventType", platformEventType)
            .Add("outcome", outcome)
            .ToDictionary();

        return eventLogger.TryLogEventAsync(eventName, properties, cancellationToken);
    }
}
