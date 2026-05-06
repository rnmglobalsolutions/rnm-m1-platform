using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Observability;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.SharedKernel.Correlation;

namespace RNM.Platform.Api.Functions;

public sealed class ContactSystemReviewFunction
{
    private const int MaxBodyBytes = 16384;
    private const string EndpointName = "contact/system-review";
    private const string ContactTenantIdEnvironmentVariable = "RNM_CONTACT_TENANT_ID";
    private const string DefaultContactTenantId = "sample-hvac-tenant";
    private const string NotificationEmail = "info@rnmglobalsolutions.com";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        "https://www.rnmglobalsolutions.com",
        "https://rnmglobalsolutions.com"
    };

    private readonly IEmailSender emailSender;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly IEventLogger eventLogger;
    private readonly LimitedRequestBodyReader requestBodyReader;

    public ContactSystemReviewFunction(
        IEmailSender emailSender,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory,
        IEventLogger eventLogger,
        LimitedRequestBodyReader requestBodyReader)
    {
        this.emailSender = emailSender;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
        this.eventLogger = eventLogger;
        this.requestBodyReader = requestBodyReader;
    }

    [Function("ContactSystemReviewFunction")]
    public async Task<HttpResponseData> Handle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options", Route = "contact/system-review")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var correlationId = correlationContextFactory.FromRequest(request).Value;
        var allowedOrigin = GetAllowedOrigin(request);

        if (string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return WritePreflightResponse(request, correlationId, allowedOrigin);
        }

        if (allowedOrigin is null)
        {
            await LogAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, "origin_rejected", "origin", cancellationToken)
                .ConfigureAwait(false);

            var forbidden = responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Forbidden,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
            AddCorsHeaders(forbidden, allowedOrigin);
            return forbidden;
        }

        var bodyReadResult = await requestBodyReader
            .ReadAsStringAsync(request, MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);
        if (bodyReadResult.IsTooLarge)
        {
            await LogAsync(TelemetryEventNames.ApiRequestFailed, correlationId, "payload_too_large", "payload_too_large", cancellationToken)
                .ConfigureAwait(false);

            var tooLarge = responseWriter.WriteSafeError(
                request,
                HttpStatusCode.RequestEntityTooLarge,
                safeErrorResponseFactory.CreatePayloadTooLarge(correlationId));
            AddCorsHeaders(tooLarge, allowedOrigin);
            return tooLarge;
        }

        var parseResult = ParseRequest(bodyReadResult.Body);
        if (parseResult.Request is null)
        {
            await LogAsync(TelemetryEventNames.ApiRequestFailed, correlationId, "validation_failed", parseResult.ValidationResult, cancellationToken)
                .ConfigureAwait(false);

            return WriteValidationFailure(request, correlationId, parseResult.ValidationResult, allowedOrigin);
        }

        var contactRequest = parseResult.Request;
        if (!string.IsNullOrWhiteSpace(contactRequest.CompanyWebsiteConfirm))
        {
            await LogAsync(TelemetryEventNames.ApiRequestCompleted, correlationId, "honeypot_success", "honeypot", cancellationToken)
                .ConfigureAwait(false);

            return WriteReceived(request, correlationId, allowedOrigin);
        }

        var notificationResult = await emailSender
            .SendEmailAsync(CreateNotificationEmail(contactRequest, correlationId), cancellationToken)
            .ConfigureAwait(false);
        if (!notificationResult.Succeeded)
        {
            await LogAsync(TelemetryEventNames.ApiRequestFailed, correlationId, "notification_failed", "email_provider", cancellationToken)
                .ConfigureAwait(false);

            return WriteJson(
                request,
                HttpStatusCode.InternalServerError,
                new
                {
                    received = false,
                    code = "notification_failed",
                    correlationId
                },
                correlationId,
                allowedOrigin);
        }

        var confirmationResult = await emailSender
            .SendEmailAsync(CreateConfirmationEmail(contactRequest, correlationId), cancellationToken)
            .ConfigureAwait(false);
        var outcome = confirmationResult.Succeeded
            ? "received_with_confirmation"
            : "received_confirmation_failed";

        await LogAsync(TelemetryEventNames.ApiRequestCompleted, correlationId, outcome, "valid", cancellationToken)
            .ConfigureAwait(false);

        return WriteReceived(request, correlationId, allowedOrigin);
    }

    private static string? GetAllowedOrigin(HttpRequestData request)
    {
        var origin = request.GetHeaderValue("Origin");
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        var normalizedOrigin = origin.Trim().TrimEnd('/');
        return AllowedOrigins.Contains(normalizedOrigin)
            ? normalizedOrigin
            : null;
    }

    private static ContactParseResult ParseRequest(string body)
    {
        ContactSystemReviewRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ContactSystemReviewRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return ContactParseResult.Invalid("malformed_json");
        }

        if (request is null)
        {
            return ContactParseResult.Invalid("malformed_json");
        }

        var normalized = request.Normalize();
        if (string.IsNullOrWhiteSpace(normalized.FullName))
        {
            return ContactParseResult.Invalid("missing_full_name");
        }

        if (string.IsNullOrWhiteSpace(normalized.Email))
        {
            return ContactParseResult.Invalid("missing_email");
        }

        if (!IsValidEmail(normalized.Email))
        {
            return ContactParseResult.Invalid("invalid_email");
        }

        if (string.IsNullOrWhiteSpace(normalized.WorkflowNeedsImprovement))
        {
            return ContactParseResult.Invalid("missing_workflow_needs_improvement");
        }

        return HasInvalidLength(normalized, out var validationResult)
            ? ContactParseResult.Invalid(validationResult)
            : ContactParseResult.Valid(normalized);
    }

    private static bool HasInvalidLength(
        ContactSystemReviewRequest request,
        out string validationResult)
    {
        validationResult = string.Empty;
        if (request.FullName.Length > 120)
        {
            validationResult = "full_name_too_long";
            return true;
        }

        if (request.Email.Length > 254)
        {
            validationResult = "email_too_long";
            return true;
        }

        if (request.Phone.Length > 40)
        {
            validationResult = "phone_too_long";
            return true;
        }

        if (request.PreferredChannels.Length > 200)
        {
            validationResult = "preferred_channels_too_long";
            return true;
        }

        if (request.CurrentTools.Length > 1000)
        {
            validationResult = "current_tools_too_long";
            return true;
        }

        if (request.WorkflowNeedsImprovement.Length > 4000)
        {
            validationResult = "workflow_needs_improvement_too_long";
            return true;
        }

        if (request.Website.Length > 500)
        {
            validationResult = "website_too_long";
            return true;
        }

        if (request.CompanyWebsiteConfirm.Length > 500)
        {
            validationResult = "company_website_confirm_too_long";
            return true;
        }

        return false;
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var parsed = new MailAddress(email);
            return string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static EmailMessageRequest CreateNotificationEmail(
        ContactSystemReviewRequest request,
        string correlationId)
    {
        return new EmailMessageRequest(
            TenantId(),
            correlationId,
            NotificationEmail,
            $"New system review request from {request.FullName}",
            BuildNotificationBody(request, correlationId));
    }

    private static EmailMessageRequest CreateConfirmationEmail(
        ContactSystemReviewRequest request,
        string correlationId)
    {
        return new EmailMessageRequest(
            TenantId(),
            correlationId,
            request.Email,
            "We received your system review request",
            $"""
            Hi {request.FullName},

            Thank you for contacting RNM Global Solutions. We received your system review request and will follow up shortly.

            Thank you,
            RNM Global Solutions
            """);
    }

    private static string BuildNotificationBody(
        ContactSystemReviewRequest request,
        string correlationId)
    {
        var builder = new StringBuilder();
        builder.AppendLine("New system review request");
        builder.AppendLine();
        builder.AppendLine($"Name: {request.FullName}");
        builder.AppendLine($"Email: {request.Email}");
        builder.AppendLine($"Phone: {request.Phone}");
        builder.AppendLine($"Preferred Channels: {request.PreferredChannels}");
        builder.AppendLine($"Current Tools: {request.CurrentTools}");
        builder.AppendLine($"Website: {request.Website}");
        builder.AppendLine("Message / Workflow Needs Improvement:");
        builder.AppendLine(request.WorkflowNeedsImprovement);
        builder.AppendLine();
        builder.AppendLine($"Correlation ID: {correlationId}");
        return builder.ToString();
    }

    private HttpResponseData WriteValidationFailure(
        HttpRequestData request,
        string correlationId,
        string validationResult,
        string allowedOrigin)
    {
        return WriteJson(
            request,
            HttpStatusCode.BadRequest,
            new
            {
                received = false,
                code = "validation_failed",
                validationResult,
                correlationId
            },
            correlationId,
            allowedOrigin);
    }

    private HttpResponseData WriteReceived(
        HttpRequestData request,
        string correlationId,
        string allowedOrigin)
    {
        return WriteJson(
            request,
            HttpStatusCode.OK,
            new
            {
                received = true,
                correlationId
            },
            correlationId,
            allowedOrigin);
    }

    private HttpResponseData WriteJson(
        HttpRequestData request,
        HttpStatusCode statusCode,
        object body,
        string correlationId,
        string allowedOrigin)
    {
        var response = responseWriter.WriteJson(request, statusCode, body, correlationId);
        AddCorsHeaders(response, allowedOrigin);
        return response;
    }

    private HttpResponseData WritePreflightResponse(
        HttpRequestData request,
        string correlationId,
        string? allowedOrigin)
    {
        if (allowedOrigin is null)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Forbidden,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }

        var response = request.CreateResponse(HttpStatusCode.NoContent);
        response.Headers.Add(CorrelationId.HeaderName, correlationId);
        AddCorsHeaders(response, allowedOrigin);
        response.Headers.Add("Access-Control-Max-Age", "3600");
        return response;
    }

    private static void AddCorsHeaders(
        HttpResponseData response,
        string? allowedOrigin)
    {
        if (allowedOrigin is null)
        {
            return;
        }

        response.Headers.Add("Access-Control-Allow-Origin", allowedOrigin);
        response.Headers.Add("Vary", "Origin");
        response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, x-correlation-id");
    }

    private Task LogAsync(
        string eventName,
        string correlationId,
        string outcome,
        string validationResult,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("endpoint", EndpointName)
            .Add("outcome", outcome)
            .Add("correlationId", correlationId)
            .Add("validationResult", validationResult)
            .ToDictionary();

        return eventLogger.TryLogEventAsync(eventName, properties, cancellationToken);
    }

    private static string TenantId() =>
        Environment.GetEnvironmentVariable(ContactTenantIdEnvironmentVariable) ?? DefaultContactTenantId;

    private sealed record ContactParseResult(
        ContactSystemReviewRequest? Request,
        string ValidationResult)
    {
        public static ContactParseResult Valid(ContactSystemReviewRequest request) =>
            new(request, "valid");

        public static ContactParseResult Invalid(string validationResult) =>
            new(null, validationResult);
    }

    private sealed record ContactSystemReviewRequest(
        string FullName,
        string Email,
        string Phone,
        string PreferredChannels,
        string CurrentTools,
        string WorkflowNeedsImprovement,
        string Website,
        string CompanyWebsiteConfirm)
    {
        public ContactSystemReviewRequest Normalize()
        {
            return new ContactSystemReviewRequest(
                (FullName ?? string.Empty).Trim(),
                (Email ?? string.Empty).Trim(),
                (Phone ?? string.Empty).Trim(),
                (PreferredChannels ?? string.Empty).Trim(),
                (CurrentTools ?? string.Empty).Trim(),
                (WorkflowNeedsImprovement ?? string.Empty).Trim(),
                (Website ?? string.Empty).Trim(),
                (CompanyWebsiteConfirm ?? string.Empty).Trim());
        }
    }
}
