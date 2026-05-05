using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Ports.Messaging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace RNM.Platform.Infrastructure.Messaging;

public sealed partial class SendGridEmailSender : IEmailSender
{
    private const string ApiKeyEnvironmentVariableName = "SENDGRID_API_KEY";
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ISendGridEmailTransport transport;
    private readonly ILogger<SendGridEmailSender> logger;

    public SendGridEmailSender(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISendGridEmailTransport transport,
        ILogger<SendGridEmailSender> logger)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.transport = transport;
        this.logger = logger;
    }

    public async Task<EmailSendResult> SendEmailAsync(
        EmailMessageRequest request,
        CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning(
                "SendGrid email skipped because SENDGRID_API_KEY is missing. TenantId={TenantId} CorrelationId={CorrelationId}",
                request.TenantId,
                request.CorrelationId);
            return Failed("missing_api_key");
        }

        try
        {
            var tenantConfiguration = await tenantConfigurationProvider
                .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
                .ConfigureAwait(false);

            var fromEmail = tenantConfiguration.Communication.EmailFromAddress;
            if (string.IsNullOrWhiteSpace(fromEmail))
            {
                logger.LogWarning(
                    "SendGrid email skipped because sender email is missing. TenantId={TenantId} CorrelationId={CorrelationId}",
                    request.TenantId,
                    request.CorrelationId);
                return Failed("missing_sender");
            }

            var message = new SendGridEmailMessage(
                FromEmail: fromEmail,
                FromName: tenantConfiguration.BusinessName,
                ToEmail: request.ToEmail,
                Subject: request.Subject,
                Body: request.Body);

            logger.LogInformation(
                "Sending SendGrid email. TenantId={TenantId} CorrelationId={CorrelationId} FromDomain={FromDomain} ToDomain={ToDomain} SubjectLength={SubjectLength} BodyLength={BodyLength}",
                request.TenantId,
                request.CorrelationId,
                EmailDomain(fromEmail),
                EmailDomain(request.ToEmail),
                request.Subject.Length,
                request.Body.Length);

            var response = await transport
                .SendAsync(apiKey, message, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccess)
            {
                logger.LogInformation(
                    "SendGrid email accepted. TenantId={TenantId} CorrelationId={CorrelationId} StatusCode={StatusCode} ProviderMessageId={ProviderMessageId}",
                    request.TenantId,
                    request.CorrelationId,
                    response.StatusCode,
                    response.ProviderMessageId);

                return new EmailSendResult(Succeeded: true, ProviderMessageId: response.ProviderMessageId);
            }

            logger.LogWarning(
                "SendGrid email rejected. TenantId={TenantId} CorrelationId={CorrelationId} StatusCode={StatusCode} ProviderMessageId={ProviderMessageId} ProviderError={ProviderError}",
                request.TenantId,
                request.CorrelationId,
                response.StatusCode,
                response.ProviderMessageId,
                SanitizeForLog(response.ErrorBody));

            return Failed("provider_failure");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "SendGrid email failed with exception. TenantId={TenantId} CorrelationId={CorrelationId} ExceptionType={ExceptionType} Error={Error}",
                request.TenantId,
                request.CorrelationId,
                exception.GetType().Name,
                SanitizeForLog(exception.Message));
            return Failed("provider_exception");
        }
    }

    private static EmailSendResult Failed(string reason) =>
        new(Succeeded: false, Message: reason);

    private static string EmailDomain(string email)
    {
        var atIndex = email.LastIndexOf('@');
        return atIndex >= 0 && atIndex < email.Length - 1
            ? email[(atIndex + 1)..]
            : "unknown";
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = EmailAddressPattern().Replace(value, "[redacted-email]");
        sanitized = BearerTokenPattern().Replace(sanitized, "Bearer [redacted]");
        sanitized = ApiKeyPattern().Replace(sanitized, "$1[redacted]");
        return sanitized.Length <= 2048
            ? sanitized
            : sanitized[..2048];
    }

    [GeneratedRegex(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailAddressPattern();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"(?i)(api[_\-\s]?key[""'\s:=]+)[^,""'\s}]+")]
    private static partial Regex ApiKeyPattern();
}

public interface ISendGridEmailTransport
{
    Task<SendGridEmailSendResponse> SendAsync(
        string apiKey,
        SendGridEmailMessage message,
        CancellationToken cancellationToken);
}

public sealed record SendGridEmailMessage(
    string FromEmail,
    string? FromName,
    string ToEmail,
    string Subject,
    string Body);

public sealed record SendGridEmailSendResponse(
    bool IsSuccess,
    string? ProviderMessageId = null,
    int StatusCode = 0,
    string? ErrorBody = null);

public sealed class SendGridEmailTransport : ISendGridEmailTransport
{
    private readonly ILogger<SendGridEmailTransport> logger;

    public SendGridEmailTransport(ILogger<SendGridEmailTransport> logger)
    {
        this.logger = logger;
    }

    public async Task<SendGridEmailSendResponse> SendAsync(
        string apiKey,
        SendGridEmailMessage message,
        CancellationToken cancellationToken)
    {
        var client = new SendGridClient(apiKey);
        var sendGridMessage = new SendGridMessage
        {
            From = new EmailAddress(message.FromEmail, message.FromName),
            Subject = message.Subject,
            PlainTextContent = message.Body
        };
        sendGridMessage.AddTo(new EmailAddress(message.ToEmail));

        var response = await client
            .SendEmailAsync(sendGridMessage, cancellationToken)
            .ConfigureAwait(false);
        var providerMessageId = TryGetProviderMessageId(response);
        var statusCode = (int)response.StatusCode;
        var isSuccess = statusCode >= 200 && statusCode <= 299;
        var body = isSuccess
            ? null
            : await response.Body.ReadAsStringAsync().ConfigureAwait(false);

        logger.LogInformation(
            "SendGrid API response received. StatusCode={StatusCode} IsSuccess={IsSuccess} ProviderMessageId={ProviderMessageId}",
            statusCode,
            isSuccess,
            providerMessageId);

        return new SendGridEmailSendResponse(isSuccess, providerMessageId, statusCode, body);
    }

    private static string? TryGetProviderMessageId(Response response)
    {
        return response.Headers.TryGetValues("X-Message-Id", out var values)
            ? values.FirstOrDefault()
            : null;
    }
}
