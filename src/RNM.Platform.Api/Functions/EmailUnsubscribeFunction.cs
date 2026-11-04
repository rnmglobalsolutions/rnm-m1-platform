using System.Net;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.EmailPreferences;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Functions;

/// <summary>
/// Public, signed unsubscribe link for tenant marketing email. POST is the RFC 8058 one-click request that
/// mail clients send; GET shows a confirmation page so link scanners cannot unsubscribe anyone.
/// </summary>
public sealed class EmailUnsubscribeFunction
{
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ISecretProvider secretProvider;
    private readonly EmailPreferenceService emailPreferences;
    private readonly CorrelationContextFactory correlationContextFactory;

    public EmailUnsubscribeFunction(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISecretProvider secretProvider,
        EmailPreferenceService emailPreferences,
        CorrelationContextFactory correlationContextFactory)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.secretProvider = secretProvider;
        this.emailPreferences = emailPreferences;
        this.correlationContextFactory = correlationContextFactory;
    }

    [Function("EmailUnsubscribe")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "tenants/{tenantId}/email/unsubscribe")] HttpRequestData request,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var correlationId = correlationContextFactory.FromRequest(request).Value;
        var query = HttpUtility.ParseQueryString(request.Url.Query);
        var email = EmailUnsubscribeToken.DecodeEmail(query["e"]);
        var token = query["t"];

        string businessName;
        string signingKey;
        try
        {
            businessName = (await tenantConfigurationProvider.GetTenantConfigurationAsync(tenantId, cancellationToken).ConfigureAwait(false)).BusinessName;
            signingKey = await secretProvider.GetSecretAsync(EmailUnsubscribeToken.SigningKeySecretName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Html(request, HttpStatusCode.NotFound, "Enlace no válido", "Este enlace de baja no es válido. / This unsubscribe link is not valid.");
        }

        if (email is null || !EmailUnsubscribeToken.IsValid(signingKey, tenantId, email, token))
        {
            return Html(request, HttpStatusCode.BadRequest, "Enlace no válido", "Este enlace de baja no es válido. / This unsubscribe link is not valid.");
        }

        var business = HttpUtility.HtmlEncode(businessName);
        if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return Html(
                request,
                HttpStatusCode.OK,
                "Darse de baja",
                $"<p>¿Dejar de recibir emails de seguimiento de {business}? / Stop receiving follow-up emails from {business}?</p>"
                + "<form method=\"post\"><button type=\"submit\">Confirmar baja / Unsubscribe</button></form>",
                encodeBody: false);
        }

        var succeeded = await emailPreferences
            .UnsubscribeAsync(tenantId, correlationId, email, "UnsubscribeLink", "one_click", cancellationToken)
            .ConfigureAwait(false);
        return succeeded
            ? Html(request, HttpStatusCode.OK, "Baja confirmada", $"No recibirás más emails de seguimiento de {business}. / You will no longer receive follow-up emails from {business}.", encodeBody: false)
            : Html(request, HttpStatusCode.ServiceUnavailable, "Intenta de nuevo", "No pudimos procesar tu baja. Intenta de nuevo en unos minutos. / We could not process your request. Please try again shortly.");
    }

    private static HttpResponseData Html(
        HttpRequestData request,
        HttpStatusCode statusCode,
        string title,
        string body,
        bool encodeBody = true)
    {
        var response = request.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");
        response.Headers.Add("X-Robots-Tag", "noindex");
        var content = encodeBody ? $"<p>{HttpUtility.HtmlEncode(body)}</p>" : body;
        response.WriteString(
            $"<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>{HttpUtility.HtmlEncode(title)}</title></head>"
            + $"<body style=\"font-family:system-ui,sans-serif;max-width:32rem;margin:3rem auto;padding:0 1rem\"><h1>{HttpUtility.HtmlEncode(title)}</h1>{content}</body></html>");
        return response;
    }
}
