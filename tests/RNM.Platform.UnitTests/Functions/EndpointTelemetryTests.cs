using System.Net;
using System.Security.Cryptography;
using System.Text;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Api.Voice;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Inbound;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Qualification;
using RNM.Platform.Application.Tenancy;
using RNM.Platform.SharedKernel.Correlation;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class EndpointTelemetryTests
{
    [Fact]
    public async Task VapiWebhook_ValidSignature_AcceptsAndProcessesCallStartedEvent()
    {
        var eventLogger = new RecordingEventLogger();
        var workflow = new RecordingInboundBookingWorkflow();
        var processor = new RecordingInboundCallEventProcessor();
        var function = CreateVapiFunction(eventLogger, workflow: workflow, processor: processor);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """
            {
              "type": "call-started",
              "call": {
                "id": "call-123",
                "customer": { "number": "+15551234567" }
              }
            }
            """);
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("\"accepted\":true", response.ReadBody());
        Assert.Empty(workflow.Events);
        var callEvent = Assert.Single(processor.Events);
        Assert.Equal(InboundCallEventType.CallStarted, callEvent.EventType);
        Assert.Equal("call-123", callEvent.Session.ProviderCallId);
        Assert.Equal("+15551234567", callEvent.Session.CallerPhoneNumber);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.WebhookValidationSucceeded));
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.VoiceEventProcessed));
        Assert.All(eventLogger.Events, AssertNoSensitiveTelemetry);
        Assert.All(eventLogger.Events, AssertNoRawPayloadTelemetry);
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_ToolCall_ReturnsVapiToolResultsShape()
    {
        var eventLogger = new RecordingEventLogger();
        var workflow = new RecordingInboundBookingWorkflow();
        var function = CreateVapiFunction(eventLogger, workflow: workflow);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """
            {
              "message": {
                "type": "tool-calls",
                "call": {
                  "id": "call-123",
                  "customer": { "number": "+15551234567" }
                },
                "toolCallList": [
                  {
                    "id": "tool-1",
                    "name": "book_hvac_appointment",
                    "arguments": {
                      "serviceNeed": "AC repair",
                      "propertyType": "residential",
                      "serviceAddress": "123 Main St, Addison TX 75001",
                      "zipCode": "75001",
                      "urgency": "today",
                      "preferredTime": "tomorrow morning",
                      "name": "Jane Customer",
                      "phoneNumber": "+15551234567",
                      "email": "jane@example.com"
                    }
                  }
                ]
              }
            }
            """);
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        var body = response.ReadBody();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"results\"", body);
        Assert.Contains("\"toolCallId\":\"tool-1\"", body);
        Assert.Contains("bookingSucceeded", body);
        Assert.Contains("crmSucceeded", body);
        Assert.Contains("confirmationSucceeded", body);
        var callEvent = Assert.Single(workflow.Events);
        Assert.Equal(InboundCallEventType.ActionRequested, callEvent.EventType);
        Assert.Equal("book_hvac_appointment", callEvent.ActionRequest?.Name);
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_AvailabilityToolCall_ReturnsSlotsWithoutAutoBooking()
    {
        var eventLogger = new RecordingEventLogger();
        var workflow = new RecordingInboundBookingWorkflow(result: new InboundBookingWorkflowResult(
            InboundBookingWorkflowOutcome.BookingStopped,
            QualificationResultState.Qualified,
            ServiceAreaDecisionState.InServiceArea,
            BookingDecisionState.AvailabilityFound,
            CrmSyncState.Succeeded,
            ConfirmationState: null)
        {
            AvailableSlots =
            [
                new AvailableSlot(
                    "slot-1",
                    new DateTimeOffset(2026, 5, 29, 21, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 5, 29, 21, 30, 0, TimeSpan.Zero),
                    "Friday, May 29 at 4:00 PM")
            ]
        });
        var function = CreateVapiFunction(eventLogger, workflow: workflow);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """
            {
              "message": {
                "type": "tool-calls",
                "call": {
                  "id": "call-123",
                  "customer": { "number": "+15551234567" }
                },
                "toolCallList": [
                  {
                    "id": "tool-1",
                    "name": "check_hvac_availability",
                    "arguments": {
                      "serviceNeed": "AC repair",
                      "propertyType": "residential",
                      "serviceAddress": "123 Main St, Addison TX 75001",
                      "zipCode": "75001",
                      "urgency": "urgent",
                      "availabilityMode": "earliest",
                      "name": "Jane Customer",
                      "phoneNumber": "+15551234567",
                      "email": "jane@example.com"
                    }
                  }
                ]
              }
            }
            """);
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        var body = response.ReadBody();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"results\"", body);
        Assert.Contains("\"toolCallId\":\"tool-1\"", body);
        Assert.Contains("availabilityFound", body);
        Assert.Contains("firstAvailableSlot", body);
        Assert.Contains("Friday, May 29 at 4:00 PM", body);
        Assert.Contains("messageForAssistant", body);
        var workflowRequest = Assert.Single(workflow.Requests);
        Assert.False(workflowRequest.AutoSelectFirstAvailableSlot);
        Assert.False(workflowRequest.RequirePreferredWindow);
        Assert.Equal("check_hvac_availability", workflowRequest.InboundCallEvent.ActionRequest?.Name);
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_DirectApiRequestBody_ReturnsDirectToolResult()
    {
        var eventLogger = new RecordingEventLogger();
        var workflow = new RecordingInboundBookingWorkflow();
        var function = CreateVapiFunction(eventLogger, workflow: workflow);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """
            {
              "name": "Jane Customer",
              "phoneNumber": "+15551234567",
              "email": "jane@example.com",
              "serviceNeed": "AC repair",
              "propertyType": "residential",
              "serviceAddress": "123 Main St, Addison TX 75001",
              "zipCode": "75001",
              "urgency": "today",
              "preferredTime": "tomorrow morning"
            }
            """);
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        var body = response.ReadBody();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("\"results\"", body);
        Assert.Contains("\"bookingSucceeded\":true", body);
        Assert.Contains("\"crmSucceeded\":true", body);
        Assert.Contains("\"confirmationSucceeded\":true", body);
        var callEvent = Assert.Single(workflow.Events);
        Assert.Equal(InboundCallEventType.ActionRequested, callEvent.EventType);
        Assert.Equal("book_hvac_appointment", callEvent.ActionRequest?.Name);
        Assert.Equal("+15551234567", callEvent.Session.CallerPhoneNumber);
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_UnsupportedToolCall_ReturnsToolResultWithoutRunningWorkflow()
    {
        var eventLogger = new RecordingEventLogger();
        var workflow = new RecordingInboundBookingWorkflow();
        var function = CreateVapiFunction(eventLogger, workflow: workflow);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """
            {
              "message": {
                "type": "tool-calls",
                "call": { "id": "call-123" },
                "toolCallList": [
                  {
                    "id": "tool-1",
                    "name": "unknown_tool",
                    "arguments": {
                      "name": "Jane Customer"
                    }
                  }
                ]
              }
            }
            """);
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        var body = response.ReadBody();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(workflow.Events);
        Assert.Contains("\"results\"", body);
        Assert.Contains("ignored_unsupported_tool", body);
        Assert.Contains("bookingSucceeded", body);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.VoiceEventUnsupported));
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_InvalidSignature_EmitsSafeTelemetryAndSafeResponse()
    {
        var eventLogger = new RecordingEventLogger();
        var function = CreateVapiFunction(eventLogger);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            "{\"event\":\"call-started\"}");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", response.ReadBody());
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.WebhookValidationFailed));
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.SecurityAuthFailed));
        Assert.All(eventLogger.Events, AssertNoSensitiveTelemetry);

        var received = Assert.Single(eventLogger.Events, EventNamed(TelemetryEventNames.WebhookReceived));
        Assert.Equal("tenant-a", received.Properties["routeTenantId"]);
        Assert.False(received.Properties.ContainsKey("tenantId"));

        var failed = Assert.Single(eventLogger.Events, EventNamed(TelemetryEventNames.WebhookValidationFailed));
        Assert.Equal("tenant-a", failed.Properties["tenantId"]);
        Assert.False(failed.Properties.ContainsKey("routeTenantId"));
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task TwilioWebhook_InvalidSignature_EmitsSafeTelemetryAndSafeResponse()
    {
        var eventLogger = new RecordingEventLogger();
        var function = CreateTwilioFunction(eventLogger);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/twilio/sms-status",
            "MessageSid=SM123&MessageStatus=delivered");
        request.Headers.Add("X-Twilio-Signature", "invalid");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", response.ReadBody());
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.WebhookValidationFailed));
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.SecurityAuthFailed));
        Assert.All(eventLogger.Events, AssertNoSensitiveTelemetry);
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task TwilioWebhook_ValidSignature_LogsSmsStatusWithoutPhoneNumbers()
    {
        var eventLogger = new RecordingEventLogger();
        var function = CreateTwilioFunction(eventLogger);
        var body = "MessageSid=SM123&MessageStatus=delivered&To=%2B15551234567&From=%2B15550001000";
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/twilio/sms-status",
            body);
        AddValidTwilioSignature(request, body);

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("\"accepted\":true", response.ReadBody());

        var statusEvent = Assert.Single(eventLogger.Events, EventNamed(TelemetryEventNames.SmsStatusReceived));
        Assert.Equal("tenant-a", statusEvent.Properties["tenantId"]);
        Assert.Equal("twilio", statusEvent.Properties["provider"]);
        Assert.Equal("SM123", statusEvent.Properties["messageSid"]);
        Assert.Equal("delivered", statusEvent.Properties["messageStatus"]);
        Assert.DoesNotContain(statusEvent.Properties.Keys, key => string.Equals(key, "to", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(statusEvent.Properties.Keys, key => string.Equals(key, "from", StringComparison.OrdinalIgnoreCase));
        Assert.All(eventLogger.Events, AssertNoSensitiveTelemetry);
        Assert.All(eventLogger.Events, recordedEvent =>
        {
            Assert.DoesNotContain(recordedEvent.Properties.Values, value => value.Contains("+15551234567", StringComparison.Ordinal));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value => value.Contains("+15550001000", StringComparison.Ordinal));
        });
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_TenantResolutionFailure_EmitsRouteTenantTelemetryOnly()
    {
        var eventLogger = new RecordingEventLogger();
        var function = CreateVapiFunction(
            eventLogger,
            tenantResolver: new TenantResolver(
                new StubTenantConfigurationProvider(throwTenantResolutionException: true),
                new StubVerticalConfigurationProvider()));
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/missing-tenant/webhooks/vapi/inbound",
            "{}");

        var response = (TestHttpResponseData)await function
            .Handle(request, "missing-tenant", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var failed = Assert.Single(eventLogger.Events, EventNamed(TelemetryEventNames.TenantResolutionFailed));
        Assert.Equal("missing-tenant", failed.Properties["routeTenantId"]);
        Assert.False(failed.Properties.ContainsKey("tenantId"));
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_MissingTenant_IsRejected()
    {
        var eventLogger = new RecordingEventLogger();
        var function = CreateVapiFunction(eventLogger);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants//webhooks/vapi/inbound",
            "{}");
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.TenantResolutionFailed));
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_CallEndedEvent_IsParsedIntoPlatformEvent()
    {
        var workflow = new RecordingInboundBookingWorkflow();
        var processor = new RecordingInboundCallEventProcessor();
        var function = CreateVapiFunction(new RecordingEventLogger(), workflow: workflow, processor: processor);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """{"message":{"type":"call-ended","call":{"id":"call-456"}}}""");
        request.Headers.Add("X-Vapi-Secret", "expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(workflow.Events);
        var callEvent = Assert.Single(processor.Events);
        Assert.Equal(InboundCallEventType.CallEnded, callEvent.EventType);
        Assert.Equal("call-456", callEvent.Session.ProviderCallId);
    }

    [Fact]
    public async Task VapiWebhook_ValidAuthMalformedJson_ReturnsShapedBadRequest()
    {
        var eventLogger = new RecordingEventLogger();
        var workflow = new RecordingInboundBookingWorkflow();
        var function = CreateVapiFunction(eventLogger, workflow: workflow);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            "{");
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", response.ReadBody());
        Assert.Empty(workflow.Events);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.ApiRequestFailed));
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_OversizedBody_ReturnsShapedPayloadTooLarge()
    {
        var eventLogger = new RecordingEventLogger();
        var function = CreateVapiFunction(
            eventLogger,
            options: new VapiWebhookOptions { MaxBodyBytes = 16, JsonMaxDepth = 32 });
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """{"type":"call-started","callId":"call-123"}""");
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("\"code\":\"payload_too_large\"", response.ReadBody());
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.ApiRequestFailed));
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_MissingCallFields_IsHandledSafely()
    {
        var workflow = new RecordingInboundBookingWorkflow();
        var processor = new RecordingInboundCallEventProcessor();
        var function = CreateVapiFunction(new RecordingEventLogger(), workflow: workflow, processor: processor);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """{"type":"call-started"}""");
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(workflow.Events);
        var callEvent = Assert.Single(processor.Events);
        Assert.Equal(InboundCallEventType.CallStarted, callEvent.EventType);
        Assert.Null(callEvent.Session.ProviderCallId);
        Assert.Null(callEvent.Session.CallerPhoneNumber);
    }

    [Fact]
    public async Task VapiWebhook_NonStringEventType_DoesNotLeakRawJsonIntoTelemetry()
    {
        var eventLogger = new RecordingEventLogger();
        var function = CreateVapiFunction(eventLogger);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """{"type":{"nested":"call-started"},"callId":"call-123"}""");
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var voiceEvent = Assert.Single(eventLogger.Events, EventNamed(TelemetryEventNames.VoiceEventUnsupported));
        Assert.Equal("unknown", voiceEvent.Properties["providerEventType"]);
        Assert.All(eventLogger.Events, AssertNoRawPayloadTelemetry);
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_WorkflowException_ReturnsShapedInternalServerError()
    {
        var function = CreateVapiFunction(
            new RecordingEventLogger(),
            workflow: new RecordingInboundBookingWorkflow(throwOnProcess: true));
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """
            {
              "message": {
                "type": "tool-calls",
                "call": { "id": "call-123" },
                "toolCallList": [
                  {
                    "id": "tool-1",
                    "name": "book_hvac_appointment",
                    "arguments": {
                      "name": "Jane Customer",
                      "phoneNumber": "+15551234567",
                      "email": "jane@example.com",
                      "serviceNeed": "AC repair",
                      "propertyType": "residential",
                      "serviceAddress": "123 Main St, Addison TX 75001",
                      "zipCode": "75001",
                      "urgency": "today",
                      "preferredTime": "tomorrow morning"
                    }
                  }
                ]
              }
            }
            """);
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("\"code\":\"internal_error\"", response.ReadBody());
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_ProcessorException_ReturnsShapedInternalServerError()
    {
        var function = CreateVapiFunction(
            new RecordingEventLogger(),
            processor: new RecordingInboundCallEventProcessor(throwOnProcess: true));
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """{"type":"call-started","callId":"call-123"}""");
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("\"code\":\"internal_error\"", response.ReadBody());
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_SecretRetrievalFailure_ReturnsShapedUnauthorized()
    {
        var eventLogger = new RecordingEventLogger();
        var function = CreateVapiFunction(
            eventLogger,
            secretProvider: new StubSecretProvider("unused", throwOnRead: true));
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """{"type":"call-started","callId":"call-123"}""");
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", response.ReadBody());
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.SecurityAuthFailed));
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public async Task VapiWebhook_UnsupportedEvent_IsHandledSafely()
    {
        var eventLogger = new RecordingEventLogger();
        var workflow = new RecordingInboundBookingWorkflow();
        var function = CreateVapiFunction(eventLogger, workflow: workflow);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            """{"type":"status-ping","callId":"call-789"}""");
        request.Headers.Add("Authorization", "Bearer expected-secret");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("\"processed\":false", response.ReadBody());
        Assert.Empty(workflow.Events);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.VoiceEventUnsupported));
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public void Health_ReturnsHealthyJsonWithCorrelationHeader()
    {
        var function = CreateHealthFunction();
        var request = new TestHttpRequestData(
            "GET",
            "https://platform.example.com/api/health");

        var response = (TestHttpResponseData)function.Handle(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\": \"healthy\"", response.ReadBody());
        AssertValidCorrelationHeader(response);
    }

    [Fact]
    public void Health_EchoesProvidedCorrelationHeader()
    {
        var function = CreateHealthFunction();
        var request = new TestHttpRequestData(
            "GET",
            "https://platform.example.com/api/health");
        var correlationId = Guid.NewGuid().ToString("N");
        request.Headers.Add(CorrelationId.HeaderName, correlationId);

        var response = (TestHttpResponseData)function.Handle(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(correlationId, GetSingleHeader(response, CorrelationId.HeaderName));
    }

    [Fact]
    public async Task MalformedIncomingCorrelationId_IsRegeneratedAndNotEchoed()
    {
        var eventLogger = new RecordingEventLogger();
        var function = CreateVapiFunction(eventLogger);
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            "{}");
        var unsafeCorrelationId = "bad value with spaces";
        request.Headers.Add(CorrelationId.HeaderName, unsafeCorrelationId);

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        var responseCorrelationId = GetSingleHeader(response, CorrelationId.HeaderName);
        Assert.NotEqual(unsafeCorrelationId, responseCorrelationId);
        Assert.True(CorrelationId.TryNormalize(responseCorrelationId, out _));
        Assert.All(eventLogger.Events, recordedEvent =>
        {
            Assert.NotEqual(unsafeCorrelationId, recordedEvent.Properties["correlationId"]);
            Assert.True(CorrelationId.TryNormalize(recordedEvent.Properties["correlationId"], out _));
        });
    }

    [Fact]
    public async Task TelemetrySinkFailure_DoesNotBreakWebhookResponseHandling()
    {
        var function = CreateVapiFunction(new RecordingEventLogger(throwOnLog: true));
        var request = CreatePostRequest(
            "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound",
            "{}");

        var response = (TestHttpResponseData)await function
            .Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertValidCorrelationHeader(response);
    }

    private static VapiInboundWebhookFunction CreateVapiFunction(
        RecordingEventLogger eventLogger,
        TenantResolver? tenantResolver = null,
        RecordingInboundBookingWorkflow? workflow = null,
        RecordingInboundCallEventProcessor? processor = null,
        StubSecretProvider? secretProvider = null,
        VapiWebhookOptions? options = null)
    {
        options ??= new VapiWebhookOptions();

        return new VapiInboundWebhookFunction(
            tenantResolver ?? CreateTenantResolver(),
            new VapiWebhookValidator(),
            secretProvider ?? new StubSecretProvider("expected-secret"),
            new SafeErrorResponseFactory(),
            new SafeHttpResponseWriter(),
            new CorrelationContextFactory(),
            eventLogger,
            new VapiWebhookPayloadParser(options),
            new VapiWebhookMapper(),
            processor ?? new RecordingInboundCallEventProcessor(),
            workflow ?? new RecordingInboundBookingWorkflow(),
            new LimitedRequestBodyReader(),
            options);
    }

    private static TwilioSmsStatusWebhookFunction CreateTwilioFunction(RecordingEventLogger eventLogger)
    {
        return new TwilioSmsStatusWebhookFunction(
            CreateTenantResolver(),
            new TwilioSignatureValidator(),
            new StubSecretProvider("expected-secret"),
            new FormUrlEncodedBodyParser(),
            new SafeErrorResponseFactory(),
            new SafeHttpResponseWriter(),
            new CorrelationContextFactory(),
            eventLogger);
    }

    private static HealthFunction CreateHealthFunction() => new();

    private static TenantResolver CreateTenantResolver()
    {
        return new TenantResolver(
            new StubTenantConfigurationProvider(),
            new StubVerticalConfigurationProvider());
    }

    private static TestHttpRequestData CreatePostRequest(string url, string body)
    {
        return new TestHttpRequestData("POST", url, body);
    }

    private static void AddValidTwilioSignature(TestHttpRequestData request, string body)
    {
        var formValues = new FormUrlEncodedBodyParser().Parse(body);
        var signatureBase = new TwilioSignatureValidator().BuildSignatureBase(request.Url, formValues);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes("expected-secret"));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureBase)));
        request.Headers.Add("X-Twilio-Signature", signature);
    }

    private static Predicate<RecordedTelemetryEvent> EventNamed(string eventName)
    {
        return recordedEvent => recordedEvent.EventName == eventName;
    }

    private static void AssertNoSensitiveTelemetry(RecordedTelemetryEvent recordedEvent)
    {
        foreach (var property in recordedEvent.Properties)
        {
            Assert.False(SafeTelemetryProperties.IsSensitiveName(property.Key));
            Assert.DoesNotContain("expected-secret", property.Value);
            Assert.DoesNotContain("Bearer", property.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sha256=", property.Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertNoRawPayloadTelemetry(RecordedTelemetryEvent recordedEvent)
    {
        Assert.All(recordedEvent.Properties.Values, value =>
        {
            Assert.DoesNotContain('{', value);
            Assert.DoesNotContain('}', value);
            Assert.DoesNotContain("+15551234567", value);
        });
    }

    private static void AssertValidCorrelationHeader(TestHttpResponseData response)
    {
        var correlationId = GetSingleHeader(response, CorrelationId.HeaderName);
        Assert.True(CorrelationId.TryNormalize(correlationId, out _));
    }

    private static string GetSingleHeader(TestHttpResponseData response, string headerName)
    {
        Assert.True(response.Headers.TryGetValues(headerName, out var values));
        return Assert.Single(values);
    }
}
