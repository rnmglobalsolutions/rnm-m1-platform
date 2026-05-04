using System.Net;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Ports.Messaging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace RNM.Platform.Infrastructure.Messaging;

public sealed class SendGridEmailSender : IEmailSender
{
    private const string ApiKeyEnvironmentVariableName = "SENDGRID_API_KEY";
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ISendGridEmailTransport transport;

    public SendGridEmailSender(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISendGridEmailTransport transport)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.transport = transport;
    }

    public async Task<EmailSendResult> SendEmailAsync(
        EmailMessageRequest request,
        CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
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
                return Failed("missing_sender");
            }

            var message = new SendGridEmailMessage(
                FromEmail: fromEmail,
                FromName: tenantConfiguration.BusinessName,
                ToEmail: request.ToEmail,
                Subject: request.Subject,
                Body: request.Body);

            var response = await transport
                .SendAsync(apiKey, message, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccess
                ? new EmailSendResult(Succeeded: true, ProviderMessageId: response.ProviderMessageId)
                : Failed("provider_failure");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Failed("provider_exception");
        }
    }

    private static EmailSendResult Failed(string reason) =>
        new(Succeeded: false, Message: reason);
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
    string? ProviderMessageId = null);

public sealed class SendGridEmailTransport : ISendGridEmailTransport
{
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
        var isSuccess = (int)response.StatusCode >= 200 && (int)response.StatusCode <= 299;

        return new SendGridEmailSendResponse(isSuccess, providerMessageId);
    }

    private static string? TryGetProviderMessageId(Response response)
    {
        return response.Headers.TryGetValues("X-Message-Id", out var values)
            ? values.FirstOrDefault()
            : null;
    }
}
