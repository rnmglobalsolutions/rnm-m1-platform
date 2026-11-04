using RNM.Platform.Application.EmailPreferences;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Infrastructure.Messaging;

public sealed class EmailUnsubscribeLinkFactory : IEmailUnsubscribeLinkFactory
{
    public const string PublicBaseUrlEnvironmentVariable = "RNM_PUBLIC_BASE_URL";
    private readonly ISecretProvider secretProvider;

    public EmailUnsubscribeLinkFactory(ISecretProvider secretProvider)
    {
        this.secretProvider = secretProvider;
    }

    public async Task<Uri?> CreateAsync(string tenantId, string email, CancellationToken cancellationToken)
    {
        var baseUrl = ResolvePublicBaseUrl();
        if (baseUrl is null)
        {
            return null;
        }

        string signingKey;
        try
        {
            signingKey = await secretProvider
                .GetSecretAsync(EmailUnsubscribeToken.SigningKeySecretName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(signingKey))
        {
            return null;
        }

        var query = $"e={EmailUnsubscribeToken.EncodeEmail(email)}&t={EmailUnsubscribeToken.Create(signingKey, tenantId, email)}";
        return new Uri($"{baseUrl}/api/tenants/{Uri.EscapeDataString(tenantId)}/email/unsubscribe?{query}");
    }

    /// <summary>
    /// Explicit RNM_PUBLIC_BASE_URL wins; otherwise the Function App host that Azure sets in WEBSITE_HOSTNAME.
    /// </summary>
    private static string? ResolvePublicBaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable(PublicBaseUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured)
            && Uri.TryCreate(configured.Trim().TrimEnd('/'), UriKind.Absolute, out var configuredUri)
            && configuredUri.Scheme == Uri.UriSchemeHttps)
        {
            return configuredUri.ToString().TrimEnd('/');
        }

        var host = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
        return string.IsNullOrWhiteSpace(host) ? null : $"https://{host.Trim()}";
    }
}
