namespace RNM.Platform.Infrastructure.Secrets;

public sealed class EnvironmentSecretProvider : ISecretProvider
{
    public Task<string> GetSecretAsync(string secretName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            throw new SecretRetrievalException("Secret name is required.");
        }

        var secretValue = Environment.GetEnvironmentVariable(secretName);
        if (string.IsNullOrWhiteSpace(secretValue))
        {
            throw new SecretRetrievalException("Secret was not found.");
        }

        return Task.FromResult(secretValue);
    }
}
