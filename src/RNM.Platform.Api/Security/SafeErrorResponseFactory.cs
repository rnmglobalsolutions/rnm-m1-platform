using RNM.Platform.SharedKernel.Correlation;

namespace RNM.Platform.Api.Security;

public sealed class SafeErrorResponseFactory
{
    public SafeErrorResponse CreateUnauthorized(string correlationId)
    {
        return new SafeErrorResponse(
            "unauthorized",
            "The request is not authorized.",
            SafeCorrelationId(correlationId));
    }

    public SafeErrorResponse CreateBadRequest(string correlationId)
    {
        return new SafeErrorResponse(
            "bad_request",
            "The request is invalid.",
            SafeCorrelationId(correlationId));
    }

    public SafeErrorResponse CreateTenantViolation(string correlationId)
    {
        return new SafeErrorResponse(
            "tenant_violation",
            "The request is not allowed for this tenant.",
            SafeCorrelationId(correlationId));
    }

    public SafeErrorResponse CreateInternalServerError(string correlationId)
    {
        return new SafeErrorResponse(
            "internal_error",
            "The request could not be completed.",
            SafeCorrelationId(correlationId));
    }

    public SafeErrorResponse CreatePayloadTooLarge(string correlationId)
    {
        return new SafeErrorResponse(
            "payload_too_large",
            "The request payload is too large.",
            SafeCorrelationId(correlationId));
    }

    private static string SafeCorrelationId(string correlationId)
    {
        return CorrelationId.FromStringOrNew(correlationId).Value;
    }
}
