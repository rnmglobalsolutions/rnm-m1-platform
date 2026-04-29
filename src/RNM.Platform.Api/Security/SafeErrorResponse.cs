namespace RNM.Platform.Api.Security;

public sealed record SafeErrorResponse(
    string Code,
    string Message,
    string CorrelationId);
