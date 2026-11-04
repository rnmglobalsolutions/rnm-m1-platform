namespace RNM.Platform.Application.Ports.Messaging;

public interface IEmailUnsubscribeLinkFactory
{
    /// <summary>
    /// Returns the one-click unsubscribe link for this tenant and address, or null when the platform cannot
    /// issue one (missing signing key or public base URL). Callers must not send marketing email without it.
    /// </summary>
    Task<Uri?> CreateAsync(string tenantId, string email, CancellationToken cancellationToken);
}
