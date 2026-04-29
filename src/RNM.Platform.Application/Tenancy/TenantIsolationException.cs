namespace RNM.Platform.Application.Tenancy;

public sealed class TenantIsolationException : Exception
{
    public TenantIsolationException(string message)
        : base(message)
    {
    }
}
