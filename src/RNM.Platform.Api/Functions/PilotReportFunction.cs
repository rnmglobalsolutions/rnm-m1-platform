using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Reporting;

namespace RNM.Platform.Api.Functions;

public sealed class PilotReportFunction
{
    private const string ApiKeyHeaderName = "x-rnm-api-key";
    private readonly PilotReportingService reportingService;
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly RnmRuntimeConfiguration runtimeConfiguration;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;

    public PilotReportFunction(
        PilotReportingService reportingService,
        ApiKeyRequestValidator apiKeyRequestValidator,
        RnmRuntimeConfiguration runtimeConfiguration,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory)
    {
        this.reportingService = reportingService;
        this.apiKeyRequestValidator = apiKeyRequestValidator;
        this.runtimeConfiguration = runtimeConfiguration;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
    }

    [Function("PilotReport")]
    public async Task<HttpResponseData> HandleAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = "tenants/{tenantId}/reports/pilot")]
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

        var query = ParseQuery(request.Url.Query);
        if (!DateTimeOffset.TryParse(query.GetValueOrDefault("from"), out var from)
            || !DateTimeOffset.TryParse(query.GetValueOrDefault("to"), out var to)
            || to < from)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }

        PilotReportResult report;
        try
        {
            report = await reportingService
                .GetPilotReportAsync(
                    new PilotReportRequest(
                        tenantId,
                        correlationId,
                        from,
                        to),
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

        return responseWriter.WriteJson(request, HttpStatusCode.OK, report, correlationId);
    }

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
}
