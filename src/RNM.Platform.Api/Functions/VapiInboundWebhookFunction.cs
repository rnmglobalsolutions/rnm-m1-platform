using System.Net;
using System.Globalization;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Observability;
using RNM.Platform.Api.Security;
using RNM.Platform.Api.Voice;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Inbound;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Qualification;
using RNM.Platform.Application.Tenancy;
using RNM.Platform.Contracts.Voice;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Functions;

public sealed class VapiInboundWebhookFunction
{
    private const string BookAppointmentToolName = "book_appointment";
    private const string LegacyBookHvacAppointmentToolName = "book_hvac_appointment";
    private const string CheckAvailabilityToolName = "check_availability";
    private const string LegacyCheckHvacAvailabilityToolName = "check_hvac_availability";
    private const string RecordContactConsentToolName = "record_contact_consent";
    private const string MarketingConsentScope = "sms_and_outbound_calls";
    private const int MaxAvailabilitySuggestions = 3;
    private static readonly string[] UrgentSignals =
    [
        "urgent",
        "emergency",
        "asap",
        "same day",
        "today",
        "no cooling",
        "no heat",
        "safety"
    ];

    private static readonly string[] NonUrgentSignals =
    [
        "not urgent",
        "non urgent",
        "non-urgent",
        "nonurgent",
        "routine",
        "maintenance",
        "quote",
        "estimate"
    ];

    private readonly TenantResolver tenantResolver;
    private readonly VapiWebhookValidator vapiWebhookValidator;
    private readonly ISecretProvider secretProvider;
    private readonly SafeErrorResponseFactory safeErrorResponseFactory;
    private readonly SafeHttpResponseWriter responseWriter;
    private readonly CorrelationContextFactory correlationContextFactory;
    private readonly IEventLogger eventLogger;
    private readonly VapiWebhookPayloadParser payloadParser;
    private readonly VapiWebhookMapper webhookMapper;
    private readonly IInboundCallEventProcessor inboundCallEventProcessor;
    private readonly IInboundBookingWorkflow inboundBookingWorkflow;
    private readonly CrmApplicationService crmApplicationService;
    private readonly LimitedRequestBodyReader requestBodyReader;
    private readonly VapiWebhookOptions options;

    public VapiInboundWebhookFunction(
        TenantResolver tenantResolver,
        VapiWebhookValidator vapiWebhookValidator,
        ISecretProvider secretProvider,
        SafeErrorResponseFactory safeErrorResponseFactory,
        SafeHttpResponseWriter responseWriter,
        CorrelationContextFactory correlationContextFactory,
        IEventLogger eventLogger,
        VapiWebhookPayloadParser payloadParser,
        VapiWebhookMapper webhookMapper,
        IInboundCallEventProcessor inboundCallEventProcessor,
        IInboundBookingWorkflow inboundBookingWorkflow,
        CrmApplicationService crmApplicationService,
        LimitedRequestBodyReader requestBodyReader,
        VapiWebhookOptions options)
    {
        this.tenantResolver = tenantResolver;
        this.vapiWebhookValidator = vapiWebhookValidator;
        this.secretProvider = secretProvider;
        this.safeErrorResponseFactory = safeErrorResponseFactory;
        this.responseWriter = responseWriter;
        this.correlationContextFactory = correlationContextFactory;
        this.eventLogger = eventLogger;
        this.payloadParser = payloadParser;
        this.webhookMapper = webhookMapper;
        this.inboundCallEventProcessor = inboundCallEventProcessor;
        this.inboundBookingWorkflow = inboundBookingWorkflow;
        this.crmApplicationService = crmApplicationService;
        this.requestBodyReader = requestBodyReader;
        this.options = options;
    }

    [Function("VapiInboundWebhook")]
    public async Task<HttpResponseData> Handle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tenants/{tenantId}/webhooks/vapi/inbound")] HttpRequestData request,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var correlationContext = correlationContextFactory.FromRequest(request);
        var correlationId = correlationContext.Value;
        TenantContext? tenantContext = null;

        try
        {
            await LogWebhookAsync(TelemetryEventNames.WebhookReceived, correlationId, tenantId, null, "vapi", "received", cancellationToken)
                .ConfigureAwait(false);

            tenantContext = await tenantResolver.ResolveAsync(tenantId, cancellationToken).ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.TenantResolved, correlationId, null, tenantContext.TenantId, "vapi", "resolved", cancellationToken)
                .ConfigureAwait(false);

            var webhookSecret = await secretProvider
                .GetSecretAsync(tenantContext.SecretNames.VoiceWebhookSecret, cancellationToken)
                .ConfigureAwait(false);
            var bodyReadResult = await requestBodyReader
                .ReadAsStringAsync(request, options.MaxBodyBytes, cancellationToken)
                .ConfigureAwait(false);
            if (bodyReadResult.IsTooLarge)
            {
                await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, null, tenantContext.TenantId, "vapi", "payload_too_large", cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.RequestEntityTooLarge,
                    safeErrorResponseFactory.CreatePayloadTooLarge(correlationId));
            }

            var rawBody = bodyReadResult.Body;

            var isValid = vapiWebhookValidator.IsValidBearerToken(request.GetHeaderValue("Authorization"), webhookSecret)
                || vapiWebhookValidator.IsValidLegacySecret(request.GetHeaderValue("X-Vapi-Secret"), webhookSecret)
                || vapiWebhookValidator.IsValidHmacSha256(rawBody, request.GetHeaderValue("x-signature"), webhookSecret);

            if (!isValid)
            {
                await LogWebhookAsync(TelemetryEventNames.WebhookValidationFailed, correlationId, null, tenantContext.TenantId, "vapi", "invalid_signature", cancellationToken)
                    .ConfigureAwait(false);
                await LogWebhookAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, null, tenantContext.TenantId, "vapi", "invalid_signature", cancellationToken)
                    .ConfigureAwait(false);
                await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, null, tenantContext.TenantId, "vapi", "unauthorized", cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.Unauthorized,
                    safeErrorResponseFactory.CreateUnauthorized(correlationId));
            }

            await LogWebhookAsync(TelemetryEventNames.WebhookValidationSucceeded, correlationId, null, tenantContext.TenantId, "vapi", "valid", cancellationToken)
                .ConfigureAwait(false);

            var parseResult = payloadParser.Parse(rawBody, DateTimeOffset.UtcNow);
            if (!parseResult.IsValid || parseResult.Envelope is null)
            {
                await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, null, tenantContext.TenantId, "vapi", parseResult.ErrorCode ?? "invalid_payload", cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteSafeError(
                    request,
                    HttpStatusCode.BadRequest,
                    safeErrorResponseFactory.CreateBadRequest(correlationId));
            }

            var inboundCallEvent = webhookMapper.Map(parseResult.Envelope, tenantContext, correlationId);
            if (inboundCallEvent.EventType is InboundCallEventType.Unsupported)
            {
                var unsupportedResult = InboundCallEventProcessingResult.IgnoredUnsupported();
                await LogVoiceEventAsync(
                        TelemetryEventNames.VoiceEventUnsupported,
                        correlationId,
                        tenantContext.TenantId,
                        parseResult.Envelope.RawEventType,
                        inboundCallEvent.EventType.ToString(),
                        unsupportedResult.Outcome,
                        cancellationToken)
                    .ConfigureAwait(false);

                await LogWebhookAsync(TelemetryEventNames.ApiRequestCompleted, correlationId, null, tenantContext.TenantId, "vapi", unsupportedResult.Outcome, cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteJson(
                    request,
                    HttpStatusCode.Accepted,
                    new
                    {
                        accepted = unsupportedResult.Accepted,
                        processed = unsupportedResult.Processed,
                        correlationId,
                        tenantId = tenantContext.TenantId,
                        eventType = inboundCallEvent.EventType.ToString(),
                        outcome = unsupportedResult.Outcome
                    },
                    correlationId);
            }

            if (inboundCallEvent.EventType is not InboundCallEventType.ActionRequested)
            {
                var processingResult = await inboundCallEventProcessor
                    .ProcessAsync(inboundCallEvent, cancellationToken)
                    .ConfigureAwait(false);

                await LogVoiceEventAsync(
                        TelemetryEventNames.VoiceEventProcessed,
                        correlationId,
                        tenantContext.TenantId,
                        parseResult.Envelope.RawEventType,
                        inboundCallEvent.EventType.ToString(),
                        processingResult.Outcome,
                        cancellationToken)
                    .ConfigureAwait(false);

                await LogWebhookAsync(TelemetryEventNames.ApiRequestCompleted, correlationId, null, tenantContext.TenantId, "vapi", processingResult.Outcome, cancellationToken)
                    .ConfigureAwait(false);

                return responseWriter.WriteJson(
                    request,
                    HttpStatusCode.Accepted,
                    new
                    {
                        accepted = processingResult.Accepted,
                        processed = processingResult.Processed,
                        correlationId,
                        tenantId = tenantContext.TenantId,
                        eventType = inboundCallEvent.EventType.ToString(),
                        outcome = processingResult.Outcome
                    },
                    correlationId);
            }

            if (!IsSupportedToolCall(inboundCallEvent.ActionRequest))
            {
                const string unsupportedToolOutcome = "ignored_unsupported_tool";

                await LogVoiceEventAsync(
                        TelemetryEventNames.VoiceEventUnsupported,
                        correlationId,
                        tenantContext.TenantId,
                        parseResult.Envelope.RawEventType,
                        inboundCallEvent.EventType.ToString(),
                        unsupportedToolOutcome,
                        cancellationToken)
                    .ConfigureAwait(false);

                await LogWebhookAsync(TelemetryEventNames.ApiRequestCompleted, correlationId, null, tenantContext.TenantId, "vapi", unsupportedToolOutcome, cancellationToken)
                    .ConfigureAwait(false);

                if (parseResult.Envelope.ToolCall is not null)
                {
                    return WriteUnsupportedToolResult(
                        request,
                        parseResult.Envelope.ToolCall.Name,
                        parseResult.Envelope.ToolCall.ToolCallId,
                        correlationId,
                        tenantContext.TenantId,
                        inboundCallEvent.EventType.ToString(),
                        unsupportedToolOutcome);
                }

                return responseWriter.WriteJson(
                    request,
                    HttpStatusCode.Accepted,
                    new
                    {
                        accepted = true,
                        processed = false,
                        correlationId,
                        tenantId = tenantContext.TenantId,
                        eventType = inboundCallEvent.EventType.ToString(),
                        outcome = unsupportedToolOutcome
                    },
                    correlationId);
            }

            var isAvailabilityToolCall = IsAvailabilityToolCall(inboundCallEvent.ActionRequest);
            var isConsentToolCall = IsConsentToolCall(inboundCallEvent.ActionRequest);
            await LogToolCallReceivedAsync(
                    correlationId,
                    tenantContext.TenantId,
                    parseResult.Envelope.RawEventType,
                    inboundCallEvent.EventType.ToString(),
                    inboundCallEvent.ActionRequest,
                    IsDirectApiRequestToolCall(parseResult.Envelope),
                    isAvailabilityToolCall,
                    cancellationToken)
                .ConfigureAwait(false);

            if (isConsentToolCall)
            {
                var consentResult = await crmApplicationService
                    .RecordInboundMarketingConsentAsync(
                        CreateMarketingConsentRequest(inboundCallEvent),
                        cancellationToken)
                    .ConfigureAwait(false);
                var consentOutcome = consentResult.Succeeded ? "Completed" : "Failed";

                await LogVoiceEventAsync(
                        TelemetryEventNames.VoiceEventProcessed,
                        correlationId,
                        tenantContext.TenantId,
                        parseResult.Envelope.RawEventType,
                        inboundCallEvent.EventType.ToString(),
                        consentOutcome,
                        cancellationToken)
                    .ConfigureAwait(false);

                await LogWebhookAsync(
                        consentResult.Succeeded ? TelemetryEventNames.ApiRequestCompleted : TelemetryEventNames.ApiRequestFailed,
                        correlationId,
                        null,
                        tenantContext.TenantId,
                        "vapi",
                        consentOutcome,
                        cancellationToken)
                    .ConfigureAwait(false);

                await LogConsentToolCallRespondedAsync(
                        correlationId,
                        tenantContext.TenantId,
                        parseResult.Envelope.RawEventType,
                        inboundCallEvent.EventType.ToString(),
                        inboundCallEvent.ActionRequest,
                        consentResult,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (parseResult.Envelope.ToolCall is not null)
                {
                    if (IsDirectApiRequestToolCall(parseResult.Envelope))
                    {
                        return WriteDirectConsentToolResult(
                            request,
                            consentResult.Succeeded ? HttpStatusCode.OK : HttpStatusCode.InternalServerError,
                            correlationId,
                            tenantContext.TenantId,
                            inboundCallEvent.EventType.ToString(),
                            consentOutcome,
                            consentResult);
                    }

                    return WriteConsentToolResult(
                        request,
                        parseResult.Envelope.ToolCall.Name,
                        parseResult.Envelope.ToolCall.ToolCallId,
                        correlationId,
                        tenantContext.TenantId,
                        inboundCallEvent.EventType.ToString(),
                        consentOutcome,
                        consentResult);
                }

                return responseWriter.WriteJson(
                    request,
                    consentResult.Succeeded ? HttpStatusCode.Accepted : HttpStatusCode.InternalServerError,
                    CreateConsentResponsePayload(
                        correlationId,
                        tenantContext.TenantId,
                        inboundCallEvent.EventType.ToString(),
                        consentOutcome,
                        consentResult),
                    correlationId);
            }

            var workflowResult = await inboundBookingWorkflow
                .ProcessAsync(
                    CreateWorkflowRequest(inboundCallEvent, isAvailabilityToolCall),
                    cancellationToken)
                .ConfigureAwait(false);
            var apiTelemetryEventName = workflowResult.Outcome is InboundBookingWorkflowOutcome.Failed
                ? TelemetryEventNames.ApiRequestFailed
                : TelemetryEventNames.ApiRequestCompleted;
            var responseStatusCode = workflowResult.Outcome is InboundBookingWorkflowOutcome.Failed
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.Accepted;
            var workflowOutcome = workflowResult.Outcome.ToString();
            var processed = workflowResult.WorkflowCompleted;

            await LogVoiceEventAsync(
                    TelemetryEventNames.VoiceEventProcessed,
                    correlationId,
                    tenantContext.TenantId,
                    parseResult.Envelope.RawEventType,
                    inboundCallEvent.EventType.ToString(),
                    workflowOutcome,
                    cancellationToken)
                .ConfigureAwait(false);

            await LogWebhookAsync(apiTelemetryEventName, correlationId, null, tenantContext.TenantId, "vapi", workflowOutcome, cancellationToken)
                .ConfigureAwait(false);
            await LogToolCallRespondedAsync(
                    correlationId,
                    tenantContext.TenantId,
                    parseResult.Envelope.RawEventType,
                    inboundCallEvent.EventType.ToString(),
                    inboundCallEvent.ActionRequest,
                    isAvailabilityToolCall,
                    workflowResult,
                    HasPreferredWindow(inboundCallEvent.ActionRequest),
                    cancellationToken)
                .ConfigureAwait(false);

            if (parseResult.Envelope.ToolCall is not null)
            {
                if (IsDirectApiRequestToolCall(parseResult.Envelope))
                {
                    if (isAvailabilityToolCall)
                    {
                        return WriteDirectAvailabilityToolResult(
                            request,
                            workflowResult.Outcome is InboundBookingWorkflowOutcome.Failed
                                ? HttpStatusCode.InternalServerError
                                : HttpStatusCode.OK,
                            correlationId,
                            tenantContext.TenantId,
                            tenantContext.TimeZone,
                            inboundCallEvent.EventType.ToString(),
                            workflowOutcome,
                            processed,
                            workflowResult,
                            HasPreferredWindow(inboundCallEvent.ActionRequest));
                    }

                    return WriteDirectToolResult(
                        request,
                        workflowResult.Outcome is InboundBookingWorkflowOutcome.Failed
                            ? HttpStatusCode.InternalServerError
                            : HttpStatusCode.OK,
                        correlationId,
                        tenantContext.TenantId,
                        inboundCallEvent.EventType.ToString(),
                        workflowOutcome,
                        processed,
                        workflowResult);
                }

                if (isAvailabilityToolCall)
                {
                    return WriteAvailabilityToolResult(
                        request,
                        parseResult.Envelope.ToolCall.Name,
                        parseResult.Envelope.ToolCall.ToolCallId,
                        correlationId,
                        tenantContext.TenantId,
                        tenantContext.TimeZone,
                        inboundCallEvent.EventType.ToString(),
                        workflowOutcome,
                        processed,
                        workflowResult,
                        HasPreferredWindow(inboundCallEvent.ActionRequest));
                }

                return WriteToolResult(
                    request,
                    parseResult.Envelope.ToolCall.Name,
                    parseResult.Envelope.ToolCall.ToolCallId,
                    correlationId,
                    tenantContext.TenantId,
                    inboundCallEvent.EventType.ToString(),
                    workflowOutcome,
                    processed,
                    workflowResult);
            }

            return responseWriter.WriteJson(
                request,
                responseStatusCode,
                new
                {
                    accepted = true,
                    processed,
                    correlationId,
                    tenantId = tenantContext.TenantId,
                    eventType = inboundCallEvent.EventType.ToString(),
                    outcome = workflowOutcome
                },
                correlationId);
        }
        catch (ConfigurationException)
        {
            await LogWebhookAsync(TelemetryEventNames.TenantResolutionFailed, correlationId, tenantId, null, "vapi", "configuration_error", cancellationToken)
                .ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantId, null, "vapi", "bad_request", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
        catch (TenantResolutionException)
        {
            await LogWebhookAsync(TelemetryEventNames.TenantResolutionFailed, correlationId, tenantId, null, "vapi", "tenant_resolution_error", cancellationToken)
                .ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantId, null, "vapi", "bad_request", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.BadRequest,
                safeErrorResponseFactory.CreateBadRequest(correlationId));
        }
        catch (SecretRetrievalException)
        {
            await LogWebhookAsync(TelemetryEventNames.SecurityAuthFailed, correlationId, tenantContext is null ? tenantId : null, tenantContext?.TenantId, "vapi", "secret_unavailable", cancellationToken)
                .ConfigureAwait(false);
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantContext is null ? tenantId : null, tenantContext?.TenantId, "vapi", "unauthorized", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.Unauthorized,
                safeErrorResponseFactory.CreateUnauthorized(correlationId));
        }
        catch
        {
            await LogWebhookAsync(TelemetryEventNames.ApiRequestFailed, correlationId, tenantContext is null ? tenantId : null, tenantContext?.TenantId, "vapi", "dependency_failure", cancellationToken)
                .ConfigureAwait(false);

            return responseWriter.WriteSafeError(
                request,
                HttpStatusCode.InternalServerError,
                safeErrorResponseFactory.CreateInternalServerError(correlationId));
        }
    }

    private HttpResponseData WriteDirectToolResult(
        HttpRequestData request,
        HttpStatusCode statusCode,
        string correlationId,
        string tenantId,
        string eventType,
        string outcome,
        bool processed,
        InboundBookingWorkflowResult workflowResult)
    {
        return responseWriter.WriteJson(
            request,
            statusCode,
            new
            {
                accepted = true,
                processed,
                correlationId,
                tenantId,
                eventType,
                outcome,
                bookingSucceeded = workflowResult.BookingSucceeded,
                crmSucceeded = workflowResult.CrmSucceeded,
                confirmationSucceeded = workflowResult.ConfirmationSucceeded,
                bookingState = workflowResult.BookingState?.ToString(),
                messageForAssistant = CreateBookingMessage(workflowResult)
            },
            correlationId);
    }

    private HttpResponseData WriteDirectAvailabilityToolResult(
        HttpRequestData request,
        HttpStatusCode statusCode,
        string correlationId,
        string tenantId,
        string timeZone,
        string eventType,
        string outcome,
        bool processed,
        InboundBookingWorkflowResult workflowResult,
        bool hasPreferredWindow)
    {
        return responseWriter.WriteJson(
            request,
            statusCode,
            CreateAvailabilityResponsePayload(
                correlationId,
                tenantId,
                timeZone,
                eventType,
                outcome,
                processed,
                workflowResult,
                hasPreferredWindow),
            correlationId);
    }

    private HttpResponseData WriteDirectConsentToolResult(
        HttpRequestData request,
        HttpStatusCode statusCode,
        string correlationId,
        string tenantId,
        string eventType,
        string outcome,
        CrmMarketingConsentResult consentResult)
    {
        return responseWriter.WriteJson(
            request,
            statusCode,
            CreateConsentResponsePayload(
                correlationId,
                tenantId,
                eventType,
                outcome,
                consentResult),
            correlationId);
    }

    private HttpResponseData WriteToolResult(
        HttpRequestData request,
        string? toolName,
        string? toolCallId,
        string correlationId,
        string tenantId,
        string eventType,
        string outcome,
        bool processed,
        InboundBookingWorkflowResult workflowResult)
    {
        var toolResult = JsonSerializer.Serialize(new
        {
            accepted = true,
            processed,
            correlationId,
            tenantId,
            eventType,
            outcome,
            bookingSucceeded = workflowResult.BookingSucceeded,
            crmSucceeded = workflowResult.CrmSucceeded,
            confirmationSucceeded = workflowResult.ConfirmationSucceeded,
            bookingState = workflowResult.BookingState?.ToString(),
            messageForAssistant = CreateBookingMessage(workflowResult)
        });

        return responseWriter.WriteJson(
            request,
            HttpStatusCode.OK,
            new
            {
                results = new[]
                {
                    new
                    {
                        name = toolName,
                        toolCallId,
                        result = toolResult
                    }
                }
            },
            correlationId);
    }

    private HttpResponseData WriteUnsupportedToolResult(
        HttpRequestData request,
        string? toolName,
        string? toolCallId,
        string correlationId,
        string tenantId,
        string eventType,
        string outcome)
    {
        var toolResult = JsonSerializer.Serialize(new
        {
            accepted = true,
            processed = false,
            correlationId,
            tenantId,
            eventType,
            outcome,
            bookingSucceeded = false,
            crmSucceeded = false,
            confirmationSucceeded = false,
            messageForAssistant = "This tool is not supported. Continue safely without exposing internal tool details."
        });

        return responseWriter.WriteJson(
            request,
            HttpStatusCode.OK,
            new
            {
                results = new[]
                {
                    new
                    {
                        name = toolName,
                        toolCallId,
                        result = toolResult
                    }
                }
            },
            correlationId);
    }

    private HttpResponseData WriteConsentToolResult(
        HttpRequestData request,
        string? toolName,
        string? toolCallId,
        string correlationId,
        string tenantId,
        string eventType,
        string outcome,
        CrmMarketingConsentResult consentResult)
    {
        var toolResult = JsonSerializer.Serialize(CreateConsentResponsePayload(
            correlationId,
            tenantId,
            eventType,
            outcome,
            consentResult));

        return responseWriter.WriteJson(
            request,
            HttpStatusCode.OK,
            new
            {
                results = new[]
                {
                    new
                    {
                        name = toolName,
                        toolCallId,
                        result = toolResult
                    }
                }
            },
            correlationId);
    }

    private HttpResponseData WriteAvailabilityToolResult(
        HttpRequestData request,
        string? toolName,
        string? toolCallId,
        string correlationId,
        string tenantId,
        string timeZone,
        string eventType,
        string outcome,
        bool processed,
        InboundBookingWorkflowResult workflowResult,
        bool hasPreferredWindow)
    {
        var toolResult = JsonSerializer.Serialize(CreateAvailabilityResponsePayload(
            correlationId,
            tenantId,
            timeZone,
            eventType,
            outcome,
            processed,
            workflowResult,
            hasPreferredWindow));

        return responseWriter.WriteJson(
            request,
            HttpStatusCode.OK,
            new
            {
                results = new[]
                {
                    new
                    {
                        name = toolName,
                        toolCallId,
                        result = toolResult
                    }
                }
            },
            correlationId);
    }

    private static object CreateAvailabilityResponsePayload(
        string correlationId,
        string tenantId,
        string timeZone,
        string eventType,
        string outcome,
        bool processed,
        InboundBookingWorkflowResult workflowResult,
        bool hasPreferredWindow)
    {
        var suggestedSlots = workflowResult.AvailableSlots
            .OrderBy(slot => slot.StartsAt)
            .Take(MaxAvailabilitySuggestions)
            .Select(slot => ToAvailabilitySlotResponse(slot, timeZone))
            .ToArray();
        var availabilityFound = suggestedSlots.Length > 0;
        var firstAvailableSlot = suggestedSlots.FirstOrDefault();

        return new
        {
            accepted = true,
            processed,
            correlationId,
            tenantId,
            eventType,
            outcome,
            availabilityFound,
            requestedWindowAvailable = hasPreferredWindow ? availabilityFound : (bool?)null,
            availabilityModeUsed = hasPreferredWindow ? "preferred_window" : "earliest",
            qualificationState = workflowResult.QualificationState?.ToString(),
            serviceAreaState = workflowResult.ServiceAreaState?.ToString(),
            bookingState = workflowResult.BookingState?.ToString(),
            bookingFailureReason = workflowResult.BookingFailureReason?.ToString(),
            availableSlotCount = suggestedSlots.Length,
            timezone = timeZone,
            firstAvailableSlot,
            selectedSlotId = firstAvailableSlot?.selectedSlotId,
            selectedSlotStart = firstAvailableSlot?.selectedSlotStart,
            selectedSlotEnd = firstAvailableSlot?.selectedSlotEnd,
            selectedSlotLabel = firstAvailableSlot?.selectedSlotLabel,
            suggestedSlots,
            messageForAssistant = CreateAvailabilityMessage(workflowResult, availabilityFound, hasPreferredWindow, firstAvailableSlot?.label)
        };
    }

    private static object CreateConsentResponsePayload(
        string correlationId,
        string tenantId,
        string eventType,
        string outcome,
        CrmMarketingConsentResult consentResult)
    {
        return new
        {
            accepted = true,
            processed = consentResult.Succeeded,
            correlationId,
            tenantId,
            eventType,
            outcome,
            consentRecorded = consentResult.Succeeded,
            consentStatus = consentResult.ConsentStatus,
            contactUpdated = consentResult.ContactUpdated,
            timelineEventType = consentResult.TimelineEventType,
            failureReason = consentResult.FailureReason?.ToString(),
            messageForAssistant = CreateConsentMessage(consentResult)
        };
    }

    private static AvailabilitySlotResponse ToAvailabilitySlotResponse(AvailableSlot slot, string timeZone)
    {
        var startsAt = ToTenantLocalTime(slot.StartsAt, timeZone);
        var endsAt = ToTenantLocalTime(slot.EndsAt, timeZone);
        var label = CreateSpokenSlotLabel(startsAt, timeZone);

        return new AvailabilitySlotResponse(
            string.IsNullOrWhiteSpace(slot.SlotId) ? startsAt.ToString("O") : slot.SlotId,
            startsAt.ToString("O"),
            endsAt.ToString("O"),
            label);
    }

    private static string CreateSpokenSlotLabel(DateTimeOffset startsAt, string timeZone)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{startsAt:dddd, MMMM d, yyyy 'at' h:mm tt} {timeZone}");
    }

    private static DateTimeOffset ToTenantLocalTime(DateTimeOffset value, string timeZone)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        }
        catch (TimeZoneNotFoundException)
        {
            return value;
        }
        catch (InvalidTimeZoneException)
        {
            return value;
        }
    }

    private static string CreateAvailabilityMessage(
        InboundBookingWorkflowResult workflowResult,
        bool availabilityFound,
        bool hasPreferredWindow,
        string? firstSlotLabel)
    {
        if (workflowResult.QualificationState is QualificationResultState.MissingRequiredFields)
        {
            return "Required details are missing. Ask only for the missing required detail, then check availability again.";
        }

        if (workflowResult.QualificationState is QualificationResultState.InvalidInput)
        {
            return "One or more required details are invalid. Ask the caller to correct the unclear detail, then check availability again.";
        }

        if (workflowResult.QualificationState is QualificationResultState.OutOfServiceArea)
        {
            return "M1 determined the service address is outside the configured service area. Offer human follow-up.";
        }

        if (workflowResult.QualificationState is QualificationResultState.NeedsEscalation)
        {
            return "M1 determined this caller should be escalated. Offer human follow-up.";
        }

        if (workflowResult.Outcome is InboundBookingWorkflowOutcome.Failed)
        {
            return "M1 could not complete the availability check. Offer human follow-up and do not invent availability.";
        }

        if (workflowResult.BookingState is BookingDecisionState.Failed)
        {
            return workflowResult.BookingFailureReason is BookingFailureReason.AdapterFailure
                ? "M1 could not reach or use the booking availability provider. Offer human follow-up and do not describe this as no availability."
                : "M1 could not complete the availability check. Offer human follow-up and do not invent availability.";
        }

        if (!availabilityFound)
        {
            return hasPreferredWindow
                ? "The requested appointment window does not appear to be available. Ask the caller for another preferred day and time."
                : "No appointment availability was found. Offer human follow-up.";
        }

        return hasPreferredWindow
            ? $"The requested window has availability. Ask the caller to confirm {firstSlotLabel}."
            : $"The earliest available appointment is {firstSlotLabel}. Ask the caller if that works for them before booking.";
    }

    private static string CreateBookingMessage(InboundBookingWorkflowResult workflowResult)
    {
        if (workflowResult.BookingSucceeded)
        {
            return workflowResult.ConfirmationSucceeded
                ? "The appointment is booked. Tell the caller they will receive confirmation by SMS and email."
                : "The appointment is booked, but confirmation delivery did not fully succeed. Tell the caller the office may follow up with confirmation details.";
        }

        return workflowResult.BookingState switch
        {
            BookingDecisionState.AvailabilityFound => "A confirmed slot was not provided. Ask the caller to accept one exact slot returned by M1 before booking.",
            BookingDecisionState.NoAvailability => "No appointment availability was found. Offer human follow-up and do not keep the caller waiting on a transfer.",
            BookingDecisionState.Failed => "Booking failed safely. Offer human follow-up and do not claim the appointment is booked.",
            BookingDecisionState.Refused => "The lead is not eligible for booking with the current details. Ask only for missing or corrected information, or offer human follow-up.",
            _ => "Booking was not completed. Offer human follow-up and do not claim the appointment is booked."
        };
    }

    private static string CreateConsentMessage(CrmMarketingConsentResult consentResult)
    {
        if (!consentResult.Succeeded)
        {
            return "M1 could not record the consent preference. Continue the booking flow and do not claim marketing consent was saved.";
        }

        return consentResult.TimelineEventType switch
        {
            CrmTimelineEventTypes.MarketingConsentInboundCallGranted => "Marketing follow-up consent was recorded. Continue naturally.",
            CrmTimelineEventTypes.MarketingConsentReversedFromOptOut => "Marketing follow-up consent was explicitly restored from a prior opt-out during this inbound call. Continue naturally.",
            CrmTimelineEventTypes.MarketingConsentInboundCallBlockedOptedOut => "The contact is opted out. Do not send marketing follow-up or outbound calls.",
            _ => "The caller declined marketing follow-up consent. Continue naturally without marking the contact as opted in."
        };
    }

    private static bool IsSupportedToolCall(StructuredActionRequest? actionRequest)
    {
        return IsBookingToolCall(actionRequest)
            || IsAvailabilityToolCall(actionRequest)
            || string.Equals(actionRequest?.Name, RecordContactConsentToolName, StringComparison.Ordinal);
    }

    private static bool IsBookingToolCall(StructuredActionRequest? actionRequest)
    {
        return IsBookingToolName(actionRequest?.Name);
    }

    private static bool IsAvailabilityToolCall(StructuredActionRequest? actionRequest)
    {
        return IsAvailabilityToolName(actionRequest?.Name);
    }

    private static bool IsConsentToolCall(StructuredActionRequest? actionRequest)
    {
        return string.Equals(actionRequest?.Name, RecordContactConsentToolName, StringComparison.Ordinal);
    }

    private static bool IsDirectApiRequestToolCall(VapiWebhookEnvelope envelope)
    {
        return string.Equals(envelope.RawEventType, "api-request", StringComparison.Ordinal)
            && (IsBookingToolName(envelope.ToolCall?.Name)
                || IsAvailabilityToolName(envelope.ToolCall?.Name)
                || string.Equals(envelope.ToolCall?.Name, RecordContactConsentToolName, StringComparison.Ordinal));
    }

    private static bool IsBookingToolName(string? toolName)
    {
        return string.Equals(toolName, BookAppointmentToolName, StringComparison.Ordinal)
            || string.Equals(toolName, LegacyBookHvacAppointmentToolName, StringComparison.Ordinal);
    }

    private static bool IsAvailabilityToolName(string? toolName)
    {
        return string.Equals(toolName, CheckAvailabilityToolName, StringComparison.Ordinal)
            || string.Equals(toolName, LegacyCheckHvacAvailabilityToolName, StringComparison.Ordinal);
    }

    private static string GetToolNameVariant(StructuredActionRequest? actionRequest)
    {
        return string.Equals(actionRequest?.Name, LegacyBookHvacAppointmentToolName, StringComparison.Ordinal)
            || string.Equals(actionRequest?.Name, LegacyCheckHvacAvailabilityToolName, StringComparison.Ordinal)
                ? "legacy"
                : "current";
    }

    private static bool RequiresPreferredWindow(StructuredActionRequest? actionRequest)
    {
        if (string.Equals(GetActionArgument(actionRequest, "availabilityMode"), "earliest", StringComparison.OrdinalIgnoreCase)
            || IsUrgentAction(actionRequest)
            || string.IsNullOrWhiteSpace(GetActionArgument(actionRequest, "preferredTime")))
        {
            return false;
        }

        return true;
    }

    private static bool IsUrgentAction(StructuredActionRequest? actionRequest)
    {
        var urgency = GetActionArgument(actionRequest, "urgency");
        if (ContainsAny(urgency, NonUrgentSignals))
        {
            return false;
        }

        return ContainsAny(urgency, UrgentSignals)
            || ContainsAny(GetActionArgument(actionRequest, "serviceNeed"), UrgentSignals);
    }

    private static bool ContainsAny(string? value, IReadOnlyCollection<string> signals)
    {
        return !string.IsNullOrWhiteSpace(value)
            && signals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeAvailabilityModeForTelemetry(StructuredActionRequest? actionRequest)
    {
        var mode = GetActionArgument(actionRequest, "availabilityMode");
        if (string.Equals(mode, "earliest", StringComparison.OrdinalIgnoreCase))
        {
            return "earliest";
        }

        if (string.Equals(mode, "preferred_window", StringComparison.OrdinalIgnoreCase))
        {
            return "preferred_window";
        }

        return string.IsNullOrWhiteSpace(mode) ? "missing" : "other";
    }

    private static bool HasPreferredWindow(StructuredActionRequest? actionRequest)
    {
        return !string.IsNullOrWhiteSpace(GetActionArgument(actionRequest, "preferredTime"));
    }

    private static InboundBookingWorkflowRequest CreateWorkflowRequest(
        InboundCallEvent inboundCallEvent,
        bool isAvailabilityToolCall)
    {
        if (isAvailabilityToolCall)
        {
            return new InboundBookingWorkflowRequest(
                inboundCallEvent,
                AutoSelectFirstAvailableSlot: false,
                RequirePreferredWindow: RequiresPreferredWindow(inboundCallEvent.ActionRequest));
        }

        return new InboundBookingWorkflowRequest(
            inboundCallEvent,
            SelectedSlot: TryCreateConfirmedSelectedSlot(inboundCallEvent.ActionRequest),
            AutoSelectFirstAvailableSlot: false,
            RequirePreferredWindow: true);
    }

    private static CrmMarketingConsentRequest CreateMarketingConsentRequest(InboundCallEvent inboundCallEvent)
    {
        return new CrmMarketingConsentRequest(
            inboundCallEvent.TenantId,
            inboundCallEvent.VerticalId,
            inboundCallEvent.CorrelationId,
            GetActionArgument(inboundCallEvent.ActionRequest, "channelScope") ?? MarketingConsentScope,
            GetActionArgumentBoolean(inboundCallEvent.ActionRequest, "granted"))
        {
            Source = "InboundVoice",
            ProviderCallId = GetActionArgument(inboundCallEvent.ActionRequest, "capturedDuringCallId")
                ?? inboundCallEvent.Session.ProviderCallId,
            PhoneNumber = GetActionArgument(inboundCallEvent.ActionRequest, "phoneNumber")
                ?? inboundCallEvent.Session.CallerPhoneNumber,
            Email = GetActionArgument(inboundCallEvent.ActionRequest, "email"),
            Name = GetActionArgument(inboundCallEvent.ActionRequest, "name"),
            ZipCode = GetActionArgument(inboundCallEvent.ActionRequest, "zipCode"),
            CapturedAt = inboundCallEvent.ReceivedAtUtc,
            IsPersonInitiatedInbound = true
        };
    }

    private static AvailableSlot? TryCreateConfirmedSelectedSlot(StructuredActionRequest? actionRequest)
    {
        if (!GetActionArgumentBoolean(actionRequest, "customerConfirmedSlot"))
        {
            return null;
        }

        var slotId = GetActionArgument(actionRequest, "selectedSlotId");
        var label = GetActionArgument(actionRequest, "selectedSlotLabel");
        var startsAtValue = GetActionArgument(actionRequest, "selectedSlotStart")
            ?? GetActionArgument(actionRequest, "startsAt");
        var endsAtValue = GetActionArgument(actionRequest, "selectedSlotEnd")
            ?? GetActionArgument(actionRequest, "endsAt");

        if (!DateTimeOffset.TryParse(startsAtValue, out var startsAt)
            || !DateTimeOffset.TryParse(endsAtValue, out var endsAt)
            || endsAt <= startsAt)
        {
            return null;
        }

        return new AvailableSlot(
            string.IsNullOrWhiteSpace(slotId) ? null : slotId.Trim(),
            startsAt.ToUniversalTime(),
            endsAt.ToUniversalTime(),
            label);
    }

    private static bool GetActionArgumentBoolean(StructuredActionRequest? actionRequest, string fieldName)
    {
        var value = GetActionArgument(actionRequest, fieldName);
        return bool.TryParse(value, out var parsed)
            ? parsed
            : string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetActionArgument(StructuredActionRequest? actionRequest, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(actionRequest?.ArgumentsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(actionRequest.ArgumentsJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty(fieldName, out var property))
            {
                return null;
            }

            return property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Number => property.GetRawText(),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record AvailabilitySlotResponse(
        string slotId,
        string startsAt,
        string endsAt,
        string label)
    {
        public string selectedSlotId => slotId;

        public string selectedSlotStart => startsAt;

        public string selectedSlotEnd => endsAt;

        public string selectedSlotLabel => label;
    }

    private Task LogWebhookAsync(
        string eventName,
        string correlationId,
        string? routeTenantId,
        string? tenantId,
        string provider,
        string outcome,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", correlationId)
            .Add("endpoint", "webhooks/vapi/inbound")
            .Add("provider", provider)
            .Add("outcome", outcome)
            .AddIf(!string.IsNullOrWhiteSpace(routeTenantId), "routeTenantId", routeTenantId)
            .AddIf(!string.IsNullOrWhiteSpace(tenantId), "tenantId", tenantId)
            .ToDictionary();

        return eventLogger.TryLogEventAsync(eventName, properties, cancellationToken);
    }

    private Task LogVoiceEventAsync(
        string eventName,
        string correlationId,
        string tenantId,
        string providerEventType,
        string platformEventType,
        string outcome,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", correlationId)
            .Add("endpoint", "webhooks/vapi/inbound")
            .Add("provider", "vapi")
            .Add("tenantId", tenantId)
            .Add("providerEventType", providerEventType)
            .Add("platformEventType", platformEventType)
            .Add("outcome", outcome)
            .ToDictionary();

        return eventLogger.TryLogEventAsync(eventName, properties, cancellationToken);
    }

    private Task LogToolCallReceivedAsync(
        string correlationId,
        string tenantId,
        string providerEventType,
        string platformEventType,
        StructuredActionRequest? actionRequest,
        bool isDirectApiRequest,
        bool isAvailabilityToolCall,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", correlationId)
            .Add("endpoint", "webhooks/vapi/inbound")
            .Add("provider", "vapi")
            .Add("tenantId", tenantId)
            .Add("providerEventType", providerEventType)
            .Add("platformEventType", platformEventType)
            .Add("toolName", actionRequest?.Name ?? "unknown")
            .Add("toolNameVariant", GetToolNameVariant(actionRequest))
            .Add("directApiRequest", ToBooleanString(isDirectApiRequest))
            .Add("isAvailabilityToolCall", ToBooleanString(isAvailabilityToolCall))
            .Add("availabilityMode", NormalizeAvailabilityModeForTelemetry(actionRequest))
            .Add("preferredTimeProvided", ToBooleanString(HasPreferredWindow(actionRequest)))
            .Add("urgentDetected", ToBooleanString(IsUrgentAction(actionRequest)))
            .Add("requiresPreferredWindow", ToBooleanString(RequiresPreferredWindow(actionRequest)))
            .ToDictionary();

        return eventLogger.TryLogEventAsync(TelemetryEventNames.VoiceToolCallReceived, properties, cancellationToken);
    }

    private Task LogToolCallRespondedAsync(
        string correlationId,
        string tenantId,
        string providerEventType,
        string platformEventType,
        StructuredActionRequest? actionRequest,
        bool isAvailabilityToolCall,
        InboundBookingWorkflowResult workflowResult,
        bool hasPreferredWindow,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", correlationId)
            .Add("endpoint", "webhooks/vapi/inbound")
            .Add("provider", "vapi")
            .Add("tenantId", tenantId)
            .Add("providerEventType", providerEventType)
            .Add("platformEventType", platformEventType)
            .Add("toolName", actionRequest?.Name ?? "unknown")
            .Add("toolNameVariant", GetToolNameVariant(actionRequest))
            .Add("isAvailabilityToolCall", ToBooleanString(isAvailabilityToolCall))
            .Add("availabilityModeUsed", hasPreferredWindow ? "preferred_window" : "earliest")
            .Add("outcome", workflowResult.Outcome.ToString())
            .AddIf(workflowResult.QualificationState is not null, "qualificationState", workflowResult.QualificationState?.ToString())
            .AddIf(workflowResult.ServiceAreaState is not null, "serviceAreaState", workflowResult.ServiceAreaState?.ToString())
            .AddIf(workflowResult.BookingState is not null, "bookingState", workflowResult.BookingState?.ToString())
            .AddIf(workflowResult.BookingFailureReason is not null, "bookingFailureReason", workflowResult.BookingFailureReason?.ToString())
            .AddIf(workflowResult.CrmState is not null, "crmState", workflowResult.CrmState?.ToString())
            .AddIf(workflowResult.ConfirmationState is not null, "confirmationState", workflowResult.ConfirmationState?.ToString())
            .Add("availableSlotCount", workflowResult.AvailableSlots.Count.ToString(CultureInfo.InvariantCulture))
            .Add("selectedSlotReturned", ToBooleanString(workflowResult.AvailableSlots.Count > 0))
            .ToDictionary();

        return eventLogger.TryLogEventAsync(TelemetryEventNames.VoiceToolCallResponded, properties, cancellationToken);
    }

    private Task LogConsentToolCallRespondedAsync(
        string correlationId,
        string tenantId,
        string providerEventType,
        string platformEventType,
        StructuredActionRequest? actionRequest,
        CrmMarketingConsentResult consentResult,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", correlationId)
            .Add("endpoint", "webhooks/vapi/inbound")
            .Add("provider", "vapi")
            .Add("tenantId", tenantId)
            .Add("providerEventType", providerEventType)
            .Add("platformEventType", platformEventType)
            .Add("toolName", actionRequest?.Name ?? "unknown")
            .Add("toolNameVariant", GetToolNameVariant(actionRequest))
            .Add("outcome", consentResult.Succeeded ? "Completed" : "Failed")
            .Add("consentStatus", consentResult.ConsentStatus)
            .Add("contactUpdated", ToBooleanString(consentResult.ContactUpdated))
            .AddIf(!string.IsNullOrWhiteSpace(consentResult.TimelineEventType), "timelineEventType", consentResult.TimelineEventType)
            .AddIf(consentResult.FailureReason is not null, "failureReason", consentResult.FailureReason?.ToString())
            .ToDictionary();

        return eventLogger.TryLogEventAsync(TelemetryEventNames.VoiceToolCallResponded, properties, cancellationToken);
    }

    private static string ToBooleanString(bool value) => value ? bool.TrueString : bool.FalseString;
}
