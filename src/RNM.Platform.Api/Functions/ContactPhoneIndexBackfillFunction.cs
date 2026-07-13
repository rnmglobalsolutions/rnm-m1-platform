using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Ports.Crm;

namespace RNM.Platform.Api.Functions;

public sealed class ContactPhoneIndexBackfillFunction
{
    private const string ApiKeyHeaderName = "x-rnm-api-key";
    private readonly IContactPhoneIndexBackfillAdapter phoneIndexBackfillAdapter;
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly RnmRuntimeConfiguration runtimeConfiguration;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;

    public ContactPhoneIndexBackfillFunction(
        IContactPhoneIndexBackfillAdapter phoneIndexBackfillAdapter,
        ApiKeyRequestValidator apiKeyRequestValidator,
        RnmRuntimeConfiguration runtimeConfiguration,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory)
    {
        this.phoneIndexBackfillAdapter = phoneIndexBackfillAdapter;
        this.apiKeyRequestValidator = apiKeyRequestValidator;
        this.runtimeConfiguration = runtimeConfiguration;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
    }

    [Function("ContactPhoneIndexBackfill")]
    public async Task<HttpResponseData> HandleAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            Route = "tenants/{tenantId}/crm/phone-index/backfill")]
        HttpRequestData request,
        string tenantId,
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
            var result = await phoneIndexBackfillAdapter
                .BackfillPhoneIndexAsync(
                    new CrmContactPhoneIndexBackfillRequest(tenantId, correlationId),
                    cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteJson(request, HttpStatusCode.OK, result, correlationId);
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
