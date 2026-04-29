using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Observability;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Observability;
using RNM.Platform.Infrastructure.Secrets;
using RNM.Platform.SharedKernel.Correlation;

namespace RNM.Platform.Api.Functions;

public sealed class HealthFunction
{
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly ISecretProvider secretProvider;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly IEventLogger eventLogger;

    public HealthFunction(
        ApiKeyRequestValidator apiKeyRequestValidator,
        ISecretProvider secretProvider,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory,
        IEventLogger eventLogger)
    {
        this.apiKeyRequestValidator = apiKeyRequestValidator;
        this.secretProvider = secretProvider;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
        this.eventLogger = eventLogger;
    }

    [Function("Health")]
    public async Task<HttpResponseData> Handle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var correlationContext = correlationContextFactory.FromRequest(request);
        var correlationId = correlationContext.Value;

        try
        {
            var secretName = Environment.GetEnvironmentVariable(ApiSecretNames.InternalApiKeySecretNameSetting);
            if (string.IsNullOrWhiteSpace(secretName))
            {
                await LogAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, "health", "missing_api_key_setting", cancellationToken)
                    .ConfigureAwait(false);
                await LogAsync(TelemetryEventNames.ApiRequestFailed, correlationId, "health", "unauthorized", cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.Unauthorized,
                    safeErrorResponseFactory.CreateUnauthorized(correlationId));
            }

            var expectedApiKey = await secretProvider.GetSecretAsync(secretName, cancellationToken).ConfigureAwait(false);
            var providedApiKey = request.GetHeaderValue("x-rnm-api-key");
            if (!apiKeyRequestValidator.IsValid(providedApiKey, expectedApiKey))
            {
                await LogAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, "health", "invalid_api_key", cancellationToken)
                    .ConfigureAwait(false);
                await LogAsync(TelemetryEventNames.ApiRequestFailed, correlationId, "health", "unauthorized", cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.Unauthorized,
                    safeErrorResponseFactory.CreateUnauthorized(correlationId));
            }

            var response = request.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add(CorrelationId.HeaderName, correlationId);
            response.WriteString("healthy");

            await LogAsync(TelemetryEventNames.ApiRequestCompleted, correlationId, "health", "ok", cancellationToken)
                .ConfigureAwait(false);

            return response;
        }
        catch (SecretRetrievalException)
        {
            await LogAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, "health", "secret_unavailable", cancellationToken)
                .ConfigureAwait(false);
            await LogAsync(TelemetryEventNames.ApiRequestFailed, correlationId, "health", "unauthorized", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Unauthorized,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }
        catch
        {
            await LogAsync(TelemetryEventNames.ApiRequestFailed, correlationId, "health", "dependency_failure", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.InternalServerError,
                safeErrorResponseFactory.CreateInternalServerError(correlationId));
        }
    }

    private Task LogAsync(
        string eventName,
        string correlationId,
        string endpoint,
        string outcome,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", correlationId)
            .Add("endpoint", endpoint)
            .Add("outcome", outcome)
            .ToDictionary();

        return eventLogger.TryLogEventAsync(eventName, properties, cancellationToken);
    }
}
