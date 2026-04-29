namespace RNM.Platform.Application.Tenancy;

public sealed class TenantResolutionException : Exception
{
    public TenantResolutionException(string message)
        : base(message)
    {
    }
}
