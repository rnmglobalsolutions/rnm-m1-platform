using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Classes;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.FollowUps;
using RNM.Platform.SharedKernel.Correlation;

namespace RNM.Platform.Api.Functions;

public sealed class ClassReminderFunction
{
    private const string ApiKeyHeaderName = "x-rnm-api-key";
    private const string ActiveTenantsEnvironmentVariable = "RNM_ACTIVE_TENANTS";
    private readonly ClassReminderService classReminderService;
    private readonly FollowUpRunService followUpRunService;
    private readonly ApiKeyRequestValidator apiKeyRequestValidator;
    private readonly RnmRuntimeConfiguration runtimeConfiguration;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;

    public ClassReminderFunction(
        ClassReminderService classReminderService,
        FollowUpRunService followUpRunService,
        ApiKeyRequestValidator apiKeyRequestValidator,
        RnmRuntimeConfiguration runtimeConfiguration,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory)
    {
        this.classReminderService = classReminderService;
        this.followUpRunService = followUpRunService;
        this.apiKeyRequestValidator = apiKeyRequestValidator;
        this.runtimeConfiguration = runtimeConfiguration;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
    }

    [Function("FollowUpManualRun")]
    public async Task<HttpResponseData> RunFollowUpsManualAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            Route = "tenants/{tenantId}/followups/run")]
        HttpRequestData request,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var correlationId = correlationContextFactory.FromRequest(request).Value;
        if (!IsAuthorized(request))
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Unauthorized,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }

        try
        {
            var query = ParseQuery(request.Url.Query);
            var maxItems = int.TryParse(query.GetValueOrDefault("maxItems"), out var parsedMaxItems)
                ? parsedMaxItems
                : 25;
            var dueAt = DateTimeOffset.TryParse(query.GetValueOrDefault("dueAt"), out var parsedDueAt)
                ? parsedDueAt
                : DateTimeOffset.UtcNow;
            var result = await followUpRunService
                .RunAsync(
                    new FollowUpRunRequest(tenantId, correlationId, dueAt)
                    {
                        MaxItems = maxItems
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return responseWriter.WriteJson(request, HttpStatusCode.OK, result, correlationId);
        }
        catch (ConfigurationException)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
    }

    [Function("ClassReminderManualRun")]
    public async Task<HttpResponseData> RunManualAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            Route = "tenants/{tenantId}/classes/reminders/run")]
        HttpRequestData request,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var correlationId = correlationContextFactory.FromRequest(request).Value;
        if (!IsAuthorized(request))
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Unauthorized,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }

        try
        {
            var query = ParseQuery(request.Url.Query);
            var maxItems = int.TryParse(query.GetValueOrDefault("maxItems"), out var parsedMaxItems)
                ? parsedMaxItems
                : 25;
            var dueAt = DateTimeOffset.TryParse(query.GetValueOrDefault("dueAt"), out var parsedDueAt)
                ? parsedDueAt
                : DateTimeOffset.UtcNow;
            var result = await classReminderService
                .RunAsync(
                    new ClassReminderRunRequest(tenantId, correlationId, dueAt)
                    {
                        MaxItems = maxItems
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return responseWriter.WriteJson(request, HttpStatusCode.OK, result, correlationId);
        }
        catch (ConfigurationException)
        {
            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
    }

    [Function("ClassReminderTimerRun")]
    public async Task RunTimerAsync(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        var tenants = GetActiveTenants();
        foreach (var tenantId in tenants)
        {
            var correlationId = CorrelationId.New().Value;
            try
            {
                await classReminderService
                    .RunAsync(
                        new ClassReminderRunRequest(tenantId, correlationId, DateTimeOffset.UtcNow),
                        cancellationToken)
                    .ConfigureAwait(false);
                await followUpRunService
                    .RunAsync(
                        new FollowUpRunRequest(tenantId, correlationId, DateTimeOffset.UtcNow),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ConfigurationException)
            {
                // A tenant without class config should not stop other tenants.
            }
        }
    }

    private bool IsAuthorized(HttpRequestData request) =>
        request.Headers.TryGetValues(ApiKeyHeaderName, out var values)
        && apiKeyRequestValidator.IsValid(values.FirstOrDefault(), runtimeConfiguration.InternalApiKey);

    private static IReadOnlyCollection<string> GetActiveTenants()
    {
        var raw = Environment.GetEnvironmentVariable(ActiveTenantsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new Dictionary<string, string>();
        }

        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);
    }
}
