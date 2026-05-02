using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.SharedKernel.Correlation;

namespace RNM.Platform.Api.Functions;

public sealed class HealthFunction
{
    [Function("Health")]
    public HttpResponseData Handle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData request)
    {
        var correlationId = request.Headers.TryGetValues(CorrelationId.HeaderName, out var values)
            ? values.FirstOrDefault()
            : null;

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add(CorrelationId.HeaderName, correlationId);
        response.Headers.Add("Content-Type", "application/json");

        response.WriteString("""
        {
          "status": "healthy"
        }
        """);

        return response;
    }
}