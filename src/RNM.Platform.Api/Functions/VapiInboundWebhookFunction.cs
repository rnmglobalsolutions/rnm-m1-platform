using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Observability;
using RNM.Platform.Api.Security;
using RNM.Platform.Api.Voice;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Inbound;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Tenancy;
using RNM.Platform.Contracts.Voice;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Api.Functions;

public sealed class VapiInboundWebhookFunction
{
    private const string BookHvacAppointmentToolName = "book_hvac_appointment";
    private const string CheckHvacAvailabilityToolName = "check_hvac_availability";
    private const int MaxAvailabilitySuggestions = 3;

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
                    return WriteToolResult(
                        request,
                        parseResult.Envelope.ToolCall.Name,
                        parseResult.Envelope.ToolCall.ToolCallId,
                        correlationId,
                        tenantContext.TenantId,
                        inboundCallEvent.EventType.ToString(),
                        unsupportedToolOutcome,
                        processed: false,
                        bookingSucceeded: false,
                        crmSucceeded: false,
                        confirmationSucceeded: false);
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
                        workflowResult.BookingSucceeded,
                        workflowResult.CrmSucceeded,
                        workflowResult.ConfirmationSucceeded);
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
                    workflowResult.BookingSucceeded,
                    workflowResult.CrmSucceeded,
                    workflowResult.ConfirmationSucceeded);
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
        bool bookingSucceeded,
        bool crmSucceeded,
        bool confirmationSucceeded)
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
                bookingSucceeded,
                crmSucceeded,
                confirmationSucceeded
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

    private HttpResponseData WriteToolResult(
        HttpRequestData request,
        string? toolName,
        string? toolCallId,
        string correlationId,
        string tenantId,
        string eventType,
        string outcome,
        bool processed,
        bool bookingSucceeded,
        bool crmSucceeded,
        bool confirmationSucceeded)
    {
        var toolResult = JsonSerializer.Serialize(new
        {
            accepted = true,
            processed,
            correlationId,
            tenantId,
            eventType,
            outcome,
            bookingSucceeded,
            crmSucceeded,
            confirmationSucceeded
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
            timezone = timeZone,
            firstAvailableSlot,
            suggestedSlots,
            messageForAssistant = CreateAvailabilityMessage(availabilityFound, hasPreferredWindow, firstAvailableSlot?.label)
        };
    }

    private static AvailabilitySlotResponse ToAvailabilitySlotResponse(AvailableSlot slot, string timeZone)
    {
        var startsAt = ToTenantLocalTime(slot.StartsAt, timeZone);
        var endsAt = ToTenantLocalTime(slot.EndsAt, timeZone);

        return new AvailabilitySlotResponse(
            string.IsNullOrWhiteSpace(slot.SlotId) ? startsAt.ToString("O") : slot.SlotId,
            startsAt.ToString("O"),
            endsAt.ToString("O"),
            string.IsNullOrWhiteSpace(slot.Label)
                ? startsAt.ToString("dddd, MMMM d 'at' h:mm tt")
                : slot.Label);
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
        bool availabilityFound,
        bool hasPreferredWindow,
        string? firstSlotLabel)
    {
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

    private static bool IsSupportedToolCall(StructuredActionRequest? actionRequest)
    {
        return string.Equals(actionRequest?.Name, BookHvacAppointmentToolName, StringComparison.Ordinal)
            || string.Equals(actionRequest?.Name, CheckHvacAvailabilityToolName, StringComparison.Ordinal);
    }

    private static bool IsAvailabilityToolCall(StructuredActionRequest? actionRequest)
    {
        return string.Equals(actionRequest?.Name, CheckHvacAvailabilityToolName, StringComparison.Ordinal);
    }

    private static bool IsDirectApiRequestToolCall(VapiWebhookEnvelope envelope)
    {
        return string.Equals(envelope.RawEventType, "api-request", StringComparison.Ordinal)
            && (string.Equals(envelope.ToolCall?.Name, BookHvacAppointmentToolName, StringComparison.Ordinal)
                || string.Equals(envelope.ToolCall?.Name, CheckHvacAvailabilityToolName, StringComparison.Ordinal));
    }

    private static bool RequiresPreferredWindow(StructuredActionRequest? actionRequest)
    {
        return !string.Equals(GetActionArgument(actionRequest, "availabilityMode"), "earliest", StringComparison.OrdinalIgnoreCase);
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
}
