using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Classes;
using RNM.Platform.Application.Configuration;

namespace RNM.Platform.Api.Functions;

public sealed class ClassSessionFunction
{
    private const string ApiKeyHeaderName = "x-rnm-api-key";
    private const int MaxBodyBytes = 32768;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ClassRegistrationService classRegistrationService;
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly RnmRuntimeConfiguration runtimeConfiguration;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly LimitedRequestBodyReader requestBodyReader;

    public ClassSessionFunction(
        ClassRegistrationService classRegistrationService,
        ApiKeyRequestValidator apiKeyRequestValidator,
        RnmRuntimeConfiguration runtimeConfiguration,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory,
        LimitedRequestBodyReader requestBodyReader)
    {
        this.classRegistrationService = classRegistrationService;
        this.apiKeyRequestValidator = apiKeyRequestValidator;
        this.runtimeConfiguration = runtimeConfiguration;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
        this.requestBodyReader = requestBodyReader;
    }

    [Function("ClassSessionUpsert")]
    public async Task<HttpResponseData> UpsertAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "put",
            "post",
            Route = "tenants/{tenantId}/classes/sessions/{sessionId}")]
        HttpRequestData request,
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var correlationId = correlationContextFactory.FromRequest(request).Value;
        if (!IsAuthorized(request))
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Unauthorized,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }

        var body = await requestBodyReader.ReadAsStringAsync(request, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (body.IsTooLarge)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.RequestEntityTooLarge,
                safeErrorResponseFactory.CreatePayloadTooLarge(correlationId));
        }

        ClassSessionUpsertBody? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ClassSessionUpsertBody>(body.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }

        if (parsed is null)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }

        try
        {
            var result = await classRegistrationService
                .UpsertSessionAsync(
                    new ClassSessionUpsertRequest(
                        tenantId,
                        correlationId,
                        sessionId,
                        parsed.Title ?? string.Empty,
                        parsed.StartsAt,
                        parsed.TimeZone ?? string.Empty,
                        parsed.ZoomUrl ?? string.Empty)
                    {
                        Status = parsed.Status ?? ClassSessionStatuses.Published,
                        EndsAt = parsed.EndsAt,
                        Capacity = parsed.Capacity,
                        CampaignId = parsed.CampaignId,
                        Attributes = parsed.Attributes ?? new Dictionary<string, string>()
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteJson(
                request,
                result.Succeeded ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
                result,
                correlationId);
        }
        catch (ConfigurationException)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
    }

    private bool IsAuthorized(HttpRequestData request) =>
        request.Headers.TryGetValues(ApiKeyHeaderName, out var values)
        && apiKeyRequestValidator.IsValid(values.FirstOrDefault(), runtimeConfiguration.InternalApiKey);

    private sealed record ClassSessionUpsertBody(
        string? Title,
        DateTimeOffset StartsAt,
        DateTimeOffset? EndsAt,
        string? TimeZone,
        string? ZoomUrl,
        string? Status,
        int? Capacity,
        string? CampaignId,
        IReadOnlyDictionary<string, string>? Attributes);
}
