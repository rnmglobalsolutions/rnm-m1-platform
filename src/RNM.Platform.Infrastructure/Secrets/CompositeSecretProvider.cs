namespace RNM.Platform.Infrastructure.Secrets;

public sealed class CompositeSecretProvider : ISecretProvider
{
    private readonly ISecretProvider primary;
    private readonly ISecretProvider fallback;

    public CompositeSecretProvider(
        ISecretProvider primary,
        ISecretProvider fallback)
    {
        this.primary = primary;
        this.fallback = fallback;
    }

    public async Task<string> GetSecretAsync(
        string secretName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await primary.GetSecretAsync(secretName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SecretRetrievalException)
        {
            return await fallback.GetSecretAsync(secretName, cancellationToken).ConfigureAwait(false);
        }
    }
}
