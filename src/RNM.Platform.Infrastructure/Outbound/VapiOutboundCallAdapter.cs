using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Outbound;
using RNM.Platform.Application.Ports.Outbound;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Infrastructure.Outbound;

public sealed class VapiOutboundCallAdapter : IOutboundCallAdapter
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromSeconds(30);
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ISecretProvider secretProvider;
    private readonly HttpClient httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly object circuitLock = new();
    private readonly Dictionary<string, CircuitState> circuitStates = new(StringComparer.Ordinal);

    public VapiOutboundCallAdapter(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISecretProvider secretProvider,
        HttpClient httpClient)
        : this(tenantConfigurationProvider, secretProvider, httpClient, Task.Delay)
    {
    }

    internal VapiOutboundCallAdapter(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISecretProvider secretProvider,
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.secretProvider = secretProvider;
        this.httpClient = httpClient;
        this.delay = delay;
    }

    public async Task<OutboundCallStartResult> StartCallAsync(
        OutboundCallStartRequest request,
        CancellationToken cancellationToken)
    {
        if (IsCircuitOpen(request.TenantId))
        {
            return Failed(OutboundCallFailureReason.CircuitOpen, retryable: true);
        }

        if (string.IsNullOrWhiteSpace(request.Contact.PhoneNumber))
        {
            return Failed(OutboundCallFailureReason.MissingPhoneNumber, retryable: false);
        }

        if (string.Equals(request.Contact.ConsentStatus, "opted_out", StringComparison.OrdinalIgnoreCase))
        {
            return Failed(OutboundCallFailureReason.ConsentOptedOut, retryable: false);
        }

        var tenantConfiguration = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(request.TenantId, cancellationToken)
            .ConfigureAwait(false);
        var outbound = tenantConfiguration.Voice?.Outbound;
        if (string.IsNullOrWhiteSpace(outbound?.VapiApiKeySecretName)
            || string.IsNullOrWhiteSpace(outbound.VapiBaseUrl))
        {
            return Failed(OutboundCallFailureReason.MissingConfiguration, retryable: false);
        }

        var apiKey = await secretProvider
            .GetSecretAsync(outbound.VapiApiKeySecretName, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Failed(OutboundCallFailureReason.MissingConfiguration, retryable: false);
        }

        var callUri = new Uri(new Uri(outbound.VapiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute), "call");
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(AttemptTimeout);
                using var httpRequest = BuildHttpRequest(request, callUri, apiKey);
                using var response = await httpClient
                    .SendAsync(httpRequest, timeout.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    MarkCircuitSuccess(request.TenantId);
                    return await ToSuccessResultAsync(response, timeout.Token).ConfigureAwait(false);
                }

                if (!IsRetryable(response.StatusCode) || attempt == MaxAttempts)
                {
                    MarkCircuitFailure(request.TenantId);
                    return Failed(OutboundCallFailureReason.ProviderFailure, retryable: IsRetryable(response.StatusCode));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == MaxAttempts)
                {
                    MarkCircuitFailure(request.TenantId);
                    return Failed(OutboundCallFailureReason.ProviderFailure, retryable: true);
                }
            }
            catch (HttpRequestException)
            {
                if (attempt == MaxAttempts)
                {
                    MarkCircuitFailure(request.TenantId);
                    return Failed(OutboundCallFailureReason.ProviderFailure, retryable: true);
                }
            }

            await delay(BackoffForAttempt(attempt), cancellationToken).ConfigureAwait(false);
        }

        MarkCircuitFailure(request.TenantId);
        return Failed(OutboundCallFailureReason.AdapterFailure, retryable: true);
    }

    private static HttpRequestMessage BuildHttpRequest(
        OutboundCallStartRequest request,
        Uri callUri,
        string apiKey)
    {
        var payload = new
        {
            assistantId = request.AssistantId,
            phoneNumberId = request.PhoneNumberId,
            customer = new
            {
                number = request.Contact.PhoneNumber,
                name = request.Contact.Name
            },
            server = new
            {
                url = request.CallbackWebhookUrl
            },
            metadata = new
            {
                tenantId = request.TenantId,
                providerContactId = request.Contact.ProviderContactId,
                correlationId = request.CorrelationId
            }
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, callUri)
        {
            Content = JsonContent.Create(payload)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return httpRequest;
    }

    private static async Task<OutboundCallStartResult> ToSuccessResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var providerCallId = TryGetString(root, "id") ?? TryGetString(root, "callId");
            var status = TryGetString(root, "status") ?? response.StatusCode.ToString();
            return new OutboundCallStartResult(true, providerCallId, status);
        }
        catch (JsonException)
        {
            return new OutboundCallStartResult(true, null, response.StatusCode.ToString());
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind is JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static OutboundCallStartResult Failed(
        OutboundCallFailureReason reason,
        bool retryable)
    {
        return new OutboundCallStartResult(
            false,
            null,
            null,
            reason,
            retryable,
            "Outbound call could not be started.");
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan BackoffForAttempt(int attempt) =>
        attempt == 1 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMilliseconds(250);

    private bool IsCircuitOpen(string tenantId)
    {
        lock (circuitLock)
        {
            return circuitStates.TryGetValue(tenantId, out var state)
                && state.CircuitOpenUntil > DateTimeOffset.UtcNow;
        }
    }

    private void MarkCircuitSuccess(string tenantId)
    {
        lock (circuitLock)
        {
            circuitStates.Remove(tenantId);
        }
    }

    private void MarkCircuitFailure(string tenantId)
    {
        lock (circuitLock)
        {
            var state = circuitStates.TryGetValue(tenantId, out var current)
                ? current
                : new CircuitState();
            state.ConsecutiveFailures++;
            if (state.ConsecutiveFailures >= MaxAttempts)
            {
                state.CircuitOpenUntil = DateTimeOffset.UtcNow.Add(CircuitOpenDuration);
            }

            circuitStates[tenantId] = state;
        }
    }

    private sealed class CircuitState
    {
        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset CircuitOpenUntil { get; set; } = DateTimeOffset.MinValue;
    }
}
