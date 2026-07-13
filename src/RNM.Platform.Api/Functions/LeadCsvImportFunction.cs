using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.LeadImport;

namespace RNM.Platform.Api.Functions;

public sealed class LeadCsvImportFunction
{
    private const string ApiKeyHeaderName = "x-rnm-api-key";
    private const int MaxCsvBodyBytes = 2 * 1024 * 1024;
    private readonly LeadCsvImportService leadCsvImportService;
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly RnmRuntimeConfiguration runtimeConfiguration;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly LimitedRequestBodyReader requestBodyReader;

    public LeadCsvImportFunction(
        LeadCsvImportService leadCsvImportService,
        ApiKeyRequestValidator apiKeyRequestValidator,
        RnmRuntimeConfiguration runtimeConfiguration,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory,
        LimitedRequestBodyReader requestBodyReader)
    {
        this.leadCsvImportService = leadCsvImportService;
        this.apiKeyRequestValidator = apiKeyRequestValidator;
        this.runtimeConfiguration = runtimeConfiguration;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
        this.requestBodyReader = requestBodyReader;
    }

    [Function("LeadCsvImport")]
    public async Task<HttpResponseData> HandleAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            Route = "tenants/{tenantId}/crm/campaigns/{campaignId}/leads/import-csv")]
        HttpRequestData request,
        string tenantId,
        string campaignId,
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

        try
        {
            var bodyReadResult = await requestBodyReader
                .ReadAsStringAsync(request, MaxCsvBodyBytes, cancellationToken)
                .ConfigureAwait(false);
            if (bodyReadResult.IsTooLarge)
            {
                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.RequestEntityTooLarge,
                    safeErrorResponseFactory.CreatePayloadTooLarge(correlationId));
            }

            var result = await leadCsvImportService
                .ImportAsync(
                    new LeadCsvImportRequest(
                        tenantId,
                        campaignId,
                        correlationId,
                        bodyReadResult.Body),
                    cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteJson(request, HttpStatusCode.OK, result, correlationId);
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
}
