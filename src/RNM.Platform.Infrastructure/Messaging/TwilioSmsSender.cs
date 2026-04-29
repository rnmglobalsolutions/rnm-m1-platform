using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Infrastructure.Messaging;

public sealed class TwilioSmsSender : ISmsSender
{
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ISecretProvider secretProvider;
    private readonly HttpClient httpClient;

    public TwilioSmsSender(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISecretProvider secretProvider,
        HttpClient httpClient)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.secretProvider = secretProvider;
        this.httpClient = httpClient;
    }

    public async Task<SmsSendResult> SendSmsAsync(
        SmsMessageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenantConfiguration = await tenantConfigurationProvider
                .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
                .ConfigureAwait(false);

            var fromPhoneNumber = tenantConfiguration.Communication.SmsFromPhoneNumber;
            if (string.IsNullOrWhiteSpace(fromPhoneNumber))
            {
                return Failed();
            }

            var accountSid = await secretProvider
                .GetSecretAsync(tenantConfiguration.SecretNames.TwilioAccountSid, cancellationToken)
                .ConfigureAwait(false);
            var authToken = await secretProvider
                .GetSecretAsync(tenantConfiguration.SecretNames.TwilioAuthToken, cancellationToken)
                .ConfigureAwait(false);

            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("From", fromPhoneNumber),
                new KeyValuePair<string, string>("To", request.ToPhoneNumber),
                new KeyValuePair<string, string>("Body", request.Body)
            ]);
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                $"Accounts/{Uri.EscapeDataString(accountSid)}/Messages.json")
            {
                Content = content
            };

            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}"));
            message.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            using var response = await httpClient
                .SendAsync(message, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Failed();
            }

            var responseJson = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            return new SmsSendResult(true, TryReadMessageSid(responseJson));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Failed();
        }
    }

    private static SmsSendResult Failed() =>
        new(Succeeded: false);

    private static string? TryReadMessageSid(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            return document.RootElement.TryGetProperty("sid", out var sid)
                ? sid.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
