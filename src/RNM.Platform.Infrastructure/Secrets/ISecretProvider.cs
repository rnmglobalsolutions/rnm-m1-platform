namespace RNM.Platform.Infrastructure.Secrets;

public interface ISecretProvider
{
    Task<string> GetSecretAsync(string secretName, CancellationToken cancellationToken);
}
