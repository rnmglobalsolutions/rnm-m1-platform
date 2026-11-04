using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Api.Voice;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class VapiCallTracingTests
{
    private const string Url = "https://platform.example.com/api/tenants/tenant-a/webhooks/vapi/inbound";

    [Fact]
    public async Task ToolCall_EveryEventAfterParseCarriesCallId()
    {
        var logger = new CallAwareEventLogger();
        var function = CreateFunction(logger);

        var response = await Send(function, ToolCallBody("call-123", "check_availability"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEventsAfterParseCarryCallId(logger, "call-123");
        Assert.Contains(logger.Events, e => e.EventName == TelemetryEventNames.VoiceToolCallReceived);
        Assert.Contains(logger.Events, e => e.EventName == TelemetryEventNames.VoiceToolCallResponded);
    }

    [Fact]
    public async Task AvailabilityThenBooking_ShareCallIdWithDifferentCorrelationIds()
    {
        var logger = new CallAwareEventLogger();
        var function = CreateFunction(logger);

        await Send(function, ToolCallBody("call-xyz", "check_availability"));
        var firstCount = logger.Events.Count;
        await Send(function, ToolCallBody("call-xyz", "book_appointment"));

        var first = logger.Events.Take(firstCount).Where(e => e.Properties.ContainsKey("callId")).ToList();
        var second = logger.Events.Skip(firstCount).Where(e => e.Properties.ContainsKey("callId")).ToList();
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.All(first.Concat(second), e => Assert.Equal("call-xyz", e.Properties["callId"]));
        var firstCorrelation = first.Select(e => e.Properties["correlationId"]).Distinct().Single();
        var secondCorrelation = second.Select(e => e.Properties["correlationId"]).Distinct().Single();
        Assert.NotEqual(firstCorrelation, secondCorrelation);
    }

    [Fact]
    public async Task ConsentToolCall_CrmEventsCarryCallId()
    {
        var logger = new CallAwareEventLogger();
        var crm = new EndpointTelemetryTests.RecordingCrmAdapter();
        var function = CreateFunction(logger, crm);
        var body = """
            {
              "message": {
                "type": "tool-calls",
                "call": { "id": "call-consent" },
                "toolCallList": [
                  {
                    "id": "tool-consent-1",
                    "name": "record_contact_consent",
                    "arguments": {
                      "name": "Jane Customer",
                      "phoneNumber": "+15551234567",
                      "email": "jane@example.com",
                      "zipCode": "75001",
                      "channelScope": "sms_and_outbound_calls",
                      "granted": true,
                      "capturedDuringCallId": "LLM-FILLED-ID"
                    }
                  }
                ]
              }
            }
            """;

        await Send(function, body);

        Assert.Contains(logger.Events, e => e.EventName.StartsWith("crm.", StringComparison.Ordinal));
        AssertEventsAfterParseCarryCallId(logger, "call-consent");
        Assert.DoesNotContain(logger.Events, e => e.Properties.Values.Contains("LLM-FILLED-ID"));
    }

    [Fact]
    public async Task EndOfCallReport_UnsupportedEventCarriesCallId()
    {
        var logger = new CallAwareEventLogger();
        var function = CreateFunction(logger);
        var body = """
            { "message": { "type": "end-of-call-report", "call": { "id": "call-eoc" } } }
            """;

        await Send(function, body);

        var voiceEvents = logger.Events.Where(e =>
            e.EventName is TelemetryEventNames.VoiceEventUnsupported or TelemetryEventNames.VoiceEventProcessed).ToList();
        Assert.NotEmpty(voiceEvents);
        Assert.All(voiceEvents, e => Assert.Equal("call-eoc", e.Properties["callId"]));
    }

    [Fact]
    public async Task InvalidSignature_NoEventCarriesCallId()
    {
        var logger = new CallAwareEventLogger();
        var function = CreateFunction(logger);
        var request = new TestHttpRequestData("POST", Url, ToolCallBody("call-123", "check_availability"));
        request.Headers.Add("Authorization", "Bearer wrong-secret");

        var response = (TestHttpResponseData)await function.Handle(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEmpty(logger.Events);
        Assert.All(logger.Events, e => Assert.False(e.Properties.ContainsKey("callId")));
    }

    [Fact]
    public async Task InvalidCallIdInPayload_IsIgnored()
    {
        var logger = new CallAwareEventLogger();
        var function = CreateFunction(logger);

        await Send(function, ToolCallBody("bad call id", "check_availability"));

        Assert.All(logger.Events, e => Assert.False(e.Properties.ContainsKey("callId")));
    }

    [Fact]
    public async Task WorkflowThrows_DependencyFailureCarriesCallId()
    {
        var logger = new CallAwareEventLogger();
        var function = CreateFunction(logger, workflow: new RecordingInboundBookingWorkflow(throwOnProcess: true));

        var response = await Send(function, ToolCallBody("call-boom", "check_availability"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var failure = Assert.Single(logger.Events, e =>
            e.EventName == TelemetryEventNames.ApiRequestFailed
            && e.Properties.Values.Contains("dependency_failure"));
        Assert.Equal("call-boom", failure.Properties["callId"]);
    }

    [Fact]
    public async Task AfterHandle_ContextIsCleared()
    {
        var function = CreateFunction(new CallAwareEventLogger());

        await Send(function, ToolCallBody("call-123", "check_availability"));

        Assert.Null(CallTelemetryContext.CurrentCallId);
    }

    [Fact]
    public async Task ConcurrentRequests_DoNotMixCallIds()
    {
        var loggers = Enumerable.Range(0, 10).Select(_ => new CallAwareEventLogger()).ToList();

        await Task.WhenAll(loggers.Select((logger, index) =>
            Send(CreateFunction(logger), ToolCallBody($"call-{index}", "check_availability"))));

        for (var index = 0; index < loggers.Count; index++)
        {
            AssertEventsAfterParseCarryCallId(loggers[index], $"call-{index}");
        }
    }

    [Fact]
    public async Task Telemetry_NeverContainsPhoneTranscriptOrBraces()
    {
        var logger = new CallAwareEventLogger();
        var function = CreateFunction(logger);

        await Send(function, ToolCallBody("call-123", "book_appointment"));

        Assert.All(logger.Events, e => Assert.All(e.Properties.Values, value =>
        {
            Assert.DoesNotContain('{', value);
            Assert.DoesNotContain('}', value);
            Assert.DoesNotContain("+15551234567", value);
            Assert.DoesNotContain("Jane Customer", value);
        }));
    }

    private static void AssertEventsAfterParseCarryCallId(CallAwareEventLogger logger, string callId)
    {
        var afterParse = logger.Events
            .SkipWhile(e => e.EventName != TelemetryEventNames.WebhookValidationSucceeded)
            .Skip(1)
            .ToList();
        Assert.NotEmpty(afterParse);
        Assert.All(afterParse, e => Assert.Equal(callId, e.Properties["callId"]));
    }

    private static async Task<TestHttpResponseData> Send(VapiInboundWebhookFunction function, string body)
    {
        var request = new TestHttpRequestData("POST", Url, body);
        request.Headers.Add("Authorization", "Bearer expected-secret");
        return (TestHttpResponseData)await function.Handle(request, "tenant-a", CancellationToken.None);
    }

    private static string ToolCallBody(string callId, string toolName) =>
        $$"""
        {
          "message": {
            "type": "tool-calls",
            "call": { "id": "{{callId}}", "customer": { "number": "+15551234567" } },
            "toolCallList": [
              {
                "id": "tool-1",
                "name": "{{toolName}}",
                "arguments": {
                  "serviceNeed": "AC repair",
                  "propertyType": "residential",
                  "serviceAddress": "123 Main St, Addison TX 75001",
                  "zipCode": "75001",
                  "urgency": "today",
                  "availabilityMode": "earliest",
                  "preferredTime": "Friday, May 29 at 4:00 PM",
                  "selectedSlotId": "slot-1",
                  "selectedSlotStart": "2026-05-29T21:00:00Z",
                  "selectedSlotEnd": "2026-05-29T21:30:00Z",
                  "selectedSlotLabel": "Friday, May 29 at 4:00 PM",
                  "customerConfirmedSlot": true,
                  "name": "Jane Customer",
                  "phoneNumber": "+15551234567",
                  "email": "jane@example.com"
                }
              }
            ]
          }
        }
        """;

    private static VapiInboundWebhookFunction CreateFunction(
        CallAwareEventLogger logger,
        EndpointTelemetryTests.RecordingCrmAdapter? crmAdapter = null,
        RecordingInboundBookingWorkflow? workflow = null)
    {
        var options = new VapiWebhookOptions();
        return new VapiInboundWebhookFunction(
            new TenantResolver(new StubTenantConfigurationProvider(), new StubVerticalConfigurationProvider()),
            new VapiWebhookValidator(),
            new StubSecretProvider("expected-secret"),
            new SafeErrorResponseFactory(),
            new SafeHttpResponseWriter(),
            new CorrelationContextFactory(),
            logger,
            new VapiWebhookPayloadParser(options),
            new VapiWebhookMapper(),
            new RecordingInboundCallEventProcessor(),
            workflow ?? new RecordingInboundBookingWorkflow(),
            new CrmApplicationService(crmAdapter ?? new EndpointTelemetryTests.RecordingCrmAdapter(), logger),
            new LimitedRequestBodyReader(),
            options);
    }

    /// <summary>
    /// Mimics the production logger: applies the ambient call context to each event.
    /// </summary>
    private sealed class CallAwareEventLogger : IEventLogger
    {
        private readonly object gate = new();

        public List<RecordedTelemetryEvent> Events { get; } = [];

        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken)
        {
            var enriched = CallTelemetryContext.Enrich(properties);
            lock (gate)
            {
                Events.Add(new RecordedTelemetryEvent(eventName, new Dictionary<string, string>(enriched)));
            }

            return Task.CompletedTask;
        }
    }
}
