using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Outbound;

namespace RNM.Platform.Api.Functions;

public sealed class OutboundCampaignRunFunction
{
    private const string ApiKeyHeaderName = "x-rnm-api-key";
    private readonly OutboundCampaignRunService outboundCampaignRunService;
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly RnmRuntimeConfiguration runtimeConfiguration;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;

    public OutboundCampaignRunFunction(
        OutboundCampaignRunService outboundCampaignRunService,
        ApiKeyRequestValidator apiKeyRequestValidator,
        RnmRuntimeConfiguration runtimeConfiguration,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory)
    {
        this.outboundCampaignRunService = outboundCampaignRunService;
        this.apiKeyRequestValidator = apiKeyRequestValidator;
        this.runtimeConfiguration = runtimeConfiguration;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
    }

    [Function("OutboundCampaignRun")]
    public async Task<HttpResponseData> HandleAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            Route = "tenants/{tenantId}/outbound/campaigns/{campaignId}/run")]
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

        OutboundCampaignRunResult result;
        try
        {
            result = await outboundCampaignRunService
                .RunAsync(
                    new OutboundCampaignRunRequest(tenantId, campaignId, correlationId),
                    cancellationToken)
                .ConfigureAwait(false);
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

        if (!result.Succeeded)
        {
            return responseWriter.WriteJson(request, HttpStatusCode.BadRequest, result, correlationId);
        }

        return responseWriter.WriteJson(request, HttpStatusCode.Accepted, result, correlationId);
    }
}
