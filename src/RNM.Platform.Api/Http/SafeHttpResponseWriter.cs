using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.SharedKernel.Correlation;

namespace RNM.Platform.Api.Http;

public sealed class SafeHttpResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public HttpResponseData WriteJson(
        HttpRequestData request,
        HttpStatusCode statusCode,
        object body,
        string correlationId)
    {
        var safeCorrelationId = CorrelationId.FromStringOrNew(correlationId).Value;
        var response = request.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json");
        response.Headers.Add(CorrelationId.HeaderName, safeCorrelationId);
        response.WriteString(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }

    public HttpResponseData WriteSafeError(
        HttpRequestData request,
        HttpStatusCode statusCode,
        SafeErrorResponse body)
    {
        return WriteJson(request, statusCode, body, body.CorrelationId);
    }
}
