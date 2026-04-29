using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.SharedKernel.Correlation;

namespace RNM.Platform.Api.Http;

public sealed class CorrelationContextFactory
{
    public CorrelationContext FromRequest(HttpRequestData request)
    {
        return CorrelationContext.FromStringOrNew(request.GetHeaderValue(CorrelationId.HeaderName));
    }
}
