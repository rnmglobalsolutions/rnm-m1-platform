using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Infrastructure.Messaging;

public sealed class AzureCommunicationEmailSender : IEmailSender
{
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ISecretProvider secretProvider;

    public AzureCommunicationEmailSender(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISecretProvider secretProvider)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.secretProvider = secretProvider;
    }

    public async Task<EmailSendResult> SendEmailAsync(
        EmailMessageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenantConfiguration = await tenantConfigurationProvider
                .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(tenantConfiguration.Communication.EmailFromAddress))
            {
                return Failed();
            }

            await secretProvider
                .GetSecretAsync(tenantConfiguration.SecretNames.EmailConnectionString, cancellationToken)
                .ConfigureAwait(false);

            // Email is optional in M1. The provider transport stays isolated here until
            // Azure Communication Services delivery is enabled for a tenant.
            return Failed();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Failed();
        }
    }

    private static EmailSendResult Failed() =>
        new(Succeeded: false);
}
