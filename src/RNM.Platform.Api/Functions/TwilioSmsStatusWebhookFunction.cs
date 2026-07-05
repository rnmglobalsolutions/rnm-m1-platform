using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Observability;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Tenancy;
using RNM.Platform.Contracts.Webhooks;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Functions;

public sealed class TwilioSmsStatusWebhookFunction
{
    private readonly TenantResolver tenantResolver;
    private readonly TwilioSignatureValidator twilioSignatureValidator;
    private readonly ISecretProvider secretProvider;
    private readonly FormUrlEncodedBodyParser formUrlEncodedBodyParser;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly IEventLogger eventLogger;
    private readonly ICrmAdapter crmAdapter;

    public TwilioSmsStatusWebhookFunction(
        TenantResolver tenantResolver,
        TwilioSignatureValidator twilioSignatureValidator,
        ISecretProvider secretProvider,
        FormUrlEncodedBodyParser formUrlEncodedBodyParser,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory,
        IEventLogger eventLogger,
        ICrmAdapter crmAdapter)
    {
        this.tenantResolver = tenantResolver;
        this.twilioSignatureValidator = twilioSignatureValidator;
        this.secretProvider = secretProvider;
        this.formUrlEncodedBodyParser = formUrlEncodedBodyParser;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
        this.eventLogger = eventLogger;
        this.crmAdapter = crmAdapter;
    }

    [Function("TwilioSmsStatusWebhook")]
    public async Task<HttpResponseData> Handle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tenants/{tenantId}/webhooks/twilio/sms-status")] HttpRequestData request,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var correlationContext = correlationContextFactory.FromRequest(request);
        var correlationId = correlationContext.Value;
        TenantContext? tenantContext = null;

        try
        {
            await LogWebhookAsync(TelemetryEventNames.WebhookReceived, correlationId, tenantId, null, "twilio", "received", cancellationToken)
                .ConfigureAwait(false);

            tenantContext = await tenantResolver.ResolveAsync(tenantId, cancellationToken).ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.TenantResolved, correlationId, null, tenantContext.TenantId, "twilio", "resolved", cancellationToken)
                .ConfigureAwait(false);

            var twilioAuthToken = await secretProvider
                .GetSecretAsync(tenantContext.SecretNames.TwilioAuthToken, cancellationToken)
                .ConfigureAwait(false);
            var rawBody = await request.ReadBodyAsStringAsync().ConfigureAwait(false);
            var formValues = formUrlEncodedBodyParser.Parse(rawBody);

            var isValid = twilioSignatureValidator.IsValid(
                request.GetHeaderValue("X-Twilio-Signature"),
                request.Url,
                formValues,
                twilioAuthToken);

            if (!isValid)
            {
                await LogWebhookAsync(TelemetryEventNames.WebhookValidationFailed, correlationId, null, tenantContext.TenantId, "twilio", "invalid_signature", cancellationToken)
                    .ConfigureAwait(false);
                await LogWebhookAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, null, tenantContext.TenantId, "twilio", "invalid_signature", cancellationToken)
                    .ConfigureAwait(false);
                await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, null, tenantContext.TenantId, "twilio", "unauthorized", cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.Unauthorized,
                    safeErrorResponseFactory.CreateUnauthorized(correlationId));
            }

            await LogWebhookAsync(TelemetryEventNames.WebhookValidationSucceeded, correlationId, null, tenantContext.TenantId, "twilio", "valid", cancellationToken)
                .ConfigureAwait(false);

            var webhook = CreateStatusWebhook(tenantContext.TenantId, formValues);
            if (webhook is null)
            {
                await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, null, tenantContext.TenantId, "twilio", "invalid_payload", cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.BadRequest,
                    safeErrorResponseFactory.CreateBadRequest(correlationId));
            }

            await HandleAsync(webhook, cancellationToken).ConfigureAwait(false);

            await LogWebhookAsync(TelemetryEventNames.ApiRequestCompleted, correlationId, null, tenantContext.TenantId, "twilio", "accepted", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteJson(
                request,
                HttpStatusCode.Accepted,
                new { accepted = true, correlationId, tenantId = tenantContext.TenantId },
                correlationId);
        }
        catch (ConfigurationException)
        {
            await LogWebhookAsync(TelemetryEventNames.TenantResolutionFailed, correlationId, tenantId, null, "twilio", "configuration_error", cancellationToken)
                .ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantId, null, "twilio", "bad_request", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
        catch (TenantResolutionException)
        {
            await LogWebhookAsync(TelemetryEventNames.TenantResolutionFailed, correlationId, tenantId, null, "twilio", "tenant_resolution_error", cancellationToken)
                .ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantId, null, "twilio", "bad_request", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
        catch (SecretRetrievalException)
        {
            await LogWebhookAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, tenantContext is null ? tenantId : null, tenantContext?.TenantId, "twilio", "secret_unavailable", cancellationToken)
                .ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantContext is null ? tenantId : null, tenantContext?.TenantId, "twilio", "unauthorized", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Unauthorized,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }
        catch
        {
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantContext is null ? tenantId : null, tenantContext?.TenantId, "twilio", "dependency_failure", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.InternalServerError,
                safeErrorResponseFactory.CreateInternalServerError(correlationId));
        }
    }

    public Task HandleAsync(
        TwilioSmsStatusWebhook request,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("tenantId", request.TenantId)
            .Add("provider", "twilio")
            .Add("messageSid", request.MessageSid)
            .Add("messageStatus", request.MessageStatus)
            .AddIf(!string.IsNullOrWhiteSpace(request.ErrorCode), "errorCode", request.ErrorCode)
            .ToDictionary();

        return eventLogger.TryLogEventAsync(TelemetryEventNames.SmsStatusReceived, properties, cancellationToken);
    }

    [Function("TwilioSmsInboundWebhook")]
    public async Task<HttpResponseData> HandleInbound(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tenants/{tenantId}/webhooks/twilio/sms-inbound")] HttpRequestData request,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var correlationContext = correlationContextFactory.FromRequest(request);
        var correlationId = correlationContext.Value;
        TenantContext? tenantContext = null;

        try
        {
            await LogWebhookAsync(TelemetryEventNames.WebhookReceived, correlationId, tenantId, null, "twilio", "received", cancellationToken)
                .ConfigureAwait(false);

            tenantContext = await tenantResolver.ResolveAsync(tenantId, cancellationToken).ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.TenantResolved, correlationId, null, tenantContext.TenantId, "twilio", "resolved", cancellationToken)
                .ConfigureAwait(false);

            var twilioAuthToken = await secretProvider
                .GetSecretAsync(tenantContext.SecretNames.TwilioAuthToken, cancellationToken)
                .ConfigureAwait(false);
            var rawBody = await request.ReadBodyAsStringAsync().ConfigureAwait(false);
            var formValues = formUrlEncodedBodyParser.Parse(rawBody);

            var isValid = twilioSignatureValidator.IsValid(
                request.GetHeaderValue("X-Twilio-Signature"),
                request.Url,
                formValues,
                twilioAuthToken);
            if (!isValid)
            {
                await LogWebhookAsync(TelemetryEventNames.WebhookValidationFailed, correlationId, null, tenantContext.TenantId, "twilio", "invalid_signature", cancellationToken)
                    .ConfigureAwait(false);
                await LogWebhookAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, null, tenantContext.TenantId, "twilio", "invalid_signature", cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.Unauthorized,
                    safeErrorResponseFactory.CreateUnauthorized(correlationId));
            }

            await LogWebhookAsync(TelemetryEventNames.WebhookValidationSucceeded, correlationId, null, tenantContext.TenantId, "twilio", "valid", cancellationToken)
                .ConfigureAwait(false);

            var values = ToDictionary(formValues);
            var body = GetValue(values, "Body");
            if (IsOptOutKeyword(body))
            {
                var optOutResult = await crmAdapter.MarkOptOutAsync(
                        new CrmOptOutRequest(
                            tenantContext.TenantId,
                            correlationId,
                            ProviderContactId: null)
                        {
                            PhoneNumber = GetValue(values, "From"),
                            Source = "TwilioSmsInbound",
                            Reason = body
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!optOutResult.Succeeded)
                {
                    await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, null, tenantContext.TenantId, "twilio", "opt_out_persistence_failed", cancellationToken)
                        .ConfigureAwait(false);

                    return responseWriter.WriteSafeError(
                        request,
                        HttpStatusCode.InternalServerError,
                        safeErrorResponseFactory.CreateInternalServerError(correlationId));
                }
            }

            await LogWebhookAsync(TelemetryEventNames.ApiRequestCompleted, correlationId, null, tenantContext.TenantId, "twilio", "accepted", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteJson(
                request,
                HttpStatusCode.Accepted,
                new { accepted = true, correlationId, tenantId = tenantContext.TenantId },
                correlationId);
        }
        catch (ConfigurationException)
        {
            await LogWebhookAsync(TelemetryEventNames.TenantResolutionFailed, correlationId, tenantId, null, "twilio", "configuration_error", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
        catch (TenantResolutionException)
        {
            await LogWebhookAsync(TelemetryEventNames.TenantResolutionFailed, correlationId, tenantId, null, "twilio", "tenant_resolution_error", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
        catch (SecretRetrievalException)
        {
            await LogWebhookAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, tenantContext is null ? tenantId : null, tenantContext?.TenantId, "twilio", "secret_unavailable", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Unauthorized,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }
        catch
        {
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantContext is null ? tenantId : null, tenantContext?.TenantId, "twilio", "dependency_failure", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.InternalServerError,
                safeErrorResponseFactory.CreateInternalServerError(correlationId));
        }
    }

    private static TwilioSmsStatusWebhook? CreateStatusWebhook(
        string tenantId,
        IReadOnlyCollection<KeyValuePair<string, string>> formValues)
    {
        var values = ToDictionary(formValues);

        var messageSid = GetValue(values, "MessageSid") ?? GetValue(values, "SmsSid");
        var messageStatus = GetValue(values, "MessageStatus") ?? GetValue(values, "SmsStatus");
        if (string.IsNullOrWhiteSpace(messageSid) || string.IsNullOrWhiteSpace(messageStatus))
        {
            return null;
        }

        return new TwilioSmsStatusWebhook(
            tenantId,
            messageSid,
            messageStatus,
            GetValue(values, "ErrorCode"),
            GetValue(values, "To"),
            GetValue(values, "From"));
    }

    private static string? GetValue(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        return values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static IReadOnlyDictionary<string, string> ToDictionary(
        IReadOnlyCollection<KeyValuePair<string, string>> formValues)
    {
        return formValues
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsOptOutKeyword(string? body)
    {
        var normalized = body?.Trim();
        return string.Equals(normalized, "STOP", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "STOPALL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "UNSUBSCRIBE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "CANCEL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "END", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "QUIT", StringComparison.OrdinalIgnoreCase);
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
            .Add("endpoint", "webhooks/twilio/sms-status")
            .Add("provider", provider)
            .Add("outcome", outcome)
            .AddIf(!string.IsNullOrWhiteSpace(routeTenantId), "routeTenantId", routeTenantId)
            .AddIf(!string.IsNullOrWhiteSpace(tenantId), "tenantId", tenantId)
            .ToDictionary();

        return eventLogger.TryLogEventAsync(eventName, properties, cancellationToken);
    }
}
