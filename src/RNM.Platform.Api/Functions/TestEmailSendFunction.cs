using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Functions;

public sealed class TestEmailSendFunction
{
    private const int MaxBodyBytes = 8192;
    private const string ApiKeyHeaderName = "x-rnm-api-key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IEmailSender emailSender;
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly ISecretProvider secretProvider;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly LimitedRequestBodyReader requestBodyReader;

    public TestEmailSendFunction(
        IEmailSender emailSender,
        ApiKeyRequestValidator apiKeyRequestValidator,
        ISecretProvider secretProvider,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory,
        LimitedRequestBodyReader requestBodyReader)
    {
        this.emailSender = emailSender;
        this.apiKeyRequestValidator = apiKeyRequestValidator;
        this.secretProvider = secretProvider;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
        this.requestBodyReader = requestBodyReader;
    }

    [Function("TestEmailSend")]
    public async Task<HttpResponseData> Handle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "test/email/send")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var correlationId = correlationContextFactory.FromRequest(request).Value;

        if (!IsEndpointEnabled())
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.NotFound,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }

        var isAuthorized = await IsAuthorizedAsync(request, cancellationToken).ConfigureAwait(false);
        if (!isAuthorized)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Unauthorized,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }

        var bodyReadResult = await requestBodyReader
            .ReadAsStringAsync(request, MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);
        if (bodyReadResult.IsTooLarge)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.RequestEntityTooLarge,
                safeErrorResponseFactory.CreatePayloadTooLarge(correlationId));
        }

        var testRequest = ParseRequest(bodyReadResult.Body);
        if (testRequest is null)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }

        var sendResult = await emailSender
            .SendEmailAsync(
                new EmailMessageRequest(
                    GetTenantId(),
                    correlationId,
                    testRequest.ToEmail,
                    testRequest.Subject,
                    testRequest.Body),
                cancellationToken)
            .ConfigureAwait(false);

        var statusCode = sendResult.Succeeded
            ? HttpStatusCode.OK
            : HttpStatusCode.BadGateway;

        return responseWriter.WriteJson(
            request,
            statusCode,
            new
            {
                sent = sendResult.Succeeded,
                providerMessageId = sendResult.ProviderMessageId,
                failureReason = sendResult.Succeeded ? null : SafeFailureReason(sendResult.Message),
                correlationId
            },
            correlationId);
    }

    private async Task<bool> IsAuthorizedAsync(
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var secretName = Environment.GetEnvironmentVariable(ApiSecretNames.InternalApiKeySecretNameSetting);
        if (string.IsNullOrWhiteSpace(secretName))
        {
            return false;
        }

        try
        {
            var expectedApiKey = await secretProvider
                .GetSecretAsync(secretName, cancellationToken)
                .ConfigureAwait(false);

            return apiKeyRequestValidator.IsValid(request.GetHeaderValue(ApiKeyHeaderName), expectedApiKey);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static TestEmailSendRequest? ParseRequest(string body)
    {
        try
        {
            var request = JsonSerializer.Deserialize<TestEmailSendRequest>(body, JsonOptions);
            return request is null
                || string.IsNullOrWhiteSpace(request.ToEmail)
                || string.IsNullOrWhiteSpace(request.Subject)
                || string.IsNullOrWhiteSpace(request.Body)
                    ? null
                    : request;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsEndpointEnabled()
    {
        var environmentName = Environment.GetEnvironmentVariable("RNM_ENVIRONMENT") ?? "Development";
        var isProduction = string.Equals(environmentName, "prod", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentName, "production", StringComparison.OrdinalIgnoreCase);

        return !isProduction || string.Equals(
            Environment.GetEnvironmentVariable("RNM_ENABLE_TEST_EMAIL_ENDPOINT"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTenantId() =>
        Environment.GetEnvironmentVariable("RNM_TEST_EMAIL_TENANT_ID") ?? "sample-hvac-tenant";

    private static string SafeFailureReason(string? providerMessage) =>
        providerMessage switch
        {
            "missing_api_key" => "missing_api_key",
            "missing_sender" => "missing_sender",
            "provider_failure" => "provider_failure",
            "provider_exception" => "provider_exception",
            _ => "email_send_failed"
        };

    private sealed record TestEmailSendRequest(
        string ToEmail,
        string Subject,
        string Body);
}
