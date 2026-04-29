using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace RNM.Platform.Infrastructure.Secrets;

public sealed class KeyVaultSecretProvider : ISecretProvider
{
    private readonly SecretClient secretClient;

    public KeyVaultSecretProvider(Uri keyVaultUri)
        : this(keyVaultUri, new DefaultAzureCredential())
    {
    }

    public KeyVaultSecretProvider(Uri keyVaultUri, TokenCredential credential)
        : this(new SecretClient(keyVaultUri, credential))
    {
    }

    public KeyVaultSecretProvider(SecretClient secretClient)
    {
        this.secretClient = secretClient;
    }

    public async Task<string> GetSecretAsync(
        string secretName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            throw new SecretRetrievalException("Secret name is required.");
        }

        try
        {
            var secret = await secretClient
                .GetSecretAsync(secretName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(secret.Value.Value))
            {
                throw new SecretRetrievalException("Secret was empty.");
            }

            return secret.Value.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SecretRetrievalException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SecretRetrievalException("Secret could not be retrieved.", exception);
        }
    }
}
