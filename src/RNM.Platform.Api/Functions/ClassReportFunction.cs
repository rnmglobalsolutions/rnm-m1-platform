using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Classes;

namespace RNM.Platform.Api.Functions;

public sealed class ClassReportFunction
{
    private const string ApiKeyHeaderName = "x-rnm-api-key";
    private readonly ClassReportService classReportService;
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly RnmRuntimeConfiguration runtimeConfiguration;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;

    public ClassReportFunction(
        ClassReportService classReportService,
        ApiKeyRequestValidator apiKeyRequestValidator,
        RnmRuntimeConfiguration runtimeConfiguration,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory)
    {
        this.classReportService = classReportService;
        this.apiKeyRequestValidator = apiKeyRequestValidator;
        this.runtimeConfiguration = runtimeConfiguration;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
    }

    [Function("ClassReport")]
    public async Task<HttpResponseData> HandleAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = "tenants/{tenantId}/classes/{sessionId}/report")]
        HttpRequestData request,
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var correlationId = correlationContextFactory.FromRequest(request).Value;
        if (!request.Headers.TryGetValues(ApiKeyHeaderName, out var values)
            || !apiKeyRequestValidator.IsValid(values.FirstOrDefault(), runtimeConfiguration.InternalApiKey))
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Unauthorized,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }

        var result = await classReportService
            .GetReportAsync(
                new ClassReportRequest(tenantId, correlationId, sessionId),
                cancellationToken)
            .ConfigureAwait(false);
        return responseWriter.WriteJson(request, HttpStatusCode.OK, result, correlationId);
    }
}

