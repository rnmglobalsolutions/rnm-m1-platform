namespace RNM.Platform.Infrastructure.Secrets;

public sealed class SecretRetrievalException : Exception
{
    public SecretRetrievalException(string message)
        : base(message)
    {
    }

    public SecretRetrievalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
