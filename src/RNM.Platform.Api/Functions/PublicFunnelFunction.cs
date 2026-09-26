using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Classes;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.LeadIntake;
using RNM.Platform.Application.Observability;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Api.Functions;

public sealed class PublicFunnelFunction
{
    private const int MaxBodyBytes = 32768;
    private const int MaxRequestsPerMinute = 60;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, RateLimitCounter> RateLimits = new(StringComparer.Ordinal);
    private static readonly string[] DefaultAllowedOrigins =
    {
        "https://www.rnmglobalsolutions.com",
        "https://rnmglobalsolutions.com"
    };

    private readonly InboundLeadIntakeService intakeService;
    private readonly ClassRegistrationService classRegistrationService;
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly LimitedRequestBodyReader bodyReader;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly SafeErrorResponseFactory errorFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly IEventLogger eventLogger;

    public PublicFunnelFunction(
        InboundLeadIntakeService intakeService,
        ClassRegistrationService classRegistrationService,
        ITenantConfigurationProvider tenantConfigurationProvider,
        LimitedRequestBodyReader bodyReader,
        CorrelationContextFactory correlationContextFactory,
        SafeErrorResponseFactory errorFactory,
        SafeHttpResponseWriter responseWriter,
        IEventLogger eventLogger)
    {
        this.intakeService = intakeService;
        this.classRegistrationService = classRegistrationService;
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.bodyReader = bodyReader;
        this.correlationContextFactory = correlationContextFactory;
        this.errorFactory = errorFactory;
        this.responseWriter = responseWriter;
        this.eventLogger = eventLogger;
    }

    [Function("PublicFunnelConsultationRequest")]
    public async Task<HttpResponseData> ConsultationAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            "options",
            Route = "tenants/{tenantId}/funnels/consultation")]
        HttpRequestData request,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var correlationId = correlationContextFactory.FromRequest(request).Value;
        var tenantResult = await LoadTenantAsync(tenantId, correlationId, cancellationToken).ConfigureAwait(false);
        if (tenantResult.Tenant is null)
        {
            return responseWriter.WriteSafeError(request, tenantResult.StatusCode, tenantResult.Error!);
        }

        var allowedOrigin = GetAllowedOrigin(request, tenantResult.Tenant);
        if (IsOptions(request))
        {
            return WritePreflight(request, allowedOrigin, correlationId);
        }

        if (allowedOrigin is null)
        {
            await LogAsync("consultation", tenantId, correlationId, "origin_rejected", cancellationToken).ConfigureAwait(false);
            return responseWriter.WriteSafeError(request, HttpStatusCode.Forbidden, errorFactory.CreateUnauthorized(correlationId));
        }

        if (!AllowRequest($"{tenantId}:consultation", MaxRequestsPerMinute))
        {
            var rateLimited = responseWriter.WriteSafeError(request, HttpStatusCode.TooManyRequests, errorFactory.CreateRateLimited(correlationId));
            AddCorsHeaders(rateLimited, allowedOrigin);
            return rateLimited;
        }

        var body = await bodyReader.ReadAsStringAsync(request, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (body.IsTooLarge)
        {
            var tooLarge = responseWriter.WriteSafeError(request, HttpStatusCode.RequestEntityTooLarge, errorFactory.CreatePayloadTooLarge(correlationId));
            AddCorsHeaders(tooLarge, allowedOrigin);
            return tooLarge;
        }

        var parsed = ParseConsultation(body.Body);
        if (parsed.Body is null)
        {
            return WriteValidationFailure(request, allowedOrigin, correlationId, parsed.ValidationResult);
        }

        if (!string.IsNullOrWhiteSpace(parsed.Body.CompanyWebsiteConfirm))
        {
            await LogAsync("consultation", tenantId, correlationId, "honeypot_success", cancellationToken).ConfigureAwait(false);
            return WriteAccepted(request, allowedOrigin, correlationId, route: "none");
        }

        var consentCapturedAt = DateTimeOffset.UtcNow;
        var smsConsent = CreateConsentCapture(parsed.Body, "consentSms", parsed.Body.ConsentSms, consentCapturedAt);
        var emailConsent = CreateConsentCapture(parsed.Body, "consentEmail", parsed.Body.ConsentEmail, consentCapturedAt);
        var attributes = BuildConsultationAttributes(parsed.Body);
        var result = await intakeService.ProcessAsync(
                new InboundLeadIntakeRequest(
                    tenantId,
                    tenantResult.Tenant.VerticalId.Value,
                    correlationId,
                    "WebFunnel",
                    $"consultation-{parsed.Body.SubmissionId}",
                    $"web-{parsed.Body.SubmissionId}",
                    parsed.Body.CustomerName,
                    parsed.Body.CustomerPhoneNumber,
                    parsed.Body.CustomerEmail,
                    parsed.Body.CampaignId ?? "web-consultation",
                    parsed.Body.ConsentSms,
                    parsed.Body.ConsentSms ? consentCapturedAt : null,
                    parsed.Body.ConsentSms ? parsed.Body.ConsentTextVersion : null,
                    attributes,
                    ScheduleFollowUp: true)
                {
                    SmsConsent = smsConsent,
                    EmailConsent = emailConsent
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            await LogAsync("consultation", tenantId, correlationId, result.FailureCode ?? "failed", cancellationToken).ConfigureAwait(false);
            var failed = responseWriter.WriteSafeError(
                request,
                IsDependencyFailure(result.FailureCode) ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest,
                IsDependencyFailure(result.FailureCode)
                    ? errorFactory.CreateServiceUnavailable(correlationId)
                    : errorFactory.CreateBadRequest(correlationId));
            AddCorsHeaders(failed, allowedOrigin);
            return failed;
        }

        await LogAsync("consultation", tenantId, correlationId, "accepted", cancellationToken).ConfigureAwait(false);
        var response = responseWriter.WriteJson(
            request,
            result.Processing ? HttpStatusCode.Accepted : HttpStatusCode.OK,
            new
            {
                received = true,
                leadClassification = result.LeadClassification,
                recommendedRoute = result.RecommendedRoute,
                classificationReasons = result.ClassificationReasons,
                correlationId
            },
            correlationId);
        AddCorsHeaders(response, allowedOrigin);
        return response;
    }

    [Function("PublicFunnelMasterClassRegistration")]
    public async Task<HttpResponseData> MasterClassRegistrationAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            "options",
            Route = "tenants/{tenantId}/funnels/masterclass/{sessionId}/registrations")]
        HttpRequestData request,
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var correlationId = correlationContextFactory.FromRequest(request).Value;
        var tenantResult = await LoadTenantAsync(tenantId, correlationId, cancellationToken).ConfigureAwait(false);
        if (tenantResult.Tenant is null)
        {
            return responseWriter.WriteSafeError(request, tenantResult.StatusCode, tenantResult.Error!);
        }

        var allowedOrigin = GetAllowedOrigin(request, tenantResult.Tenant);
        if (IsOptions(request))
        {
            return WritePreflight(request, allowedOrigin, correlationId);
        }

        if (allowedOrigin is null)
        {
            await LogAsync("masterclass", tenantId, correlationId, "origin_rejected", cancellationToken).ConfigureAwait(false);
            return responseWriter.WriteSafeError(request, HttpStatusCode.Forbidden, errorFactory.CreateUnauthorized(correlationId));
        }

        if (!AllowRequest($"{tenantId}:masterclass", tenantResult.Tenant.Classes?.EffectiveMaxRegistrationsPerMinute ?? MaxRequestsPerMinute))
        {
            var rateLimited = responseWriter.WriteSafeError(request, HttpStatusCode.TooManyRequests, errorFactory.CreateRateLimited(correlationId));
            AddCorsHeaders(rateLimited, allowedOrigin);
            return rateLimited;
        }

        var body = await bodyReader.ReadAsStringAsync(request, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (body.IsTooLarge)
        {
            var tooLarge = responseWriter.WriteSafeError(request, HttpStatusCode.RequestEntityTooLarge, errorFactory.CreatePayloadTooLarge(correlationId));
            AddCorsHeaders(tooLarge, allowedOrigin);
            return tooLarge;
        }

        var parsed = ParseMasterClassRegistration(body.Body);
        if (parsed.Body is null)
        {
            return WriteValidationFailure(request, allowedOrigin, correlationId, parsed.ValidationResult);
        }

        if (!string.IsNullOrWhiteSpace(parsed.Body.CompanyWebsiteConfirm))
        {
            await LogAsync("masterclass", tenantId, correlationId, "honeypot_success", cancellationToken).ConfigureAwait(false);
            return WriteAccepted(request, allowedOrigin, correlationId, route: "none");
        }

        var consentCapturedAt = DateTimeOffset.UtcNow;
        var result = await classRegistrationService.RegisterAsync(
                new ClassRegistrationRequest(
                    tenantId,
                    correlationId,
                    sessionId,
                    parsed.Body.CustomerName,
                    parsed.Body.CustomerPhoneNumber,
                    parsed.Body.CustomerEmail)
                {
                    Source = "WebFunnel",
                    CampaignId = parsed.Body.CampaignId ?? "web-masterclass",
                    MarketingConsentGranted = parsed.Body.ConsentSms,
                    ConsentCapturedAt = parsed.Body.ConsentSms ? consentCapturedAt : null,
                    ConsentTextVersion = parsed.Body.ConsentSms ? parsed.Body.ConsentTextVersion : null,
                    SmsConsent = CreateConsentCapture(parsed.Body, "consentSms", parsed.Body.ConsentSms, consentCapturedAt),
                    EmailConsent = CreateConsentCapture(parsed.Body, "consentEmail", parsed.Body.ConsentEmail, consentCapturedAt),
                    Attributes = BuildMasterClassAttributes(parsed.Body)
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            await LogAsync("masterclass", tenantId, correlationId, result.FailureReason?.ToString() ?? "failed", cancellationToken).ConfigureAwait(false);
            var failed = responseWriter.WriteSafeError(request, MapClassStatusCode(result), CreateClassSafeError(correlationId, result));
            AddCorsHeaders(failed, allowedOrigin);
            return failed;
        }

        await LogAsync("masterclass", tenantId, correlationId, result.Duplicate ? "duplicate" : "registered", cancellationToken).ConfigureAwait(false);
        var response = responseWriter.WriteJson(
            request,
            HttpStatusCode.OK,
            new
            {
                registered = true,
                duplicate = result.Duplicate,
                sessionId = result.Session?.SessionId,
                classTitle = result.Session?.Title,
                startsAt = result.Session?.StartsAt,
                timeZone = result.Session?.TimeZone,
                correlationId
            },
            correlationId);
        AddCorsHeaders(response, allowedOrigin);
        return response;
    }

    private async Task<TenantLoadResult> LoadTenantAsync(
        string tenantId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenant = await tenantConfigurationProvider.GetTenantConfigurationAsync(tenantId, cancellationToken).ConfigureAwait(false);
            return new TenantLoadResult(tenant, HttpStatusCode.OK, null);
        }
        catch (ConfigurationException)
        {
            return new TenantLoadResult(null, HttpStatusCode.BadRequest, errorFactory.CreateBadRequest(correlationId));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync("load_tenant", tenantId, correlationId, "tenant_configuration_unavailable", cancellationToken).ConfigureAwait(false);
            return new TenantLoadResult(null, HttpStatusCode.ServiceUnavailable, errorFactory.CreateServiceUnavailable(correlationId));
        }
    }

    private static ConsultationParseResult ParseConsultation(string body)
    {
        ConsultationBody? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ConsultationBody>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return ConsultationParseResult.Invalid("malformed_json");
        }

        if (parsed is null)
        {
            return ConsultationParseResult.Invalid("malformed_json");
        }

        parsed = parsed.Normalize();
        var validation = ValidateContact(parsed);
        if (validation is not null)
        {
            return ConsultationParseResult.Invalid(validation);
        }

        if (string.IsNullOrWhiteSpace(parsed.SubmissionId) || parsed.SubmissionId.Length > 128)
        {
            return ConsultationParseResult.Invalid("invalid_submission_id");
        }

        if (parsed.CampaignId?.Length > 128
            || parsed.FunnelType?.Length > 64
            || parsed.PrimaryGoal?.Length > 128
            || parsed.Timeline?.Length > 64
            || parsed.State?.Length > 32
            || parsed.CurrentProtection?.Length > 128
            || parsed.MonthlyRange?.Length > 64
            || parsed.ExperienceLevel?.Length > 128
            || parsed.WeeklyAvailability?.Length > 64)
        {
            return ConsultationParseResult.Invalid("invalid_field_length");
        }

        return ConsultationParseResult.Valid(parsed);
    }

    private static MasterClassRegistrationParseResult ParseMasterClassRegistration(string body)
    {
        MasterClassRegistrationBody? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MasterClassRegistrationBody>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return MasterClassRegistrationParseResult.Invalid("malformed_json");
        }

        if (parsed is null)
        {
            return MasterClassRegistrationParseResult.Invalid("malformed_json");
        }

        parsed = parsed.Normalize();
        var validation = ValidateContact(parsed);
        if (validation is not null)
        {
            return MasterClassRegistrationParseResult.Invalid(validation);
        }

        if (parsed.CampaignId?.Length > 128
            || parsed.PrimaryGoal?.Length > 128
            || parsed.Timeline?.Length > 64
            || parsed.State?.Length > 32)
        {
            return MasterClassRegistrationParseResult.Invalid("invalid_field_length");
        }

        return MasterClassRegistrationParseResult.Valid(parsed);
    }

    private static string? ValidateContact(PublicFunnelContactBody body)
    {
        if (string.IsNullOrWhiteSpace(body.CustomerName))
        {
            return "missing_name";
        }

        if (body.CustomerName.Length > 200)
        {
            return "name_too_long";
        }

        if (string.IsNullOrWhiteSpace(body.CustomerPhoneNumber) && string.IsNullOrWhiteSpace(body.CustomerEmail))
        {
            return "missing_contact_identifier";
        }

        if (body.CustomerPhoneNumber?.Length > 40 || body.CustomerEmail?.Length > 320)
        {
            return "contact_identifier_too_long";
        }

        if (body.ConsentTextVersion?.Length > 128 || body.CompanyWebsiteConfirm?.Length > 500)
        {
            return "invalid_field_length";
        }

        if (body.EffectiveConsentDisclosureText?.Length > 2000
            || body.ConsentSmsDisclosureText?.Length > 2000
            || body.ConsentEmailDisclosureText?.Length > 2000)
        {
            return "invalid_field_length";
        }

        return null;
    }

    private static ChannelConsentCapture CreateConsentCapture(
        PublicFunnelContactBody body,
        string sourceField,
        bool granted,
        DateTimeOffset capturedAt)
    {
        var disclosure = string.Equals(sourceField, "consentSms", StringComparison.Ordinal)
            ? body.ConsentSmsDisclosureText ?? body.EffectiveConsentDisclosureText
            : body.ConsentEmailDisclosureText ?? body.EffectiveConsentDisclosureText;
        return new ChannelConsentCapture(
            granted,
            sourceField,
            disclosure ?? string.Empty,
            body.ConsentTextVersion ?? string.Empty,
            capturedAt,
            "WebFunnel");
    }

    private static IReadOnlyDictionary<string, string> BuildConsultationAttributes(ConsultationBody body)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sourcePage"] = "consultation",
            ["triggerType"] = "web_form",
            ["funnelType"] = body.FunnelType ?? "financial_education",
            ["requestedNextStep"] = "consultation"
        };
        AddIfPresent(attributes, "primaryGoal", body.PrimaryGoal);
        AddIfPresent(attributes, "timeline", body.Timeline);
        AddIfPresent(attributes, "state", body.State);
        AddIfPresent(attributes, "currentProtection", body.CurrentProtection);
        AddIfPresent(attributes, "monthlyRange", body.MonthlyRange);
        AddIfPresent(attributes, "experienceLevel", body.ExperienceLevel);
        AddIfPresent(attributes, "weeklyAvailability", body.WeeklyAvailability);
        return attributes;
    }

    private static IReadOnlyDictionary<string, string> BuildMasterClassAttributes(MasterClassRegistrationBody body)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sourcePage"] = "masterclass_register",
            ["funnelType"] = body.FunnelType ?? "financial_education",
            ["requestedNextStep"] = "master_class"
        };
        AddIfPresent(attributes, "primaryGoal", body.PrimaryGoal);
        AddIfPresent(attributes, "timeline", body.Timeline);
        AddIfPresent(attributes, "state", body.State);
        return attributes;
    }

    private static void AddIfPresent(IDictionary<string, string> attributes, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            attributes[name] = value.Trim();
        }
    }

    private static string? GetAllowedOrigin(HttpRequestData request, TenantConfiguration tenant)
    {
        var origin = request.GetHeaderValue("Origin")?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        var configured = tenant.Classes?.AllowedRegistrationOrigins ?? [];
        return DefaultAllowedOrigins
            .Concat(configured)
            .Select(value => value.Trim().TrimEnd('/'))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => string.Equals(value, origin, StringComparison.OrdinalIgnoreCase))
                ? origin
                : null;
    }

    private static bool AllowRequest(string key, int limit)
    {
        var now = DateTimeOffset.UtcNow;
        var counter = RateLimits.GetOrAdd(key, _ => new RateLimitCounter(now, 0));
        lock (counter)
        {
            if (now - counter.WindowStartedAt >= TimeSpan.FromMinutes(1))
            {
                counter.WindowStartedAt = now;
                counter.Count = 0;
            }

            counter.Count++;
            return counter.Count <= limit;
        }
    }

    private static HttpResponseData WritePreflight(HttpRequestData request, string? allowedOrigin, string correlationId)
    {
        if (allowedOrigin is null)
        {
            return request.CreateResponse(HttpStatusCode.Forbidden);
        }

        var response = request.CreateResponse(HttpStatusCode.NoContent);
        AddCorsHeaders(response, allowedOrigin);
        return response;
    }

    private HttpResponseData WriteValidationFailure(
        HttpRequestData request,
        string allowedOrigin,
        string correlationId,
        string validationResult)
    {
        var response = responseWriter.WriteJson(
            request,
            HttpStatusCode.BadRequest,
            new
            {
                received = false,
                validationResult,
                correlationId
            },
            correlationId);
        AddCorsHeaders(response, allowedOrigin);
        return response;
    }

    private HttpResponseData WriteAccepted(HttpRequestData request, string allowedOrigin, string correlationId, string route)
    {
        var response = responseWriter.WriteJson(
            request,
            HttpStatusCode.OK,
            new
            {
                received = true,
                route,
                correlationId
            },
            correlationId);
        AddCorsHeaders(response, allowedOrigin);
        return response;
    }

    private SafeErrorResponse CreateClassSafeError(string correlationId, ClassRegistrationResult result) =>
        result.FailureReason is ClassFailureReason.CrmWriteFailed or ClassFailureReason.StorageFailure
            ? errorFactory.CreateServiceUnavailable(correlationId)
            : errorFactory.CreateBadRequest(correlationId);

    private static HttpStatusCode MapClassStatusCode(ClassRegistrationResult result)
    {
        if (result.Succeeded)
        {
            return HttpStatusCode.OK;
        }

        return result.FailureReason switch
        {
            ClassFailureReason.MissingSession => HttpStatusCode.NotFound,
            ClassFailureReason.CapacityReached => HttpStatusCode.Conflict,
            ClassFailureReason.CrmWriteFailed or ClassFailureReason.StorageFailure => HttpStatusCode.ServiceUnavailable,
            _ => HttpStatusCode.BadRequest
        };
    }

    private static bool IsDependencyFailure(string? failureCode) =>
        failureCode is "dependency_unavailable" or "crm_upsert_failed" or "timeline_write_failed" or "consent_audit_failed" or "processing_failed";

    private static bool IsOptions(HttpRequestData request) =>
        string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase);

    private static void AddCorsHeaders(HttpResponseData response, string allowedOrigin)
    {
        response.Headers.Add("Access-Control-Allow-Origin", allowedOrigin);
        response.Headers.Add("Vary", "Origin");
        response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, x-correlation-id");
    }

    private async Task LogAsync(
        string funnel,
        string tenantId,
        string correlationId,
        string outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger.LogEventAsync(
                    TelemetryEventNames.ApiRequestCompleted,
                    new SafeTelemetryProperties()
                        .Add("tenantId", tenantId)
                        .Add("correlationId", correlationId)
                        .Add("endpoint", "public_funnel")
                        .Add("funnel", funnel)
                        .Add("outcome", outcome)
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Telemetry is best-effort.
        }
    }

    private sealed record TenantLoadResult(TenantConfiguration? Tenant, HttpStatusCode StatusCode, SafeErrorResponse? Error);

    private sealed record ConsultationParseResult(ConsultationBody? Body, string ValidationResult)
    {
        public static ConsultationParseResult Valid(ConsultationBody body) => new(body, string.Empty);

        public static ConsultationParseResult Invalid(string validationResult) => new(null, validationResult);
    }

    private sealed record MasterClassRegistrationParseResult(MasterClassRegistrationBody? Body, string ValidationResult)
    {
        public static MasterClassRegistrationParseResult Valid(MasterClassRegistrationBody body) => new(body, string.Empty);

        public static MasterClassRegistrationParseResult Invalid(string validationResult) => new(null, validationResult);
    }

    private abstract record PublicFunnelContactBody(
        string? CustomerName,
        string? Name,
        string? CustomerPhoneNumber,
        string? PhoneNumber,
        string? CustomerEmail,
        string? Email,
        bool MarketingConsentGranted,
        bool ConsentSms,
        bool ConsentEmail,
        string? ConsentTextVersion,
        string? CompanyWebsiteConfirm)
    {
        public string CustomerName { get; init; } = CustomerName ?? Name ?? string.Empty;

        public string? CustomerPhoneNumber { get; init; } = CustomerPhoneNumber ?? PhoneNumber;

        public string? CustomerEmail { get; init; } = CustomerEmail ?? Email;

        public string CompanyWebsiteConfirm { get; init; } = CompanyWebsiteConfirm ?? string.Empty;

        public string? ConsentDisclosureText { get; init; }

        public string? ConsentSmsDisclosureText { get; init; }

        public string? ConsentEmailDisclosureText { get; init; }

        public string? EffectiveConsentDisclosureText =>
            ConsentDisclosureText ?? ConsentSmsDisclosureText ?? ConsentEmailDisclosureText;
    }

    private sealed record ConsultationBody(
        string? SubmissionId,
        string? CustomerName,
        string? Name,
        string? CustomerPhoneNumber,
        string? PhoneNumber,
        string? CustomerEmail,
        string? Email,
        bool MarketingConsentGranted,
        bool ConsentSms,
        bool ConsentEmail,
        string? ConsentTextVersion,
        string? CompanyWebsiteConfirm,
        string? CampaignId,
        string? FunnelType,
        string? PrimaryGoal,
        string? Timeline,
        string? State,
        string? CurrentProtection,
        string? MonthlyRange,
        string? ExperienceLevel,
        string? WeeklyAvailability)
        : PublicFunnelContactBody(
            CustomerName,
            Name,
            CustomerPhoneNumber,
            PhoneNumber,
            CustomerEmail,
            Email,
            MarketingConsentGranted,
            ConsentSms,
            ConsentEmail,
            ConsentTextVersion,
            CompanyWebsiteConfirm)
    {
        public ConsultationBody Normalize() =>
            this with
            {
                SubmissionId = NormalizeValue(SubmissionId),
                CustomerName = NormalizeValue(CustomerName) ?? string.Empty,
                CustomerPhoneNumber = NormalizeValue(CustomerPhoneNumber),
                CustomerEmail = NormalizeValue(CustomerEmail),
                ConsentTextVersion = NormalizeValue(ConsentTextVersion),
                ConsentDisclosureText = NormalizeValue(ConsentDisclosureText),
                ConsentSmsDisclosureText = NormalizeValue(ConsentSmsDisclosureText),
                ConsentEmailDisclosureText = NormalizeValue(ConsentEmailDisclosureText),
                CompanyWebsiteConfirm = NormalizeValue(CompanyWebsiteConfirm) ?? string.Empty,
                CampaignId = NormalizeValue(CampaignId),
                FunnelType = NormalizeValue(FunnelType),
                PrimaryGoal = NormalizeValue(PrimaryGoal),
                Timeline = NormalizeValue(Timeline),
                State = NormalizeValue(State),
                CurrentProtection = NormalizeValue(CurrentProtection),
                MonthlyRange = NormalizeValue(MonthlyRange),
                ExperienceLevel = NormalizeValue(ExperienceLevel),
                WeeklyAvailability = NormalizeValue(WeeklyAvailability)
            };
    }

    private sealed record MasterClassRegistrationBody(
        string? CustomerName,
        string? Name,
        string? CustomerPhoneNumber,
        string? PhoneNumber,
        string? CustomerEmail,
        string? Email,
        bool MarketingConsentGranted,
        bool ConsentSms,
        bool ConsentEmail,
        string? ConsentTextVersion,
        string? CompanyWebsiteConfirm,
        string? CampaignId,
        string? FunnelType,
        string? PrimaryGoal,
        string? Timeline,
        string? State)
        : PublicFunnelContactBody(
            CustomerName,
            Name,
            CustomerPhoneNumber,
            PhoneNumber,
            CustomerEmail,
            Email,
            MarketingConsentGranted,
            ConsentSms,
            ConsentEmail,
            ConsentTextVersion,
            CompanyWebsiteConfirm)
    {
        public MasterClassRegistrationBody Normalize() =>
            this with
            {
                CustomerName = NormalizeValue(CustomerName) ?? string.Empty,
                CustomerPhoneNumber = NormalizeValue(CustomerPhoneNumber),
                CustomerEmail = NormalizeValue(CustomerEmail),
                ConsentTextVersion = NormalizeValue(ConsentTextVersion),
                ConsentDisclosureText = NormalizeValue(ConsentDisclosureText),
                ConsentSmsDisclosureText = NormalizeValue(ConsentSmsDisclosureText),
                ConsentEmailDisclosureText = NormalizeValue(ConsentEmailDisclosureText),
                CompanyWebsiteConfirm = NormalizeValue(CompanyWebsiteConfirm) ?? string.Empty,
                CampaignId = NormalizeValue(CampaignId),
                FunnelType = NormalizeValue(FunnelType),
                PrimaryGoal = NormalizeValue(PrimaryGoal),
                Timeline = NormalizeValue(Timeline),
                State = NormalizeValue(State)
            };
    }

    private static string? NormalizeValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class RateLimitCounter(DateTimeOffset windowStartedAt, int count)
    {
        public DateTimeOffset WindowStartedAt { get; set; } = windowStartedAt;

        public int Count { get; set; } = count;
    }
}
